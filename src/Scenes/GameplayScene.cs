using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.JoltApi;
using Brutal.Numerics;
using Brutal.RakNetApi;
using BrutalSandbox.Framework.Files;
using BrutalSandbox.Framework.Graphics;
using BrutalSandbox.Framework.Localization;
using BrutalSandbox.Framework.Network;
using BrutalSandbox.Framework.Physics;
using BrutalSandbox.Framework.Scenes;
using BrutalSandbox.Framework.Terrain;
using BrutalSandbox.Framework.UI;
using BrutalSandbox.GameRelated;
using System;
using System.Collections.Generic;

namespace BrutalSandbox;

public sealed class GameplayScene : IScene
{
    public struct LerpedCubeVisual
    {
        public float3 CurrentPos;
        public floatQuat CurrentRot;
        public float3 TargetPos;
        public floatQuat TargetRot;
    }

    private readonly MainApp _app;
    private readonly TerrainManager _terrainManager = new();
    private readonly Frustum _frustum = new();
    private readonly FirstPersonController _player = new();
    private ConstructionManager _constructionManager;

    private MeshData _graphicCubeMesh;
    private MeshData _graphicCapsuleMesh;

    private int _renderedChunksCount = 0;
    private bool _isMouseCaptured = false;
    public bool IsMouseCaptured => _isMouseCaptured;
    private bool _fWasPressed = false;
    private float _networkTickTimer = 0f;
    private const float NetworkTickRate = 1f / 30f;

    // Слоты хотбара (хранят ID блоков)
    private readonly uint[] _hotbarSlots = [1, 2, 3, 4, 5, 6, 7];
    private int _activeHotbarIndex = 0;
    private bool _isBlockMenuOpen = false;
    private bool _bWasPressed = false;

    public struct WeldLink
    {
        public uint ParentNetId;
        public float3 LocalOffset;   
        public floatQuat LocalRotation; 
    }


    private readonly Dictionary<uint, WeldLink> _weldLinks = new();
    private readonly Dictionary<uint, uint> _networkedCubes = new();
    private readonly Dictionary<uint, LerpedCubeVisual> _cubeVisuals = new();
    private readonly Dictionary<uint, uint> _cubeBlockIds = new();
    public Dictionary<uint, uint> GetCubeBlockIds() => _cubeBlockIds;
    public Dictionary<uint, uint> GetNetworkedCubes() => _networkedCubes;

    private readonly string _worldName;

    public readonly float AutoSaveInterval = 30f;
    private float _autoSaveTimer = 0f;
    private float4x4 _cachedViewProj;
    private bool _isChatOpen = false;
    private byte[] _chatInputBuffer = new byte[128];
    private readonly List<string> _chatMessages = new();
    private bool _tWasPressed = false;
    private bool _chatNeedsFocus = false;

    // Черные списки для базовой модерации (для сервера)
    private readonly HashSet<string> _bannedNicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bannedIps = new();
    public bool IsNickBanned(string nickname) => _bannedNicks.Contains(nickname.Trim());
    public bool IsIpBanned(string ip) => _bannedIps.Contains(ip.Trim());

    public GameplayScene(MainApp app, string worldName)
    {
        _app = app;
        _worldName = worldName;
        _constructionManager = new ConstructionManager(_app, _terrainManager, _networkedCubes, _cubeBlockIds, _weldLinks);
    }

    // В метод Initialize() добавляем автоматическую загрузку блоков из файла:
    public void Initialize()
    {
        _graphicCubeMesh = PrimitiveGenerator.CreateCube();
        _graphicCapsuleMesh = PrimitiveGenerator.CreateCapsule(1.0f, 0.4f);
        _player.Initialize();

        if (!_app.Network.IsActive || _app.Network.IsServer)
        {
            if (WorldSerializer.LoadWorld(_worldName, out var loadedBlocks, out var loadedPlayers))
            {
                // Восстановление кубов со скоростями
                foreach (var block in loadedBlocks)
                {
                    BlockData blockData = BlockRegistry.GetById(block.BlockId);
                    SpawnCubeLocal(block.NetId, block.Position, blockData.DefaultMass, blockData.DefaultFriction, block.IsStatic, block.ParentNetId, block.BlockId);

                    if (_networkedCubes.TryGetValue(block.NetId, out uint bodyId) && !block.IsStatic)
                    {
                        PhysicsManager.Instance.BodyInterface.SetLinearVelocity(bodyId, block.LinearVelocity);
                        PhysicsManager.Instance.BodyInterface.SetAngularVelocity(bodyId, block.AngularVelocity);
                    }

                    if (block.HasWeld)
                    {
                        _weldLinks[block.NetId] = new WeldLink
                        {
                            ParentNetId = block.ParentNetId,
                            LocalOffset = block.LocalOffset,
                            LocalRotation = block.LocalRotation
                        };
                    }
                }

                // Восстановление позиции локального хоста/игрока, если ник совпадает
                foreach (var p in loadedPlayers)
                {
                    if (p.Nickname == _app.Network.MyNickname)
                    {
                        _player.Position = p.Position;
                        // Если у вашего FirstPersonController есть вектор скорости, можно присвоить и его:
                        // _player.Velocity = p.LinearVelocity;
                        break;
                    }
                    else
                    {
                        // Добавляем ранее вышедших игроков сессии в кэш сетевого менеджера, чтобы не потерять их при перезаписи
                        _app.Network.SavedPlayersFromSession[p.Nickname] = p;
                    }
                }
            }
        }
    }

    public float4x4 GetViewProjection(float aspectRatio)
    {
        float4x4 view = _player.GetViewMatrix();
        float4x4 projection = float4x4.CreatePerspectiveFieldOfView(MathF.PI / 3.5f, aspectRatio, 0.1f, 1000f);
        _cachedViewProj = view * projection; // Кэшируем для Ui рендера
        return _cachedViewProj;
    }
    public uint GenerateNextNetworkId(uint blockId)
    {
        BlockData block = BlockRegistry.GetById(blockId);

        // Вызываем приватные счетчики ConstructionManager
        // Для этого нужно будет сделать свойства или методы в ConstructionManager, либо генерировать их здесь.
        // Самый простой способ — делегировать это нашему ConstructionManager:
        return _constructionManager.AllocateServerId(block.Shape == BlockShape.Platform);
    }
    public unsafe void Update(float deltaTime)
    {
        _app.Network.Update();

        if (!_app.Network.IsActive || _app.Network.IsServer)
        {
            _autoSaveTimer += deltaTime;
            if (_autoSaveTimer >= AutoSaveInterval)
            {
                PerformWorldSave();
                _autoSaveTimer = 0f; // Сбрасываем таймер
            }
        }

        PhysicsManager.Instance.Update(deltaTime);
        var phys = PhysicsManager.Instance;
        while (_app.Network.IncomingBuildCommands.TryDequeue(out var buildCmd))
        {
            if (!_networkedCubes.ContainsKey(buildCmd.NetId))
            {
                // Теперь мы берем оригинальный, валидный ID блока напрямую без кастов во float
                uint blockId = buildCmd.BlockId;
                _cubeBlockIds[buildCmd.NetId] = blockId;

                BlockData block = BlockRegistry.GetById(blockId);
                bool networkIsStatic = buildCmd.IsStatic;

                Console.WriteLine($"[ОТЛАДКА СЦЕНЫ] Успешный спавн из структуры. ID: {buildCmd.NetId}, Истинный BlockID: {blockId}, Static: {networkIsStatic}");

                SpawnCubeLocal(buildCmd.NetId, buildCmd.Position, block.DefaultMass, block.DefaultFriction, networkIsStatic, buildCmd.ParentNetId, blockId);
            }
        }



        while (_app.Network.IncomingDestroyCommands.TryDequeue(out var netId))
        {
            if (_networkedCubes.TryGetValue(netId, out uint bodyId))
            {
                _weldLinks.Remove(netId);
                _cubeBlockIds.Remove(netId); // Очистка ID

                phys.BodyInterface.RemoveBody(bodyId);
                phys.BodyInterface.DestroyBody(bodyId);
                _networkedCubes.Remove(netId);
            }
        }


        if (!_app.Network.IsActive || _app.Network.IsServer)
        {
            foreach (var kvp in _weldLinks)
            {
                uint childNetId = kvp.Key;
                WeldLink link = kvp.Value;

                if (_networkedCubes.TryGetValue(childNetId, out uint childBodyId) &&
                    _networkedCubes.TryGetValue(link.ParentNetId, out uint parentBodyId))
                {
                    phys.BodyInterface.GetPositionAndRotation(parentBodyId, out float3 parentPos, out floatQuat parentRot);
                    float3 parentLinVel = phys.BodyInterface.GetLinearVelocity(parentBodyId);
                    float3 parentAngVel = phys.BodyInterface.GetAngularVelocity(parentBodyId);

                    float3 globalTargetPos = parentPos + float3.Transform(link.LocalOffset, parentRot);
                    floatQuat globalTargetRot = parentRot * link.LocalRotation;

                    phys.BodyInterface.SetPositionAndRotation(childBodyId, globalTargetPos, globalTargetRot, Activation.Activate);
                    phys.BodyInterface.SetLinearVelocity(childBodyId, parentLinVel);
                    phys.BodyInterface.SetAngularVelocity(childBodyId, parentAngVel);
                }
            }
        }

        if (_app.Network.IsActive)
        {
            if (_app.Network.IsServer)
            {
                _networkTickTimer += deltaTime;
                if (_networkTickTimer >= NetworkTickRate)
                {
                    SendDynamicObjectsToClients();
                    _networkTickTimer = 0f;
                }
            }
            else
            {
                foreach (var kvp in _app.Network.NetworkedCubesState)
                {
                    uint netId = kvp.Key;
                    NetworkCubeState serverState = kvp.Value;

                    if (!_networkedCubes.TryGetValue(netId, out uint bodyId)) continue;
                    if (phys.BodyInterface.GetMotionType(bodyId) == MotionType.Static) continue;

                    // ИСПРАВЛЕНИЕ: Принудительно активируем тело в Jolt, если сервер сообщает, что оно движется
                    if (float3.Dot(serverState.LinearVelocity, serverState.LinearVelocity) > 0.01f)
                    {
                        phys.BodyInterface.ActivateBody(bodyId);
                    }

                    phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 clientPos, out _);
                    float3 posError = serverState.Position - clientPos;

                    if (float3.Dot(posError, posError) > 16f) // Дистанция рассинхронизации увеличена до 4 метров
                    {
                        phys.BodyInterface.SetPositionAndRotation(bodyId, serverState.Position, serverState.Rotation, Activation.Activate);
                        phys.BodyInterface.SetLinearVelocity(bodyId, serverState.LinearVelocity);
                        phys.BodyInterface.SetAngularVelocity(bodyId, serverState.AngularVelocity);
                    }
                    else
                    {
                        float3 blendVelocity = serverState.LinearVelocity + (posError * 8.0f); 
                        phys.BodyInterface.SetLinearVelocity(bodyId, blendVelocity);
                        phys.BodyInterface.SetAngularVelocity(bodyId, serverState.AngularVelocity);

                        phys.BodyInterface.SetPositionAndRotation(bodyId, clientPos, serverState.Rotation, Activation.DontActivate);
                    }
                }
            }
        }
        _player.UpdateLogic(_app.Window, deltaTime, _terrainManager);


        if (_app.Network.IsActive)
        {
            float3 currentVelocity = _player.PhysicsBodyId != 0
                ? PhysicsManager.Instance.BodyInterface.GetLinearVelocity(_player.PhysicsBodyId)
                : float3.Zero;

            _app.Network.SendMyTransform(_player.Position, currentVelocity);
        }
    }

    private void SpawnCubeLocal(uint netId, float3 position, float mass, float friction, bool isStatic, uint parentNetId, uint blockId)
    {
        var phys = PhysicsManager.Instance;
        MotionType motionType = isStatic ? MotionType.Static : MotionType.Dynamic;
        ushort layer = isStatic ? PhysicsManager.LayerStatic : PhysicsManager.LayerMoving;

        float3 finalSpawnPos = position;

        if (parentNetId != 0 && _networkedCubes.TryGetValue(parentNetId, out uint parentBodyId))
        {
            phys.BodyInterface.GetPositionAndRotation(parentBodyId, out float3 parentPos, out floatQuat parentRot);
            float3 worldOffset = position - parentPos;
            floatQuat invParentRot = floatQuat.Inverse(parentRot);
            float3 localOffset = float3.Transform(worldOffset, invParentRot);

            BlockData currentBlock = BlockRegistry.GetById(blockId);

            if (currentBlock.Shape == BlockShape.Cube)
            {
                localOffset.X = MathF.Round(localOffset.X);
                localOffset.Y = MathF.Round(localOffset.Y);
                localOffset.Z = MathF.Round(localOffset.Z);
            }
            else if (currentBlock.Shape == BlockShape.Platform)
            {
                localOffset.X = MathF.Round(localOffset.X / 3.0f) * 3.0f;
                localOffset.Z = MathF.Round(localOffset.Z / 3.0f) * 3.0f;
                localOffset.Y = MathF.Round(localOffset.Y * 5f) / 5f;
            }
            finalSpawnPos = parentPos + float3.Transform(localOffset, parentRot);

            _weldLinks[netId] = new WeldLink
            {
                ParentNetId = parentNetId,
                LocalOffset = localOffset,
                LocalRotation = floatQuat.Identity
            };
        }

        _cubeBlockIds[netId] = blockId; // Сохраняем переданный ID для рендера

        BlockData block = BlockRegistry.GetById(blockId);
        ShapeSettings shapeSettings;

        switch (block.Shape)
        {
            case BlockShape.Platform:
                shapeSettings = BoxShapeSettings.Create(new float3(1.5f, 0.1f, 1.5f), 0.05f, default);
                break;
            case BlockShape.Capsule:
                shapeSettings = CapsuleShapeSettings.Create(0.5f, 0.4f, default);
                break;
            case BlockShape.Cube:
            default:
                shapeSettings = BoxShapeSettings.Create(new float3(0.5f), 0.05f, default);
                break;
        }

        using (shapeSettings)
        {
            uint bodyId = phys.CreateBody(shapeSettings, finalSpawnPos, floatQuat.Identity, motionType, layer, block.TextureName);
            phys.BodyInterface.SetFriction(bodyId, block.DefaultFriction);

            if (motionType == MotionType.Dynamic)
            {
                phys.BodyInterface.ActivateBody(bodyId);
            }

            _networkedCubes[netId] = bodyId;

            if (parentNetId != 0 && _networkedCubes.TryGetValue(parentNetId, out uint pBodyId))
            {
                phys.BodyInterface.ActivateBody(pBodyId);
            }
        }
    }

    private unsafe void SendDynamicObjectsToClients()
    {
        var phys = PhysicsManager.Instance;
        var list = new List<(uint NetId, float3 Pos, float3 LinVel, float3 AngVel, floatQuat Rot)>();

        foreach (var (netId, bodyId) in _networkedCubes)
        {
            // ИСПРАВЛЕНИЕ: Если объект приварен к кому-то, серверу НЕ нужно слать его координаты.
            // Клиент сам переместит его вслед за родителем по WeldLink. Это экономит трафик и убирает тряску.
            if (_weldLinks.ContainsKey(netId)) continue;

            phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 p, out floatQuat r);
            float3 linVel = phys.BodyInterface.GetLinearVelocity(bodyId);
            float3 angVel = phys.BodyInterface.GetAngularVelocity(bodyId);
            list.Add((netId, p, linVel, angVel, r));
        }

        if (list.Count == 0) return;

        byte[] buffer = new byte[6 + (list.Count * 56)];
        buffer[0] = (byte)DefaultMessageIDTypes.UserPacketEnum;
        buffer[1] = (byte)GameMessageID.SyncDynamicObjects;

        fixed (byte* p = buffer)
        {
            *(int*)(p + 2) = list.Count;
            int offset = 6;
            foreach (var obj in list)
            {
                *(uint*)(p + offset) = obj.NetId;
                *(float3*)(p + offset + 4) = obj.Pos;
                *(float3*)(p + offset + 16) = obj.LinVel;
                *(float3*)(p + offset + 28) = obj.AngVel;
                *(floatQuat*)(p + offset + 40) = obj.Rot;
                offset += 56;
            }
        }

        _app.Network.SendRawPacket(buffer, true);
    }

    private void PerformWorldSave()
    {
        var playersToSave = new List<SavedPlayerData>();

        // 1. Добавляем себя (Хоста)
        float3 myVel = _player.PhysicsBodyId != 0 ? PhysicsManager.Instance.BodyInterface.GetLinearVelocity(_player.PhysicsBodyId) : float3.Zero;
        playersToSave.Add(new SavedPlayerData { Nickname = _app.Network.MyNickname, Position = _player.Position, LinearVelocity = myVel });

        // 2. Добавляем активных клиентов на сервере
        foreach (var kvp in _app.Network.RemotePlayerPositions)
        {
            // Ключ имеет формат "address|nickname"
            if (kvp.Key.Contains('|'))
            {
                var parts = kvp.Key.Split('|');
                string nick = parts[1];
                playersToSave.Add(new SavedPlayerData { Nickname = nick, Position = kvp.Value, LinearVelocity = float3.Zero });
            }
        }

        // 3. Добавляем игроков, которые вышли во время этой сессии (из кэша NetworkManager)
        foreach (var kvp in _app.Network.SavedPlayersFromSession)
        {
            if (!playersToSave.Exists(p => p.Nickname == kvp.Key))
            {
                playersToSave.Add(kvp.Value);
            }
        }

        WorldSerializer.SaveWorld(_worldName, _cubeBlockIds, _networkedCubes, _weldLinks, playersToSave);
        Console.WriteLine($"[WorldSave] Мир {_worldName} успешно сохранен. Записано игроков: {playersToSave.Count}");
    }
    public void Render(IRenderer renderer, float4x4 viewProj)
    {
        _frustum.Update(viewProj);
        _terrainManager.UpdateLoadGrid(_player.Position, _frustum);
        _renderedChunksCount = 0;

        foreach (var chunk in _terrainManager.GetVisibleChunks())
        {
            if (chunk.Mesh != null)
            {
                renderer.RenderMesh(chunk.Mesh, float4x4.Identity, "grass_tile");
                _renderedChunksCount++;
            }
        }

        var phys = PhysicsManager.Instance;
        float dt = ImGui.GetIO().DeltaTime;

        foreach (var (netId, bodyId) in _networkedCubes)
        {
            if (bodyId == _player.PhysicsBodyId) continue;

            // Рендерим строго по тем координатам, которые зафиксированы в Update симуляции
            float3 position = float3.Zero;
            floatQuat rotation = floatQuat.Identity;

            if (_cubeVisuals.TryGetValue(netId, out var visual))
            {
                position = visual.CurrentPos;
                rotation = visual.CurrentRot;
            }
            else
            {
                // Фоллбэк на случай, если объект только что создался
                phys.BodyInterface.GetPositionAndRotation(bodyId, out position, out rotation);
            }

            if (!_frustum.IsBoxVisible(position - new float3(2f), position + new float3(2f))) continue;

            // Прямое получение данных о блоке
            if (!_cubeBlockIds.TryGetValue(netId, out uint blockId))
                blockId = 1;

            BlockData block = BlockRegistry.GetById(blockId);
            MeshData targetMesh = _graphicCubeMesh;
            float4x4 scale = float4x4.Identity;

            if (block.Shape == BlockShape.Platform)
            {
                scale = float4x4.CreateScale(3.0f, 0.2f, 3.0f);
            }
            else if (block.Shape == BlockShape.Capsule)
            {
                targetMesh = _graphicCapsuleMesh;
            }

            float4x4 model = scale * float4x4.CreateFromQuaternion(rotation) * float4x4.CreateTranslation(position);
            renderer.RenderMesh(targetMesh, model, block.TextureName);
        }

        foreach (var kvp in _app.Network.RemotePlayerPositions)
        {
            float3 remotePlayerPos = kvp.Value;

            // Проверяем видимость игрока камерой во избежание лишней нагрузки на GPU
            if (!_frustum.IsBoxVisible(remotePlayerPos - new float3(1f, 2f, 1f), remotePlayerPos + new float3(1f, 2f, 1f)))
                continue;

            // Игроки рендерятся стандартной текстурой "stone_wall" в форме капсулы
            float4x4 playerModelMatrix = float4x4.CreateTranslation(remotePlayerPos);
            renderer.RenderMesh(_graphicCapsuleMesh, playerModelMatrix, "stone_wall");
        }
    }
    public void RenderUi()
    {
        var io = ImGui.GetIO();
        var drawList = ImGui.GetForegroundDrawList();

        foreach (var kvp in _app.Network.RemotePlayerPositions)
        {
            string fullKey = kvp.Key;
            string nick = fullKey.Contains('|') ? fullKey.Split('|')[1] : fullKey;

            float3 worldPos = kvp.Value + new float3(0f, 1.2f, 0f);

            float4 clipSpace = float4.Transform(new float4(worldPos, 1.0f), _cachedViewProj);

            if (clipSpace.W > 0.1f) 
            {
                float3 ndc = new float3(clipSpace.X / clipSpace.W, clipSpace.Y / clipSpace.W, clipSpace.Z / clipSpace.W);

                float screenX = (ndc.X + 1.0f) * 0.5f * io.DisplaySize.X;
                float screenY = (1.0f - ndc.Y) * 0.5f * io.DisplaySize.Y;

                float2 textSize = ImGui.CalcTextSize(nick);
                float2 textPos = new float2(screenX - (textSize.X / 2f), screenY);

                drawList.AddRectFilled(textPos - new float2(4, 2), textPos + textSize + new float2(4, 2), 0x7F000000, 4f);
                drawList.AddText(textPos, 0xFF00FFFF, nick);
            }
        }

        ImGui.SetNextWindowPos(new float2(10, 10), ImGuiCond.FirstUseEver);

        if (ImGui.Begin(LocalizationKeys.GameplayPlayerPanelTitle.ToUtf8()))
        {
            if (ImGui.Button(LocalizationKeys.GameplayBackToMenu.ToUtf8()))
            {
                if (_app.Network.IsActive && _app.Network.IsServer)
                {
                    _app.Network.Dispose();
                }
                _app.ChangeScene(GameState.MainMenu);
            }

            ImGui.SameLine();
            if (ImGui.Button(LocalizationKeys.GameplaySaveWorldBtn.ToUtf8()))
            {
                PerformWorldSave();
            }

            string worldLabelFmt = string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayWorldLabel.ToUtf8()), _worldName);
            ImGui.Text(worldLabelFmt);
            ImGui.Separator();

            ImGui.Text(string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayRenderedChunks.ToUtf8()), _renderedChunksCount));
            ImGui.Text(string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayPosition.ToUtf8()), _player.Position.X.ToString("F1"), _player.Position.Y.ToString("F1"), _player.Position.Z.ToString("F1")));

            string flightStatus = _player.IsNoclip ? System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayOn.ToUtf8()) : System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayOff.ToUtf8());
            ImGui.Text(string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayFlightMode.ToUtf8()), flightStatus));

            ImGui.Separator();
            ImGui.Text(LocalizationKeys.GameplayBuildSettings.ToUtf8());

            bool staticObj = _constructionManager.BuildIsStatic;
            if (ImGui.Checkbox(LocalizationKeys.GameplayStaticObject.ToUtf8(), ref staticObj))
                _constructionManager.BuildIsStatic = staticObj;

            bool weldObj = _constructionManager.BuildIsWelded;
            if (ImGui.Checkbox(LocalizationKeys.GameplayWeldObject.ToUtf8(), ref weldObj))
                _constructionManager.BuildIsWelded = weldObj;
        }
        ImGui.End();

        // УЛУЧШЕННОЕ МЕНЮ БИБЛИОТЕКИ БЛОКОВ
        if (_isBlockMenuOpen)
        {
            // Фиксируем оптимальный размер окна под 10 колонок
            ImGui.SetNextWindowSize(new float2(980, 480), ImGuiCond.Always);

            // ИСПРАВЛЕНИЕ: Локализован заголовок окна библиотеки блоков
            if (ImGui.Begin(LocalizationKeys.BlockLibraryTitle.ToUtf8(), ref _isBlockMenuOpen, ImGuiWindowFlags.NoResize))
            {
                // Разделяем экран: Слева — сетка 10xX (720px), Справа — инфо-карточка (240px)
                ImGui.Columns(2, "menuSplit"u8, true);
                ImGui.SetColumnWidth(0, 720f);

                // ИСПРАВЛЕНИЕ: Локализована строка приглашения к выбору хотбара
                ImGui.Text(LocalizationKeys.BlockLibraryChoosePrompt.ToUtf8());
                ImGui.Separator();

                // Скроллируемый контейнер для сетки блоков
                ImGui.BeginChild("BlockGridRegion"u8, new float2(0, -10), ImGuiChildFlags.Borders);

                uint hoveredBlockId = _constructionManager.SelectedBlockId;
                int columnsCount = 10; // Идеальная сетка 10 на X

                for (int i = 0; i < BlockRegistry.RegisteredBlocks.Count; i++)
                {
                    var block = BlockRegistry.RegisteredBlocks[i];
                    ImGui.PushID((int)block.Id);

                    ImTextureRef texRef = _app.AssetManager.GetGeneratedIconRef(block.Id);

                    bool isSelected = _constructionManager.SelectedBlockId == block.Id;
                    if (isSelected) ImGui.PushStyleColor(ImGuiCol.Button, new float4(1f, 0.7f, 0f, 0.6f));

                    float2 uv0 = new float2(0f, 1f);
                    float2 uv1 = new float2(1f, 0f);

                    // Компактные квадратные кнопки 60x60
                    if (ImGui.ImageButton("##gridBtn"u8, texRef, new float2(60, 60), uv0, uv1))
                    {
                        _hotbarSlots[_activeHotbarIndex] = block.Id;
                        _constructionManager.SelectedBlockId = block.Id;
                    }

                    if (isSelected) ImGui.PopStyleColor();

                    if (ImGui.IsItemHovered())
                    {
                        hoveredBlockId = block.Id;
                    }

                    // Перенос на новую строку строго каждые 10 элементов
                    if ((i + 1) % columnsCount != 0 && (i + 1) < BlockRegistry.RegisteredBlocks.Count)
                    {
                        ImGui.SameLine();
                    }

                    ImGui.PopID();
                }
                ImGui.EndChild();

                ImGui.NextColumn();

                // ПРАВАЯ КОЛОНКА: Информационная карточка с полной локализацией
                BlockData infoBlock = BlockRegistry.GetById(hoveredBlockId);
                ImGui.TextColored(new float4(1f, 0.8f, 0f, 1f), LocalizationKeys.BlockInfoTitle.Localized);
                ImGui.Separator();
                ImGui.Spacing();

                // Исправлено ошибочное форматирование: заменяем вызов BlockInfoTitle на вывод имени блока из реестра
                ImGui.Text(infoBlock.Name);
                ImGui.Spacing();
                ImGui.Text(string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.BlockInfoGeometry.ToUtf8()), infoBlock.Shape));
                ImGui.Text(string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.BlockInfoMass.ToUtf8()), infoBlock.DefaultMass));
                ImGui.Text(string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.BlockInfoFriction.ToUtf8()), infoBlock.DefaultFriction));
            }
            ImGui.End();
        }

        ImGui.SetNextWindowPos(new float2(io.DisplaySize.X / 2f - 270f, io.DisplaySize.Y - 75f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(540, 70), ImGuiCond.Always);

        ImGuiWindowFlags hotbarFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                       ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                                       ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("HotbarWindow"u8, hotbarFlags))
        {
            ImGui.Columns(7, "slots"u8, false);
            for (int i = 0; i < 7; i++)
            {
                uint blockId = _hotbarSlots[i];
                var block = BlockRegistry.GetById(blockId);

                ImTextureRef blockTextureRef = _app.AssetManager.GetGeneratedIconRef(block.Id);
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(0, 0));

                float2 uv0 = new float2(0f, 1f);
                float2 uv1 = new float2(1f, 0f);

                if (ImGui.ImageButton($"##slotimg{i}", blockTextureRef, new float2(50, 50), uv0, uv1))
                {
                    _activeHotbarIndex = i;
                    _constructionManager.SelectedBlockId = blockId;
                }

                ImGui.PopStyleVar();

                if (i == _activeHotbarIndex)
                {
                    float2 pMin = ImGui.GetItemRectMin();
                    float2 pMax = ImGui.GetItemRectMax();
                    ImGui.GetWindowDrawList().AddRect(pMin, pMax, 0xFF00DFFF, 0f, 0, 2f);
                }
                ImGui.NextColumn();
            }
        }
        ImGui.End();

        ImGui.SetNextWindowPos(new float2(10, io.DisplaySize.Y - 320), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new float2(400, 230), ImGuiCond.Always);
        ImGuiWindowFlags chatFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;
        if (!_isChatOpen) chatFlags |= ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground;

        if (ImGui.Begin("ChatWindow"u8, chatFlags))
        {
            // Зона вывода сообщений
            ImGui.BeginChild("ChatHistory"u8, new float2(0, _isChatOpen ? -35 : 0), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
            lock (_chatMessages)
            {
                foreach (var msg in _chatMessages)
                {
                    ImGui.TextWrapped(msg);
                }
            }
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY()) ImGui.SetScrollHereY(1.0f);
            ImGui.EndChild();

            if (_isChatOpen)
            {
                ImGui.Separator();
                if (_chatNeedsFocus)
                {
                    ImGui.SetKeyboardFocusHere();
                    _chatNeedsFocus = false;
                }

                if (ImGui.InputText("##chatinput"u8, _chatInputBuffer, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    string rawText = System.Text.Encoding.UTF8.GetString(_chatInputBuffer).Split('\0')[0].Trim();
                    Array.Clear(_chatInputBuffer, 0, _chatInputBuffer.Length);

                    if (!string.IsNullOrEmpty(rawText))
                    {
                        ExecuteChatMessage(rawText);
                    }
                    _isChatOpen = false;
                    _isMouseCaptured = true;
                }
            }
        }
        ImGui.End();

        drawList = ImGui.GetForegroundDrawList();
        float2 center = new float2(io.DisplaySize.X / 2f, io.DisplaySize.Y / 2f);
        drawList.AddCircleFilled(center, 3f, 0xFFFFFFFF);
    }
    public void AddMessageLocally(string message)
    {
        lock (_chatMessages)
        {
            _chatMessages.Add(message);
            // Ограничиваем историю чата, например, до 50 сообщений, чтобы не забивать память
            if (_chatMessages.Count > 50)
            {
                _chatMessages.RemoveAt(0);
            }
        }
    }

    private void ExecuteChatMessage(string message)
    {
        if (message.StartsWith('/'))
        {
            // Обработка консольных команд сервера
            string[] args = message.Substring(1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0) return;

            string command = args[0].ToLower();

            if (!_app.Network.IsServer)
            {
                lock (_chatMessages) _chatMessages.Add(System.Text.Encoding.UTF8.GetString(LocalizationKeys.ChatSystemNoRights.ToUtf8()));
                return;
            }

            // ИСПРАВЛЕНИЕ: Безопасно собираем весь остаток строки после команды в один целевой аргумент (поддерживает пробелы)
            string targetParam = "";
            if (args.Length > 1)
            {
                targetParam = string.Join(' ', args, 1, args.Length - 1).Trim();
            }

            switch (command)
            {
                case "kick":
                    if (!string.IsNullOrEmpty(targetParam))
                    {
                        string targetNick = targetParam;
                        string targetAddressKey = null;

                        foreach (var kvp in _app.Network.RemotePlayerPositions)
                        {
                            if (kvp.Key.Contains('|') && kvp.Key.Split('|')[1].Equals(targetNick, StringComparison.OrdinalIgnoreCase))
                            {
                                targetAddressKey = kvp.Key.Split('|')[0];
                                break;
                            }
                        }

                        if (targetAddressKey != null)
                        {
                            // ИСПРАВЛЕНИЕ: Отправляем клиенту пакет кика
                            _app.Network.SendDisconnectionCommand(targetAddressKey, false);

                            string kickFmt = string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.ChatServerKicked.ToUtf8()), targetNick);
                            AddMessageLocally(kickFmt);
                        }
                        else
                        {
                            AddMessageLocally($"[Система] Игрок '{targetNick}' не найден на сервере.");
                        }
                    }
                    break;

                case "ban":
                    if (!string.IsNullOrEmpty(targetParam))
                    {
                        string targetNick = targetParam;
                        _bannedNicks.Add(targetNick);

                        string targetAddressKey = null;
                        foreach (var kvp in _app.Network.RemotePlayerPositions)
                        {
                            if (kvp.Key.Contains('|') && kvp.Key.Split('|')[1].Equals(targetNick, StringComparison.OrdinalIgnoreCase))
                            {
                                targetAddressKey = kvp.Key.Split('|')[0];
                                break;
                            }
                        }

                        if (targetAddressKey != null)
                        {
                            // ИСПРАВЛЕНИЕ: Отправляем клиенту пакет бана
                            _app.Network.SendDisconnectionCommand(targetAddressKey, true);
                        }

                        string banFmt = string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.ChatServerBannedNick.ToUtf8()), targetNick);
                        AddMessageLocally(banFmt);
                    }
                    break;

                case "banip":
                    if (!string.IsNullOrEmpty(targetParam))
                    {
                        string targetIp = targetParam;
                        _bannedIps.Add(targetIp);

                        // ИСПРАВЛЕНИЕ: Ищем всех игроков с таким IP адресом и отправляем им бан
                        foreach (var kvp in _app.Network.RemotePlayerPositions)
                        {
                            if (kvp.Key.Contains('|') && kvp.Key.StartsWith(targetIp))
                            {
                                _app.Network.SendDisconnectionCommand(targetIp, true);
                            }
                        }

                        string banIpFmt = string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.ChatServerBannedIp.ToUtf8()), targetIp);
                        AddMessageLocally(banIpFmt);
                    }
                    break;


                case "close":
                    lock (_chatMessages) _chatMessages.Add(System.Text.Encoding.UTF8.GetString(LocalizationKeys.ChatServerClosing.ToUtf8()));
                    _app.Network.Dispose();
                    _app.ChangeScene(GameState.MainMenu);
                    break;

                default:
                    string unknownCmdFmt = string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.ChatSystemUnknownCmd.ToUtf8()), command);
                    lock (_chatMessages) _chatMessages.Add(unknownCmdFmt);
                    break;
            }
        }
        else
        {
            // Обычное сообщение в чат
            string formatted = $"[{_app.Network.MyNickname}]: {message}";
            lock (_chatMessages) _chatMessages.Add(formatted);

            if (_app.Network.IsActive)
            {
                _app.Network.SendChatMessage(formatted);
            }
        }
    }

    private bool _lmbWasPressed = false;
    private bool _rmbWasPressed = false;
    private double2 _lastCursor;

    public void HandleInput(GlfwWindow window, double2 cursorDelta, bool shiftHeld, bool dragging)
    {
        // Переключение слотов хотбара цифровыми клавишами
        if (window.GetKey(GlfwKey.Number1) == GlfwKeyAction.Press) { _activeHotbarIndex = 0; _constructionManager.SelectedBlockId = _hotbarSlots[0]; }
        if (window.GetKey(GlfwKey.Number2) == GlfwKeyAction.Press) { _activeHotbarIndex = 1; _constructionManager.SelectedBlockId = _hotbarSlots[1]; }
        if (window.GetKey(GlfwKey.Number3) == GlfwKeyAction.Press) { _activeHotbarIndex = 2; _constructionManager.SelectedBlockId = _hotbarSlots[2]; }
        if (window.GetKey(GlfwKey.Number4) == GlfwKeyAction.Press) { _activeHotbarIndex = 3; _constructionManager.SelectedBlockId = _hotbarSlots[3]; }
        if (window.GetKey(GlfwKey.Number5) == GlfwKeyAction.Press) { _activeHotbarIndex = 4; _constructionManager.SelectedBlockId = _hotbarSlots[4]; }
        if (window.GetKey(GlfwKey.Number6) == GlfwKeyAction.Press) { _activeHotbarIndex = 5; _constructionManager.SelectedBlockId = _hotbarSlots[5]; }
        if (window.GetKey(GlfwKey.Number7) == GlfwKeyAction.Press) { _activeHotbarIndex = 6; _constructionManager.SelectedBlockId = _hotbarSlots[6]; }

        bool tPressed = window.GetKey(GlfwKey.T) == GlfwKeyAction.Press;
        if (tPressed && !_tWasPressed && _isMouseCaptured && !_isChatOpen)
        {
            _isChatOpen = true;
            _isMouseCaptured = false;
            _chatNeedsFocus = true;
            window.SetInputMode(GlfwInputMode.Cursor, Glfw.Constants.GLFW_CURSOR_NORMAL);
        }
        _tWasPressed = tPressed;

        // Если чат открыт, блокируем остальной игровой ввод (движение, строительство)
        if (_isChatOpen) return;

        // Открытие/Закрытие меню блоков на кнопку B
        bool bPressed = window.GetKey(GlfwKey.B) == GlfwKeyAction.Press;
        if (bPressed && !_bWasPressed)
        {
            _isBlockMenuOpen = !_isBlockMenuOpen;
            if (_isBlockMenuOpen && _isMouseCaptured)
            {
                _isMouseCaptured = false;
                window.SetInputMode(GlfwInputMode.Cursor, Glfw.Constants.GLFW_CURSOR_NORMAL);
            }
        }
        _bWasPressed = bPressed;

        bool fPressed = window.GetKey(GlfwKey.F) == GlfwKeyAction.Press;
        if (fPressed && !_fWasPressed) _player.ToggleNoclip();
        _fWasPressed = fPressed;

        // Освобождение мыши по Escape
        if (window.GetKey(GlfwKey.Escape) == GlfwKeyAction.Press && _isMouseCaptured)
        {
            _isMouseCaptured = false;
            window.SetInputMode(GlfwInputMode.Cursor, Glfw.Constants.GLFW_CURSOR_NORMAL);
            return;
        }

        bool lmbPressed = window.GetMouseButton(GlfwMouseButton.Number1) == GlfwButtonAction.Press;
        bool rmbPressed = window.GetMouseButton(GlfwMouseButton.Number2) == GlfwButtonAction.Press;

        // ЗАХВАТ МЫШИ
        if (lmbPressed && !_isMouseCaptured && !ImGui.GetIO().WantCaptureMouse)
        {
            _isMouseCaptured = true;

            // 1. Центрируем курсор ПЕРЕД переходом в режим DISABLED (критично для Linux)
            var size = window.FramebufferSize;
            double centerX = size.X / 2.0;
            double centerY = size.Y / 2.0;
            window.CursorPos = new double2(centerX, centerY);

            // 2. Сбрасываем кэш в MainApp, чтобы дельта следующего кадра была равна нулю
            _app.ResetLastCursor(new double2(centerX, centerY));

            // 3. Активируем режим DISABLED
            window.SetInputMode(GlfwInputMode.Cursor, Glfw.Constants.GLFW_CURSOR_DISABLED);

            _lmbWasPressed = true;
            return;
        }

        if (_isMouseCaptured)
        {
            // На Linux в режиме DISABLED дельта приходит чистой и безграничной. 
            // Больше никаких ручных SetCursorPos внутри кадра делать НЕЛЬЗЯ.
            _player.HandleMouseLook(cursorDelta);

            if (lmbPressed && !_lmbWasPressed)
            {
                _constructionManager.ExecuteConstructionRaycast(_player, true, SpawnCubeLocal);
            }
            if (rmbPressed && !_rmbWasPressed)
            {
                _constructionManager.ExecuteConstructionRaycast(_player, false, SpawnCubeLocal);
            }
        }

        _lmbWasPressed = lmbPressed;
        _rmbWasPressed = rmbPressed;
    }
    public void Dispose()
    {
        _app.Window.SetInputMode(GlfwInputMode.Cursor, Glfw.Constants.GLFW_CURSOR_NORMAL);
        _terrainManager.Dispose();
    }
}