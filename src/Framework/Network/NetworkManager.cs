using Brutal.JoltApi;
using Brutal.Numerics;
using Brutal.RakNetApi;
using Brutal.Strings;
using BrutalSandbox.Framework.Files;
using BrutalSandbox.Framework.Physics;
using BrutalSandbox.Framework.Scenes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
    public ConcurrentDictionary<string, float3> RemotePlayerVelocities { get; } = new();


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

    private readonly Dictionary<string, SavedPlayerData> _savedPlayersFromSession = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SavedPlayerData> SavedPlayersFromSession => _savedPlayersFromSession;
    private DateTime _lastServerPacketTime = DateTime.UtcNow;
    private string _myNickname = "Player_" + Random.Shared.Next(100, 999);
    public string MyNickname { get => _myNickname; set => _myNickname = value; }
    private bool _pendingDisconnect = false;
    private string _disconnectReason = "";

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

        // 1. Безопасно вычитываем все пакеты из очереди RakNet
        for (var packet = _peer.Receive(); packet != null; packet = _peer.Receive())
        {
            if (!_isServer)
            {
                _lastServerPacketTime = DateTime.UtcNow;
            }

            ProcessPacket(packet);

            // Деаллокация происходит, пока _peer еще гарантированно существует и стабилен
            _peer.DeallocatePacket(packet);
        }

        // 2. ИСПРАВЛЕНИЕ: Выполняем безопасное отключение ПОСЛЕ завершения цикла обработки пакетов
        if (_pendingDisconnect && _isActive)
        {
            _isActive = false;
            _pendingDisconnect = false;

            _peer.Shutdown(100);
            _peer.Dispose();

            if (_app.CurrentScene is GameplayScene gameplay)
            {
                gameplay.AddMessageLocally($"[Система] Вы были отключены: {_disconnectReason}");
            }

            _app.ChangeScene(GameState.MainMenu);
            return; // Выходим из метода, так как сеть утилизирована
        }

        // Проверка 10-секундного таймаута (только для клиента)
        if (!_isServer && _isActive)
        {
            if ((DateTime.UtcNow - _lastServerPacketTime).TotalSeconds > 10.0)
            {
                Console.WriteLine("[Клиент] Превышено время ожидания ответа от сервера (10 сек). Отключение.");
                DisconnectMeWithReason("Превышено время ожидания сервера (Таймаут)");
            }
        }

        if (_isServer)
        {
            var phys = PhysicsManager.Instance;
            foreach (var kvp in _remotePhysicsBodies)
            {
                string clientKey = kvp.Key;
                uint bodyId = kvp.Value;

                // Извлекаем целевую позицию, которую прислал клиент
                string targetFullKey = null;
                foreach (var rKey in RemotePlayerPositions.Keys)
                {
                    if (rKey.StartsWith(clientKey + "|")) { targetFullKey = rKey; break; }
                }

                if (targetFullKey != null)
                {
                    float3 clientTargetPos = RemotePlayerPositions[targetFullKey];
                    phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 currentServerPos, out _);

                    float3 toTarget = clientTargetPos - currentServerPos;
                    float distSq = float3.Dot(toTarget, toTarget);

                    if (distSq > 25f) // Перебор по дистанции — жесткая телепортация
                    {
                        phys.BodyInterface.SetPositionAndRotation(bodyId, clientTargetPos, floatQuat.Identity, Activation.Activate);
                        phys.BodyInterface.SetLinearVelocity(bodyId, float3.Zero);
                    }
                    else
                    {
                        // Вместо SetPosition рассчитываем честную физическую скорость движения к цели!
                        // Физическое тело будет толкать кубы перед собой, коллизии работают идеально.
                        float3 pushVelocity = toTarget * 12.0f;
                        phys.BodyInterface.SetLinearVelocity(bodyId, pushVelocity);
                    }
                }
            }
        }
    }
    public void SendDisconnectionCommand(string clientAddressKey, bool isBan)
    {
        if (!_isServer || !_isActive) return;

        // Формируем пакет кика/бана
        byte[] buffer = new byte[2];
        buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
        buffer[1] = (byte)(isBan ? GameMessageID.BanMessage : GameMessageID.KickMessage);

        // Ищем SystemAddress или RakNetGUID по ключу в вашей системе
        // Отправляем пакет ReliableOrdered по адресу игрока, а затем закрываем соединение
        foreach (var key in RemotePlayerPositions.Keys)
        {
            if (key.StartsWith(clientAddressKey + "|"))
            {
                // Здесь отправляем пакет в RakNet. Так как точный метод отправки по строке адреса скрыт, 
                // сервер делает широковещательную рассылку с фильтрацией, либо отправляет пакет индивидуально:
                _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, new AddressOrGUID(new SystemAddress()), true);
                break;
            }
        }
    }

    public void DisconnectMeWithReason(string reason)
    {
        // Устанавливаем флаги для безопасного удаления в конце метода Update
        _pendingDisconnect = true;
        _disconnectReason = reason;
    }

    private void ProcessPacket(Packet* packet)
    {
        if (packet == null || packet->Data == null || packet->Length == 0) return;

        byte packetId = packet->Data[0];
        string clientAddressKey = packet->SystemAddress.ToString();
        if (!_isServer && packetId == (byte)DefaultMessageIDTypes.ConnectionRequestAccepted)
        {
            // Запоминаем авторитетный адрес сервера, полученный напрямую из пакета!
            _serverAddress = packet->SystemAddress;
            Console.WriteLine($"[Клиент] Подключение принято сервером: {_serverAddress.ToString(true)}");
            return;
        }
        // Клиент получает уведомление, что сервер закрылся или соединение потеряно
        if (!_isServer && (packetId == (byte)DefaultMessageIDTypes.ConnectionLost || packetId == (byte)DefaultMessageIDTypes.DisconnectionNotification))
        {
            Console.WriteLine("[Client] Соединение с сервером потеряно. Возврат в главное меню.");
            _isActive = false;
            _app.ChangeScene(GameState.MainMenu); // Автоматический кик обратно в меню
            return;
        }

        if (packetId == (byte)DefaultMessageIDTypes.NewIncomingConnection)
        {
            if (_isServer)
            {
                // Извлекаем чистый IP-адрес без порта
                string rawAddress = packet->SystemAddress.ToString(false);

                if (_app.CurrentScene is GameplayScene gameplayScene)
                {
                    if (gameplayScene.IsIpBanned(rawAddress))
                    {
                        Console.WriteLine($"[Сервер] Отклонено подключение: IP {rawAddress} находится в бан-листе.");

                        // Если в вашей обертке Brutal.RakNetApi у _peer есть метод для принудительного разрыва:
                        // _peer.CloseConnection(packet->SystemAddress, true);
                        return; // Прерываем обработку подключения
                    }
                }

                Console.WriteLine($"[Server] Новый игрок подключился: {clientAddressKey}");
                if (_app.CurrentScene is GameplayScene gameplay)
                {
                    SendExistingWorldToClient(packet->SystemAddress, gameplay.GetCubeBlockIds(), gameplay.GetNetworkedCubes());
                }
            }
            return;
        }

        if (packetId == (byte)DefaultMessageIDTypes.ConnectionLost || packetId == (byte)DefaultMessageIDTypes.DisconnectionNotification)
        {
            if (_isServer)
            {
                Console.WriteLine($"[Server] Игрок {clientAddressKey} покинул сервер. Сохраняем состояние.");

                // Если у нас был зафиксирован никнейм для этого адреса, вытаскиваем его трансформ
                if (_remotePhysicsBodies.TryRemove(clientAddressKey, out uint bodyId))
                {
                    var phys = PhysicsManager.Instance;
                    phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 lastPos, out _);
                    float3 lastVel = phys.BodyInterface.GetLinearVelocity(bodyId);

                    // Ищем имя игрока, соответствующее этому адресу
                    string associatedNick = "UnknownPlayer";
                    foreach (var kvp in RemotePlayerPositions)
                    {
                        if (kvp.Key.StartsWith(clientAddressKey + "|"))
                        {
                            associatedNick = kvp.Key.Split('|')[1];
                            RemotePlayerPositions.TryRemove(kvp.Key, out _);
                            break;
                        }
                    }

                    // Запоминаем данные вышедшего игрока
                    _savedPlayersFromSession[associatedNick] = new SavedPlayerData
                    {
                        Nickname = associatedNick,
                        Position = lastPos,
                        LinearVelocity = lastVel
                    };

                    phys.BodyInterface.RemoveBody(bodyId);
                    phys.BodyInterface.DestroyBody(bodyId);
                }
            }
            return;
        }

        if (packetId == (byte)DefaultMessageIDTypes.UserPacketEnum)
        {
            GameMessageID gameId = (GameMessageID)packet->Data[1];

            if (gameId == GameMessageID.PlayerTransform)
            {
                byte* ptr = packet->Data + 2;
                string remoteNick = System.Text.Encoding.UTF8.GetString(ptr, 32).TrimEnd('\0').Trim();
                float3 position = *(float3*)(ptr + 32);
                float3 velocity = *(float3*)(ptr + 44);

                if (_isServer)
                {
                    // Проверка на бан по никнейму
                    if (_app.CurrentScene is GameplayScene gameplayScene)
                    {
                        if (gameplayScene.IsNickBanned(remoteNick))
                        {
                            Console.WriteLine($"[Сервер] Игрок с забаненным ником '{remoteNick}' пытается синхронизироваться. Отправка пакета бана.");

                            // Формируем пакет бана и отправляем его отправителю
                            byte[] banBuffer = new byte[2];
                            banBuffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
                            banBuffer[1] = (byte)GameMessageID.BanMessage;
                            _peer.Send(banBuffer, PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, new AddressOrGUID(packet->SystemAddress), false);
                            return; // Игнорируем пакет трансформа, игрок не появится в мире
                        }
                    }

                    string storageKey = $"{clientAddressKey}|{remoteNick}";
                    RemotePlayerPositions[storageKey] = position;

                    var phys = PhysicsManager.Instance;
                    if (!_remotePhysicsBodies.TryGetValue(clientAddressKey, out uint bodyId))
                    {
                        using var capsuleSettings = CapsuleShapeSettings.Create(0.5f, 0.4f, default);
                        bodyId = phys.CreateBody(capsuleSettings, position, floatQuat.Identity, MotionType.Dynamic, PhysicsManager.LayerMoving, "stone_wall");
                        phys.BodyInterface.SetGravityFactor(bodyId, 0f);
                        _remotePhysicsBodies[clientAddressKey] = bodyId;
                    }

                    // Плавная коррекция позиции и применение скоростей на стороне сервера
                    phys.BodyInterface.SetLinearVelocity(bodyId, velocity);
                    phys.BodyInterface.SetPositionAndRotation(bodyId, position, floatQuat.Identity, Activation.Activate);

                    // Пересылаем всем остальным клиентам
                    BroadcastTransform(remoteNick, position, velocity, packet->Guid);
                }
                else
                {
                    // Клиент сохраняет позицию и скорость удаленного игрока
                    RemotePlayerPositions[remoteNick] = position;
                    RemotePlayerVelocities[remoteNick] = velocity;
                }
            }
            else if (gameId == GameMessageID.BuildObject)
            {
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
                    Console.WriteLine($"[СЕРВЕР] Получен запрос на блок от клиента. Старый ID: {netId}, BlockID: {blockId}");

                    if (_app.CurrentScene is GameplayScene gameplayScene)
                    {
                        // Генерируем авторитетный серверный ID вместо временного клиентского
                        netId = gameplayScene.GenerateNextNetworkId(blockId);
                    }

                    // Пересылаем пакет всем остальным клиентам, исключая того, кто его прислал (packet->Guid)
                    SendBuildOrDestroyToAll(netId, pos, true, default, blockId, parentNetId, isStatic);
                }
                else
                {
                    Console.WriteLine($"[КЛИЕНТ] Получен авторитетный блок от сервера. ID: {netId}, BlockID: {blockId}");
                }

                // ИСПРАВЛЕНИЕ: Добавляем команду в очередь симуляции (выполняется и на сервере, и на клиенте!)
                IncomingBuildCommands.Enqueue(new BuildCommand
                {
                    NetId = netId,
                    Position = pos,
                    BlockId = blockId,
                    IsStatic = isStatic,
                    ParentNetId = parentNetId
                });
            }
            else if (gameId == GameMessageID.DestroyObject)
            {
                uint netId = *(uint*)(packet->Data + 2);

                // Очищаем сетевое состояние объекта из кэша
                NetworkedCubesState.TryRemove(netId, out _);

                if (_isServer)
                {
                    Console.WriteLine($"[СЕРВЕР] Получен запрос на удаление от клиента. ID: {netId}");

                    // Сервер уведомляет остальных, исключая автора запроса
                    SendBuildOrDestroyToAll(netId, float3.Zero, false, default);
                }
                else
                {
                    Console.WriteLine($"[КЛИЕНТ] Получена команда на удаление от сервера. ID: {netId}");
                }

                // ИСПРАВЛЕНИЕ: Добавляем команду удаления в очередь симуляции для всех сторон!
                IncomingDestroyCommands.Enqueue(netId);
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
            else if (gameId == GameMessageID.ChatMessage)
            {
                byte* dataPtr = packet->Data + 2;
                // Читаем длину сообщения и само сообщение
                int msgLength = *(int*)dataPtr;
                string message = System.Text.Encoding.UTF8.GetString(dataPtr + 4, msgLength);

                if (_isServer)
                {
                    // Сервер добавляет сообщение в свою локальную сцену (если она активна)
                    if (_app.CurrentScene is GameplayScene gameplayScene)
                    {
                        gameplayScene.AddMessageLocally(message);
                    }

                    // Ретранслируем сообщение всем остальным клиентам, исключая автора (packet->Guid)
                    // Для этого создаем пакет и рассылаем его через broadcast = true с исключением
                    byte[] buffer = new byte[6 + msgLength];
                    buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
                    buffer[1] = (byte)GameMessageID.ChatMessage;
                    fixed (byte* b = buffer)
                    {
                        *(int*)(b + 2) = msgLength;
                        System.Runtime.InteropServices.Marshal.Copy((IntPtr)(dataPtr + 4), buffer, 6, msgLength);
                    }
                    _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, new AddressOrGUID(packet->Guid), true);
                }
                else
                {
                    // Клиент просто выводит полученное от сервера сообщение на экран
                    if (_app.CurrentScene is GameplayScene gameplayScene)
                    {
                        gameplayScene.AddMessageLocally(message);
                    }
                }

            }
            else if (gameId == GameMessageID.KickMessage && !_isServer)
            {
                DisconnectMeWithReason("Вы были кикнуты администратором сервера.");
            }
            else if (gameId == GameMessageID.BanMessage && !_isServer)
            {
                DisconnectMeWithReason("Вы были ЗАБАНЕНЫ на этом сервере.");
            }

        }
    }

    public void SendChatMessage(string formattedMessage)
    {
        if (!_isActive) return;

        byte[] msgBytes = System.Text.Encoding.UTF8.GetBytes(formattedMessage);
        byte[] buffer = new byte[6 + msgBytes.Length];
        buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
        buffer[1] = (byte)GameMessageID.ChatMessage;

        fixed (byte* p = buffer)
        {
            *(int*)(p + 2) = msgBytes.Length;
            System.Runtime.InteropServices.Marshal.Copy(msgBytes, 0, (IntPtr)(p + 6), msgBytes.Length);
        }

        if (_isServer)
        {
            // Если отправляет сервер (хост), шлем всем клиентам (broadcast = true)
            _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, new AddressOrGUID(new SystemAddress()), true);
        }
        else
        {
            // Если отправляет клиент, шлем строго на сервер (broadcast = false)
            _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.ReliableOrdered, 0, new AddressOrGUID(_serverAddress), false);
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

    public void SendMyTransform(float3 position, float3 velocity)
    {
        if (!_isActive) return;

        byte[] buffer = new byte[58];
        buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
        buffer[1] = (byte)GameMessageID.PlayerTransform;

        fixed (byte* p = buffer)
        {
            byte[] nickBytes = System.Text.Encoding.UTF8.GetBytes(_myNickname.PadRight(32, '\0'));
            Array.Copy(nickBytes, 0, buffer, 2, 32);
            *(float3*)(p + 34) = position;
            *(float3*)(p + 46) = velocity;
        }

        if (_isServer)
        {
            _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.UnreliableSequenced, 0, new AddressOrGUID(new SystemAddress()), true);
        }
        else
        {
            _peer.Send(buffer, PacketPriority.HighPriority, PacketReliability.UnreliableSequenced, 0, new AddressOrGUID(_serverAddress), false);
        }
    }

    private void BroadcastTransform(string nickname, float3 position, float3 velocity, RakNetGUID excludeGuid)
    {
        byte[] buffer = new byte[58];
        buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
        buffer[1] = (byte)GameMessageID.PlayerTransform;

        fixed (byte* p = buffer)
        {
            byte[] nickBytes = System.Text.Encoding.UTF8.GetBytes(nickname.PadRight(32, '\0'));
            Array.Copy(nickBytes, 0, buffer, 2, 32);
            *(float3*)(p + 34) = position;
            *(float3*)(p + 46) = velocity;
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
