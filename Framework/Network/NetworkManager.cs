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
    private readonly MainApp _app;
    public ConcurrentDictionary<string, float3> RemotePlayerPositions { get; } = new();
    public ConcurrentDictionary<uint, NetworkCubeState> NetworkedCubesState { get; } = new();
    public struct BuildCommand
    {
        public uint NetId;
        public float3 Position;
        public uint BlockId;
        public bool IsStatic;
        public uint ParentNetId;
    }
    public ConcurrentQueue<BuildCommand> IncomingBuildCommands { get; } = new();
    public ConcurrentQueue<uint> IncomingDestroyCommands { get; } = new();

    private readonly ConcurrentDictionary<string, uint> _remotePhysicsBodies = new();

    public bool IsServer => _isServer;
    public bool IsActive => _isActive;
    public NetworkManager(MainApp app)
    {
        _app = app;
    }
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

        // СВЯЗЫВАНИЕ: Момент авторизации нового игрока на СЕРВЕРЕ
        if (packetId == (byte)DefaultMessageIDTypes.NewIncomingConnection)
        {
            Console.WriteLine($"[Server] Новый игрок авторизован и вошел в мир: {clientAddressKey}");

            // Запрашиваем у сцены текущее состояние мира и отправляем его новому клиенту
            if (_app.CurrentScene is GameplayScene gameplayScene)
            {
                SendExistingWorldToClient(packet->SystemAddress, gameplayScene.GetCubeBlockIds(), gameplayScene.GetNetworkedCubes());
            }
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
                    string remoteAddressStr = System.Text.Encoding.UTF8.GetString(ptr, 64).TrimEnd('\0');
                    float3 remotePos = *(float3*)(packet->Data + 66);

                    RemotePlayerPositions[remoteAddressStr] = remotePos;
                }
            }
            else if (gameId == GameMessageID.BuildObject)
            {
                // Принудительно кастим к byte* для побайтовой точности
                byte* dataPtr = (byte*)packet->Data;

                uint netId = *(uint*)(dataPtr + 2);

                float3 pos;
                pos.X = *(float*)(dataPtr + 6);
                pos.Y = *(float*)(dataPtr + 10);
                pos.Z = *(float*)(dataPtr + 14);

                uint blockId = *(uint*)(dataPtr + 18);
                uint parentNetId = *(uint*)(dataPtr + 22);
                bool isStatic = *(dataPtr + 26) == 1;

                if (_isServer)
                {
                    Console.WriteLine($"[СЕРВЕР] Запрос от клиента. Врем. ID: {netId}, Истинный BlockID: {blockId}, Static: {isStatic}");

                    if (_app.CurrentScene is GameplayScene gameplayScene)
                    {
                        netId = gameplayScene.GenerateNextNetworkId(blockId);
                    }

                    // Сервер пересылает пакет дальше с сохранением оригинальных blockId и isStatic
                    SendBuildOrDestroyToAll(netId, pos, true, default, blockId, parentNetId, isStatic);
                }
                else
                {
                    Console.WriteLine($"[КЛИЕНТ] Получен авторитетный блок от сервера. ID: {netId}, Распакованный BlockID: {blockId}, Static: {isStatic}");
                }

                // Отправляем в нашу новую безопасную структуру очереди
                IncomingBuildCommands.Enqueue(new BuildCommand
                {
                    NetId = netId,
                    Position = pos,
                    BlockId = blockId,
                    IsStatic = isStatic,
                    ParentNetId = parentNetId
                });
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
            else if (gameId == GameMessageID.DestroyObject)
            {
                uint netId = *(uint*)(packet->Data + 2);

                if (_isServer)
                {
                    Console.WriteLine($"[ОТЛАДКА УДАЛЕНИЯ СЕРВЕРА] Получен запрос на удаление. ID: {netId}");

                    // ИСПРАВЛЕНИЕ: Сервер очищает сетевой кэш состояния этого куба у себя
                    NetworkedCubesState.TryRemove(netId, out _);

                    IncomingDestroyCommands.Enqueue(netId);
                    SendBuildOrDestroyToAll(netId, float3.Zero, false, default);
                }
                else
                {
                    Console.WriteLine($"[ОТЛАДКА УДАЛЕНИЯ КЛИЕНТА] Получена команда на удаление от сервера. ID: {netId}");

                    // ИСПРАВЛЕНИЕ: Клиент очищает сетевой кэш состояния этого куба у себя
                    NetworkedCubesState.TryRemove(netId, out _);

                    IncomingDestroyCommands.Enqueue(netId);
                }
            }


        }
    }
 
    public void SendExistingWorldToClient(SystemAddress clientAddress, Dictionary<uint, uint> serverCubeBlockIds, Dictionary<uint, uint> serverNetworkedCubes)
    {
        if (!_isServer || !_isActive) return;

        var phys = PhysicsManager.Instance;

        foreach (var kvp in serverNetworkedCubes)
        {
            uint netId = kvp.Key;
            uint bodyId = kvp.Value;

            // Получаем актуальные данные блока из симуляции сервера
            phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 pos, out _);

            if (!serverCubeBlockIds.TryGetValue(netId, out uint blockId))
                blockId = 1;

            // Формируем пакет BuildObject индивидуально для этого адреса
            byte[] buffer = new byte[26];
            buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
            buffer[1] = (byte)GameMessageID.BuildObject;

            fixed (byte* p = buffer)
            {
                *(uint*)(p + 2) = netId;
                *(float3*)(p + 6) = pos;
                *(uint*)(p + 18) = blockId;
                *(uint*)(p + 22) = 0; // Для старых блоков parentId сбрасываем, они уже на своих местах
            }

            // Отправляем пакет ReliableOrdered конкретно вошедшему игроку
            _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, new AddressOrGUID(clientAddress), false);
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
    public void SendBuildOrDestroyToAll(uint netId, float3 pos, bool isBuild, RakNetGUID excludeGuid, uint blockId = 1, uint parentNetId = 0, bool isStatic = false)
    {
        if (!_isActive) return;

        // Пакет строго фиксированного размера: 27 байт для постройки, 6 байт для удаления
        byte[] buffer = isBuild ? new byte[27] : new byte[6];
        buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
        buffer[1] = (byte)(isBuild ? GameMessageID.BuildObject : GameMessageID.DestroyObject);

        // Безопасное побайтовое заполнение буфера без использования указателей fixed
        Buffer.BlockCopy(BitConverter.GetBytes(netId), 0, buffer, 2, 4);

        if (isBuild)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(pos.X), 0, buffer, 6, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(pos.Y), 0, buffer, 10, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(pos.Z), 0, buffer, 14, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(blockId), 0, buffer, 18, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(parentNetId), 0, buffer, 22, 4);
            buffer[26] = (byte)(isStatic ? 1 : 0);
        }

        // Отправка с правильной маршрутизацией направления
        if (_isServer)
        {
            _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, new AddressOrGUID(excludeGuid), true);
        }
        else
        {
            _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, new AddressOrGUID(_serverAddress), false);
        }
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
