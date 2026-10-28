using Brutal.JoltApi;
using Brutal.Numerics;
using Brutal.RakNetApi;
using Brutal.Strings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BrutalSandbox.Framework.Physics;

namespace BrutalSandbox.Framework.Network;

public struct NetworkCubeState
{
    public float3 Position;
    public float3 LinearVelocity;
    public float3 AngularVelocity;
    public floatQuat Rotation;
}

public sealed unsafe class NetworkManager : IDisposable
{
    private RakPeerInstance _peer;
    private bool _isServer;
    private bool _isActive;
    private SystemAddress _serverAddress;

    public ConcurrentDictionary<string, float3> RemotePlayerPositions { get; } = new();
    public ConcurrentDictionary<uint, NetworkCubeState> NetworkedCubesState { get; } = new();
    public ConcurrentQueue<(uint NetId, float3 Position, float Mass, float Friction, bool IsStatic, uint ParentNetId)> IncomingBuildCommands { get; } = new();
    public ConcurrentQueue<uint> IncomingDestroyCommands { get; } = new();

    private readonly ConcurrentDictionary<string, uint> _remotePhysicsBodies = new();

    public bool IsServer => _isServer;
    public bool IsActive => _isActive;

    public bool StartServer(ushort port)
    {
        RemotePlayerPositions.Clear();
        NetworkedCubesState.Clear();
        _remotePhysicsBodies.Clear();

        _peer = RakNetLibrary.CreateInstance();
        var socket = new SocketDescriptor(port);

        try
        {
            var result = _peer.Startup(32, new Span<SocketDescriptor>(ref socket));
            if (result != StartupResult.RaknetStarted)
            {
                _peer.Dispose();
                return false;
            }
        }
        catch
        {
            return false;
        }

        _peer.SetMaximumIncomingConnections(32);
        _isServer = true;
        _isActive = true;
        return true;
    }

    public void StartClient(string ip, ushort port)
    {
        RemotePlayerPositions.Clear();
        NetworkedCubesState.Clear();

        _peer = RakNetLibrary.CreateInstance();
        var socket = new SocketDescriptor(0);
        _peer.Startup(1, new Span<SocketDescriptor>(ref socket));

        byte[] ipBytes = System.Text.Encoding.UTF8.GetBytes(ip);
        ReadOnlySpan<byte> ipSpan = new ReadOnlySpan<byte>(ipBytes);

        _peer.Connect(ipSpan, port, default, null);
        _isServer = false;
        _isActive = true;
    }

    public void Update()
    {
        if (!_isActive) return;

        for (var packet = _peer.Receive(); packet != null; packet = _peer.Receive())
        {
            ProcessPacket(packet);
            _peer.DeallocatePacket(packet);
        }
    }

    private void ProcessPacket(Packet* packet)
    {
        if (packet == null || packet->Data == null || packet->Length == 0) return;

        byte packetId = packet->Data[0];
        string clientAddressKey = packet->SystemAddress.ToString();

        // Системные пакеты RakNet
        if (packetId == (byte)DefaultMessageIDTypes.ConnectionRequestAccepted)
        {
            _serverAddress = packet->SystemAddress;
            Console.WriteLine("[Client] Успешное подключение к серверу!");
            return;
        }
        if (packetId == (byte)DefaultMessageIDTypes.ConnectionLost || packetId == (byte)DefaultMessageIDTypes.DisconnectionNotification)
        {
            Console.WriteLine($"[Network] Игрок отключился: {clientAddressKey}");
            RemotePlayerPositions.TryRemove(clientAddressKey, out _);

            if (_isServer && _remotePhysicsBodies.TryRemove(clientAddressKey, out uint bodyId))
            {
                PhysicsManager.Instance.BodyInterface.RemoveBody(bodyId);
                PhysicsManager.Instance.BodyInterface.DestroyBody(bodyId);
            }
            return;
        }

        if (packetId == (byte)DefaultMessageIDTypes.UserPacketEnum)
        {
            GameMessageID gameId = (GameMessageID)packet->Data[1];

            if (gameId == GameMessageID.PlayerTransform)
            {
                if (_isServer)
                {
                    float3 position = *(float3*)(packet->Data + 2);
                    RemotePlayerPositions[clientAddressKey] = position;

                    var phys = PhysicsManager.Instance;
                    if (!_remotePhysicsBodies.TryGetValue(clientAddressKey, out uint bodyId))
                    {
                        using (var capsuleSettings = CapsuleShapeSettings.Create(0.5f, 0.4f, default))
                        {
                            bodyId = phys.CreateBody(capsuleSettings, position, floatQuat.Identity, MotionType.Dynamic, PhysicsManager.LayerMoving, "stone_wall");
                        }
                        phys.BodyInterface.SetGravityFactor(bodyId, 0f);
                        _remotePhysicsBodies[clientAddressKey] = bodyId;
                    }
                    else
                    {
                        phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 currentServerPos, out _);

                        float3 toTarget = position - currentServerPos;
                        float distSq = float3.Dot(toTarget, toTarget);

                        if (distSq > 16f)
                        {
                            phys.BodyInterface.SetPositionAndRotation(bodyId, position, floatQuat.Identity, Activation.Activate);
                            phys.BodyInterface.SetLinearVelocity(bodyId, float3.Zero);
                        }
                        else
                        {
                            float3 requiredVelocity = toTarget * 10f;

                            float maxPushSpeed = 12f;
                            float speedSq = float3.Dot(requiredVelocity, requiredVelocity);
                            if (speedSq > maxPushSpeed * maxPushSpeed)
                            {
                                requiredVelocity = float3.Normalize(requiredVelocity) * maxPushSpeed;
                            }

                            phys.BodyInterface.SetLinearVelocity(bodyId, requiredVelocity);
                        }
                    }

                    BroadcastTransform(clientAddressKey, position, packet->Guid);
                }
                else
                {
                    byte* ptr = packet->Data + 2;
                    float3 remotePos = *(float3*)(packet->Data + 66);
                    string remoteAddressStr = System.Text.Encoding.UTF8.GetString(ptr, 64);

                    RemotePlayerPositions[remoteAddressStr] = remotePos;
                }
            }
            else if (gameId == GameMessageID.SyncDynamicObjects && !_isServer)
            {
                int count = *(int*)(packet->Data + 2);
                int offset = 6;

                for (int i = 0; i < count; i++)
                {
                    uint netId = *(uint*)(packet->Data + offset);

                    NetworkCubeState state;
                    state.Position = *(float3*)(packet->Data + offset + 4);
                    state.LinearVelocity = *(float3*)(packet->Data + offset + 16);
                    state.AngularVelocity = *(float3*)(packet->Data + offset + 28);
                    state.Rotation = *(floatQuat*)(packet->Data + offset + 40);

                    NetworkedCubesState[netId] = state;
                    offset += 56;
                }
            }
            else if (gameId == GameMessageID.BuildObject)
            {
                uint netId = *(uint*)(packet->Data + 2);
                float3 pos = *(float3*)(packet->Data + 6);
                float mass = *(float*)(packet->Data + 18);
                float friction = *(float*)(packet->Data + 22);
                bool isStatic = *(bool*)(packet->Data + 26);
                uint parentNetId = *(uint*)(packet->Data + 27); // Читаем ID родительского куба для сварки

                if (_isServer)
                {
                    IncomingBuildCommands.Enqueue((netId, pos, mass, friction, isStatic, parentNetId));
                    SendBuildOrDestroyToAll(netId, pos, true, default, mass, friction, isStatic, parentNetId);
                }
                else
                {
                    IncomingBuildCommands.Enqueue((netId, pos, mass, friction, isStatic, parentNetId));
                }
            }


            else if (gameId == GameMessageID.DestroyObject)
            {
                uint netId = *(uint*)(packet->Data + 2);

                if (_isServer)
                {
                    IncomingDestroyCommands.Enqueue(netId);

                    SendBuildOrDestroyToAll(netId, float3.Zero, false, default);
                }
                else
                {
                    IncomingDestroyCommands.Enqueue(netId);
                }
            }
        }
    
    }

    public void SendMyTransform(float3 position)
    {
        if (!_isActive) return;

        if (_isServer)
        {
            byte[] buffer = new byte[2 + 64 + 12];
            buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
            buffer[1] = (byte)GameMessageID.PlayerTransform;

            fixed (byte* p = buffer)
            {
                System.Text.Encoding.UTF8.GetBytes("SERVER\0").CopyTo(new Span<byte>(p + 2, 64));
                *(float3*)(p + 66) = position;
            }

            _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.UnreliableSequenced, 0, new AddressOrGUID(new SystemAddress()), true);
        }
        else
        {
            byte[] buffer = new byte[2 + 12];
            buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
            buffer[1] = (byte)GameMessageID.PlayerTransform;

            fixed (byte* p = buffer)
            {
                *(float3*)(p + 2) = position;
            }

            _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.UnreliableSequenced, 0, new AddressOrGUID(_serverAddress), false);
        }
    }

    private void BroadcastTransform(string originAddress, float3 position, RakNetGUID excludeGuid)
    {
        byte[] buffer = new byte[2 + 64 + 12];
        buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
        buffer[1] = (byte)GameMessageID.PlayerTransform;

        fixed (byte* p = buffer)
        {
            System.Text.Encoding.UTF8.GetBytes(originAddress + "\0").CopyTo(new Span<byte>(p + 2, 64));
            *(float3*)(p + 66) = position;
        }

        _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.UnreliableSequenced, 0, new AddressOrGUID(excludeGuid), true);
    }
    public void SendBuildOrDestroyToAll(uint netId, float3 pos, bool isBuild, RakNetGUID excludeGuid, float mass = 1.0f, float friction = 0.2f, bool isStatic = false, uint parentNetId = 0)
    {
        if (!_isActive) return;

        byte[] buffer = isBuild ? new byte[2 + 4 + 12 + 4 + 4 + 1 + 4] : new byte[2 + 4];
        buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
        buffer[1] = (byte)(isBuild ? GameMessageID.BuildObject : GameMessageID.DestroyObject);

        fixed (byte* p = buffer)
        {
            *(uint*)(p + 2) = netId;
            if (isBuild)
            {
                *(float3*)(p + 6) = pos;
                *(float*)(p + 18) = mass;
                *(float*)(p + 22) = friction;
                *(bool*)(p + 26) = isStatic;
                *(uint*)(p + 27) = parentNetId;
            }
        }

        _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, new AddressOrGUID(excludeGuid), _isServer);
    }


    public void SendRawPacket(byte[] buffer, bool broadcast)
    {
        if (!_isActive) return;
        _peer.Send(buffer, PacketPriority.MediumPriority, PacketReliability.UnreliableSequenced, 0, broadcast ? new AddressOrGUID(new SystemAddress()) : new AddressOrGUID(_serverAddress), broadcast);
    }

    public void Dispose()
    {
        if (_isActive)
        {
            _peer.Shutdown(100);
            _peer.Dispose();
            _isActive = false;
        }
    }
}
