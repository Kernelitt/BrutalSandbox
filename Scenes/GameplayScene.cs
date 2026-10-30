using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.JoltApi;
using Brutal.Numerics;
using Brutal.RakNetApi;
using System;
using System.Collections.Generic;
using BrutalSandbox.Framework.Graphics;
using BrutalSandbox.Framework.Localization;
using BrutalSandbox.Framework.Network;
using BrutalSandbox.Framework.Physics;
using BrutalSandbox.Framework.Scenes;
using BrutalSandbox.Framework.Terrain;
using BrutalSandbox.Framework.UI;
using BrutalSandbox.GameRelated;

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

    // Авторитарная генерация ID на стороне сервера для клиентских построек

    public GameplayScene(MainApp app)
    {
        _app = app;
        _constructionManager = new ConstructionManager(_app, _terrainManager, _networkedCubes, _cubeBlockIds, _weldLinks);
    }


    public void Initialize()
    {
        _graphicCubeMesh = PrimitiveGenerator.CreateCube();
        _graphicCapsuleMesh = PrimitiveGenerator.CreateCapsule(1.0f, 0.4f);

        _player.Initialize();
    }
    public float4x4 GetViewProjection(float aspectRatio)
    {
        float4x4 view = _player.GetViewMatrix();
        float4x4 projection = float4x4.CreatePerspectiveFieldOfView(MathF.PI / 3.5f, aspectRatio, 0.1f, 1000f);
        return view * projection;
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

                // СИНХРОНИЗАЦИЯ АКТИВНЫХ ОБЪЕКТОВ ДЛЯ КЛИЕНТА
                var receivedNetIds = new HashSet<uint>();

                // Важно: Безопасно проверяем, есть ли вообще данные от сервера в этом кадре
                bool hasNetworkState = _app.Network.NetworkedCubesState.Count > 0;

                foreach (var kvp in _app.Network.NetworkedCubesState)
                {
                    uint netId = kvp.Key;
                    NetworkCubeState serverState = kvp.Value;
                    receivedNetIds.Add(netId);

                    if (!_networkedCubes.TryGetValue(netId, out uint bodyId))
                    {
                        continue; // Ждем BuildObject, это правильно и защищает от кубов-пустышек
                    }

                    if (phys.BodyInterface.GetMotionType(bodyId) == MotionType.Static)
                    {
                        continue;
                    }

                    phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 clientPos, out _);

                    float3 posError = serverState.Position - clientPos;
                    float distErrorSq = float3.Dot(posError, posError);

                    if (distErrorSq > 16f)
                    {
                        phys.BodyInterface.SetPositionAndRotation(bodyId, serverState.Position, serverState.Rotation, Activation.Activate);
                        phys.BodyInterface.SetLinearVelocity(bodyId, serverState.LinearVelocity);
                        phys.BodyInterface.SetAngularVelocity(bodyId, serverState.AngularVelocity);
                    }
                    else
                    {
                        float3 targetLinearVel = serverState.LinearVelocity + (posError * 5.0f);
                        phys.BodyInterface.SetLinearVelocity(bodyId, targetLinearVel);
                        phys.BodyInterface.SetAngularVelocity(bodyId, serverState.AngularVelocity);
                        phys.BodyInterface.SetPositionAndRotation(bodyId, clientPos, serverState.Rotation, Activation.Activate);
                    }
                }


            }
        }

    


    _player.UpdateLogic(_app.Window, deltaTime, _terrainManager);

        foreach (var kvp in _networkedCubes)
        {
            uint netId = kvp.Key;
            uint bodyId = kvp.Value;

            // Если это не тело игрока, считываем актуальные координаты из симуляции Jolt
            if (bodyId != _player.PhysicsBodyId)
            {
                phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 realPos, out floatQuat realRot);

                // Если у вас в LerpedCubeVisual или другом кэше обновляются позиции — 
                // сохраняем их сюда, чтобы метод Render и рейкаст использовали строго ОДИНАКОВЫЕ координаты.
                if (_cubeVisuals.TryGetValue(netId, out var visual))
                {
                    visual.CurrentPos = realPos;
                    visual.CurrentRot = realRot;
                    _cubeVisuals[netId] = visual;
                }
                else
                {
                    _cubeVisuals[netId] = new LerpedCubeVisual
                    {
                        CurrentPos = realPos,
                        CurrentRot = realRot
                    };
                }
            }
        }

        if (_app.Network.IsActive)
        {
            _app.Network.SendMyTransform(_player.Position);
        }
    }



    // ИСПРАВЛЕНИЕ: Передаем blockId аргументом, чтобы не читать его из чужого хотбара
    private void SpawnCubeLocal(uint netId, float3 position, float mass, float friction, bool isStatic, uint parentNetId, uint blockId)
    {
        var phys = PhysicsManager.Instance;
        MotionType motionType = isStatic ? MotionType.Static : MotionType.Dynamic;
        ushort layer = isStatic ? PhysicsManager.LayerStatic : PhysicsManager.LayerMoving;

        float3 finalSpawnPos = position;

        // СТРОКУ НИЖЕ УДАЛЯЕМ/ЗАМЕНЯЕМ НА ИСПОЛЬЗОВАНИЕ ПАРАМЕТРА blockId
        // uint blockId = _constructionManager.SelectedBlockId; 

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
            phys.BodyInterface.GetPositionAndRotation(bodyId, out var p, out var r);
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
        ImGui.SetNextWindowPos(new float2(10, 10), ImGuiCond.FirstUseEver);

        // ИСПРАВЛЕНИЕ: Локализован заголовок панели игрока
        if (ImGui.Begin(LocalizationKeys.GameplayPlayerPanelTitle.ToUtf8()))
        {
            if (ImGui.Button(LocalizationKeys.GameplayBackToMenu.ToUtf8())) _app.ChangeScene(GameState.MainMenu);
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

        // Хотбар на нижней грани экрана
        var io = ImGui.GetIO();
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

        var drawList = ImGui.GetForegroundDrawList();
        float2 center = new float2(io.DisplaySize.X / 2f, io.DisplaySize.Y / 2f);
        drawList.AddCircleFilled(center, 3f, 0xFFFFFFFF);
    }


    private bool _lmbWasPressed = false;
    private bool _rmbWasPressed = false;

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

        // Открытие/Закрытие меню блоков на кнопку B
        bool bPressed = window.GetKey(GlfwKey.B) == GlfwKeyAction.Press;
        if (bPressed && !_bWasPressed)
        {
            _isBlockMenuOpen = !_isBlockMenuOpen;
            // Если открыли меню блоков, освобождаем мышь для кликов в ImGui
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

        if (window.GetKey(GlfwKey.Escape) == GlfwKeyAction.Press && _isMouseCaptured)
        {
            _isMouseCaptured = false;
            window.SetInputMode(GlfwInputMode.Cursor, Glfw.Constants.GLFW_CURSOR_NORMAL);
            return;
        }

        bool lmbPressed = window.GetMouseButton(GlfwMouseButton.Number1) == GlfwButtonAction.Press;
        bool rmbPressed = window.GetMouseButton(GlfwMouseButton.Number2) == GlfwButtonAction.Press;

        if (lmbPressed && !_isMouseCaptured && !ImGui.GetIO().WantCaptureMouse)
        {
            _isMouseCaptured = true;
            window.SetInputMode(GlfwInputMode.Cursor, Glfw.Constants.GLFW_CURSOR_DISABLED);
            _lmbWasPressed = true;
            return;
        }

        if (_isMouseCaptured)
        {
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