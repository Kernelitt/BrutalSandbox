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

    private MeshData _graphicCubeMesh;
    private MeshData _graphicCapsuleMesh;

    private int _renderedChunksCount = 0;
    private bool _isMouseCaptured = false;
    private bool _fWasPressed = false;
    private float _networkTickTimer = 0f;
    private const float NetworkTickRate = 1f / 30f;
    private uint _buildIdCounter = 5000;      
    private uint _platformIdCounter = 10000;  

    private float _buildMass = 5.0f;      
    private float _buildFriction = 0.3f; 
    private bool _buildIsStatic = false;
    private bool _buildIsWelded = false;
    public struct WeldLink
    {
        public uint ParentNetId;
        public float3 LocalOffset;   
        public floatQuat LocalRotation; 
    }


    private readonly Dictionary<uint, WeldLink> _weldLinks = new();



    private readonly Dictionary<uint, uint> _networkedCubes = new();
    private readonly Dictionary<uint, LerpedCubeVisual> _cubeVisuals = new();
    public GameplayScene(MainApp app) => _app = app;

    public void Initialize()
    {
        _graphicCubeMesh = PrimitiveGenerator.CreateCube();
        _graphicCapsuleMesh = PrimitiveGenerator.CreateCapsule(1.0f, 0.4f);

        _player.Initialize();

        if (!_app.Network.IsActive || _app.Network.IsServer)
        {
            BuildPhysicsSceneFromServer();
        }
    }

    private void BuildPhysicsSceneFromServer()
    {
        var phys = PhysicsManager.Instance;
        var boxHalfSize = new float3(0.5f);
        uint networkIdCounter = 1000;

        using (var boxShapeSettings = BoxShapeSettings.Create(boxHalfSize, 0.05f, default))
        {
            foreach (int i in System.Linq.Enumerable.Range(0, 25))
            {
                uint bodyId = phys.CreateBody(
                    boxShapeSettings,
                    new float3(32f, 45f + (i * 2.2f), 35f),
                    floatQuat.Identity,
                    MotionType.Dynamic,
                    PhysicsManager.LayerMoving,
                    "stone_wall"
                );

                _networkedCubes[networkIdCounter] = bodyId;
                networkIdCounter++;
            }
        }
    }

    public float4x4 GetViewProjection(float aspectRatio)
    {
        float4x4 view = _player.GetViewMatrix();
        float4x4 projection = float4x4.CreatePerspectiveFieldOfView(MathF.PI / 3.5f, aspectRatio, 0.1f, 1000f);
        return view * projection;
    }

    public unsafe void Update(float deltaTime)
    {
        _app.Network.Update();

        PhysicsManager.Instance.Update(deltaTime);

        while (_app.Network.IncomingBuildCommands.TryDequeue(out var buildCmd))
        {
            if (!_networkedCubes.ContainsKey(buildCmd.NetId))
            {
                SpawnCubeLocal(buildCmd.NetId, buildCmd.Position, buildCmd.Mass, buildCmd.Friction, buildCmd.IsStatic, buildCmd.ParentNetId);
            }
        }

        // 3. Считываем команды разрушения
        while (_app.Network.IncomingDestroyCommands.TryDequeue(out var netId))
        {
            if (_networkedCubes.TryGetValue(netId, out uint bodyId))
            {
                var phys = PhysicsManager.Instance;

                _weldLinks.Remove(netId);

                phys.BodyInterface.RemoveBody(bodyId);
                phys.BodyInterface.DestroyBody(bodyId);
                _networkedCubes.Remove(netId);
            }
        }

        if (!_app.Network.IsActive || _app.Network.IsServer)
        {
            var phys = PhysicsManager.Instance;
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
                // КЛИЕНТ: Корректируем физику Jolt и гасим фантомные кочки ландшафта
                var phys = PhysicsManager.Instance;
                var boxHalfSize = new float3(0.5f);

                foreach (var kvp in _app.Network.NetworkedCubesState)
                {
                    uint netId = kvp.Key;
                    NetworkCubeState serverState = kvp.Value;

                    if (!_networkedCubes.TryGetValue(netId, out uint bodyId))
                    {
                        using (var boxShapeSettings = BoxShapeSettings.Create(boxHalfSize, 0.05f, default))
                        {
                            bodyId = phys.CreateBody(boxShapeSettings, serverState.Position, serverState.Rotation, MotionType.Dynamic, PhysicsManager.LayerMoving, "stone_wall");
                        }
                        phys.BodyInterface.ActivateBody(bodyId);
                        _networkedCubes[netId] = bodyId;
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

                        float3 currentLocalVel = phys.BodyInterface.GetLinearVelocity(bodyId);

                        if (currentLocalVel.Y > 1.5f && serverState.LinearVelocity.Y <= 0.2f)
                        {
                            targetLinearVel.Y = serverState.LinearVelocity.Y;
                        }

                        phys.BodyInterface.SetLinearVelocity(bodyId, targetLinearVel);
                        phys.BodyInterface.SetAngularVelocity(bodyId, serverState.AngularVelocity);
                        phys.BodyInterface.SetPositionAndRotation(bodyId, clientPos, serverState.Rotation, Activation.DontActivate);
                    }
                }
            }

        }

        _player.UpdateLogic(_app.Window, deltaTime, _terrainManager);

        if (_app.Network.IsActive)
        {
            _app.Network.SendMyTransform(_player.Position);
        }
    }



    private void SpawnCubeLocal(uint netId, float3 position, float mass, float friction, bool isStatic, uint parentNetId)
    {
        var phys = PhysicsManager.Instance;
        float3 boxHalfSize = isStatic ? new float3(1.5f, 0.1f, 1.5f) : new float3(0.5f);
        MotionType motionType = isStatic ? MotionType.Static : MotionType.Dynamic;
        ushort layer = isStatic ? PhysicsManager.LayerStatic : PhysicsManager.LayerMoving;

        float3 finalSpawnPos = position;

        if (parentNetId != 0 && _networkedCubes.TryGetValue(parentNetId, out uint parentBodyId))
        {
            phys.BodyInterface.GetPositionAndRotation(parentBodyId, out float3 parentPos, out floatQuat parentRot);

            float3 worldOffset = position - parentPos;
            floatQuat invParentRot = floatQuat.Inverse(parentRot);
            float3 localOffset = float3.Transform(worldOffset, invParentRot);

            bool isParentPlatform = parentNetId >= 10000;

            localOffset.X = MathF.Round(localOffset.X);
            localOffset.Z = MathF.Round(localOffset.Z);

            if (isParentPlatform)
            {
                float targetStep = 0.6f;
                localOffset.Y = MathF.Round(localOffset.Y / targetStep) * targetStep;
            }
            else
            {
                localOffset.Y = MathF.Round(localOffset.Y);
            }

            finalSpawnPos = parentPos + float3.Transform(localOffset, parentRot);

            _weldLinks[netId] = new WeldLink
            {
                ParentNetId = parentNetId,
                LocalOffset = localOffset,
                LocalRotation = floatQuat.Identity
            };
        }

        using (var boxShapeSettings = BoxShapeSettings.Create(boxHalfSize, 0.05f, default))
        {
            uint bodyId = phys.CreateBody(boxShapeSettings, finalSpawnPos, floatQuat.Identity, motionType, layer, "stone_wall");
            phys.BodyInterface.SetFriction(bodyId, friction);

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

    private unsafe void ExecuteConstructionRaycast(bool isPlaceMode)
    {
        float cosPitch = MathF.Cos(_player.Pitch);
        float sinPitch = MathF.Sin(_player.Pitch);
        float cosYaw = MathF.Cos(_player.Yaw);
        float sinYaw = MathF.Sin(_player.Yaw);

        float3 forward = new float3(sinYaw * cosPitch, sinPitch, cosYaw * cosPitch);
        float3 rayStart = _player.Position + new float3(0f, 0.88f, 0f); // Из глаз
        float maxRayDistance = 15f;

        var phys = PhysicsManager.Instance;

        uint hitBodyId = 0;
        float closestT = 1.0f; 
        float3 hitNormal = new float3(0f, 1f, 0f);

        foreach (var kvp in _networkedCubes)
        {
            uint bodyId = kvp.Value;
            if (bodyId == _player.PhysicsBodyId) continue;

            phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 center, out floatQuat rotation);

            bool isPlatform = phys.BodyInterface.GetMotionType(bodyId) == MotionType.Static;

            float3 halfExtents = isPlatform ? new float3(1.5f, 0.1f, 1.5f) : new float3(0.5f);

            float3 boxMin = center - halfExtents;
            float3 boxMax = center + halfExtents;

            if (RayCasting.RayIntersectsScaledBox(rayStart, forward, boxMin, boxMax, out float t, out float3 normal))
            {
                if (t < closestT && t > 0.01f)
                {
                    closestT = t;
                    hitBodyId = bodyId;
                    hitNormal = normal;
                }
            }
        }

        float3 hitPoint;
        uint targetNetId = 0;
        bool hasHit = false;
        hitPoint = rayStart + (forward * (closestT * maxRayDistance));

        if (hitBodyId != 0)
        {
            hasHit = true;
            foreach (var kvp in _networkedCubes)
            {
                if (kvp.Value == hitBodyId) { targetNetId = kvp.Key; break; }
            }
        }
        else
        {
            for (float d = 1f; d <= maxRayDistance; d += 0.1f)
            {
                float3 checkPoint = rayStart + (forward * d);
                float terrainHeight = _terrainManager.GetTerrainHeightAt(checkPoint.X, checkPoint.Z);

                if (checkPoint.Y <= terrainHeight)
                {
                    hitPoint = checkPoint;
                    hitNormal = new float3(0f, 1f, 0f);
                    hasHit = true;
                    break;
                }
            }
        }

        if (!hasHit) return;

        if (isPlaceMode)
        {
            float offsetDistance = _buildIsStatic ? 0.1f : 0.5f;
            float3 spawnPos = hitPoint + (hitNormal * offsetDistance);

            uint parentCubeId = (_buildIsWelded && !_buildIsStatic && hitBodyId != 0) ? targetNetId : 0;

            if (parentCubeId == 0)
            {
                spawnPos = new float3(MathF.Round(spawnPos.X), MathF.Round(spawnPos.Y), MathF.Round(spawnPos.Z));
                if (hitBodyId == 0 && hitNormal.Y > 0.9f) spawnPos.Y += 0.02f;
            }

            uint newNetId = _buildIsStatic ? _platformIdCounter++ : _buildIdCounter++;

            if (_app.Network.IsActive)
            {
                if (_app.Network.IsServer)
                {
                    SpawnCubeLocal(newNetId, spawnPos, _buildMass, _buildFriction, _buildIsStatic, parentCubeId);
                    _app.Network.SendBuildOrDestroyToAll(newNetId, spawnPos, true, default, _buildMass, _buildFriction, _buildIsStatic, parentCubeId);
                }
                else
                {
                    _app.Network.SendBuildOrDestroyToAll(newNetId, spawnPos, true, default, _buildMass, _buildFriction, _buildIsStatic, parentCubeId);
                }
            }
            else
            {
                SpawnCubeLocal(newNetId, spawnPos, _buildMass, _buildFriction, _buildIsStatic, parentCubeId);
            }
        }
        else
        {
            if (hitBodyId != 0 && targetNetId >= 1000)
            {
                if (_app.Network.IsActive)
                {
                    if (_app.Network.IsServer)
                    {
                        phys.BodyInterface.RemoveBody(hitBodyId);
                        phys.BodyInterface.DestroyBody(hitBodyId);
                        _networkedCubes.Remove(targetNetId);

                        _app.Network.SendBuildOrDestroyToAll(targetNetId, float3.Zero, false, default);
                    }
                    else
                    {
                        _app.Network.SendBuildOrDestroyToAll(targetNetId, float3.Zero, false, default);
                    }
                }
                else
                {
                    phys.BodyInterface.RemoveBody(hitBodyId);
                    phys.BodyInterface.DestroyBody(hitBodyId);
                    _networkedCubes.Remove(targetNetId);
                }
            }
        }
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

            phys.BodyInterface.GetPositionAndRotation(bodyId, out var position, out var rotation);
            if (!_frustum.IsBoxVisible(position - new float3(2f), position + new float3(2f))) continue;

            // Определяем тип движения тела для масштабирования меша
            bool isObjectStatic = phys.BodyInterface.GetMotionType(bodyId) == MotionType.Static;
            bool isWidePlatform = phys.BodyInterface.GetMotionType(bodyId) == MotionType.Static && netId >= 10000;

            float4x4 scale = isWidePlatform ? float4x4.CreateScale(3.0f, 0.2f, 3.0f) : float4x4.Identity;
            float4x4 model = scale * float4x4.CreateFromQuaternion(rotation) * float4x4.CreateTranslation(position);
            renderer.RenderMesh(_graphicCubeMesh, model, "stone_wall");
        }

        foreach (var (addressStr, pos) in _app.Network.RemotePlayerPositions)
        {
            if (!_frustum.IsBoxVisible(pos - new float3(1f), pos + new float3(2f))) continue;

            float4x4 model = float4x4.CreateTranslation(pos);
            renderer.RenderMesh(_graphicCapsuleMesh, model, "stone_wall");
        }
    }

    public void RenderUi()
    {
        ImGui.SetNextWindowPos(new float2(10, 10), ImGuiCond.FirstUseEver);

        // Заголовок панели игрока берется из старого общего класса или можно оставить сырым/кастомным
        if (ImGui.Begin("Панель игрока"u8))
        {
            if (ImGui.Button(LocalizationKeys.GameplayBackToMenu.ToUtf8())) _app.ChangeScene(GameState.MainMenu);
            ImGui.Separator();

            // Динамическое форматирование количества чанков
            ImGui.Text(string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayRenderedChunks.ToUtf8()), _renderedChunksCount));

            // Координаты позиции игрока
            ImGui.Text(string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayPosition.ToUtf8()), _player.Position.X.ToString("F1"), _player.Position.Y.ToString("F1"), _player.Position.Z.ToString("F1")));

            // Статус режима полета
            string flightStatus = _player.IsNoclip ? System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayOn.ToUtf8()) : System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayOff.ToUtf8());
            ImGui.Text(string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.GameplayFlightMode.ToUtf8()), flightStatus));

            ImGui.Separator();
            ImGui.Text(LocalizationKeys.GameplayBuildSettings.ToUtf8());

            ImGui.Checkbox(LocalizationKeys.GameplayStaticObject.ToUtf8(), ref _buildIsStatic);
            if (!_buildIsStatic)
            {
                ImGui.Checkbox(LocalizationKeys.GameplayWeldObject.ToUtf8(), ref _buildIsWelded);
                ImGui.SliderFloat(LocalizationKeys.GameplayObjectMass.ToUtf8(), ref _buildMass, 0.1f, 500f, "%.1f"u8);
            }
            else
            {
                _buildIsWelded = false;
            }

            ImGui.SliderFloat(LocalizationKeys.GameplayFrictionCoeff.ToUtf8(), ref _buildFriction, 0.0f, 1.0f, "%.2f"u8);
        }
        ImGui.End();

        var drawList = ImGui.GetForegroundDrawList();
        var io = ImGui.GetIO();
        float2 center = new float2(io.DisplaySize.X / 2f, io.DisplaySize.Y / 2f);
        drawList.AddCircleFilled(center, 3f, 0xFFFFFFFF);
    }

    private bool _lmbWasPressed = false;
    private bool _rmbWasPressed = false;

    public void HandleInput(GlfwWindow window, double2 cursorDelta, bool shiftHeld, bool dragging)
    {
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
                ExecuteConstructionRaycast(true);
            }
            if (rmbPressed && !_rmbWasPressed)
            {
                ExecuteConstructionRaycast(false);
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