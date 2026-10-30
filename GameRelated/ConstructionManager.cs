using Brutal.JoltApi;
using Brutal.Numerics;
using BrutalSandbox.Framework.Graphics;
using BrutalSandbox.Framework.Physics;
using BrutalSandbox.Framework.Terrain;
using BrutalSandbox.Framework.UI;
using BrutalSandbox.GameRelated;
using System;
using System.Collections.Generic;

namespace BrutalSandbox;

public sealed class ConstructionManager
{
    private readonly MainApp _app;
    private readonly TerrainManager _terrainManager;
    private readonly Dictionary<uint, uint> _networkedCubes;
    private readonly Dictionary<uint, GameplayScene.WeldLink> _weldLinks;
    private readonly Dictionary<uint, uint> _cubeBlockIds;

    private uint _buildIdCounter = 5000;
    private uint _platformIdCounter = 10000;

    public uint SelectedBlockId { get; set; } = 1;
    public float BuildMass { get; set; } = 5.0f;
    public float BuildFriction { get; set; } = 0.3f;
    public bool BuildIsStatic { get; set; } = false;
    public bool BuildIsWelded { get; set; } = false;

    public ConstructionManager(
        MainApp app,
        TerrainManager terrainManager,
        Dictionary<uint, uint> networkedCubes,
        Dictionary<uint, uint> cubeBlockIds,
        Dictionary<uint, GameplayScene.WeldLink> weldLinks)
    {
        _app = app;
        _terrainManager = terrainManager;
        _networkedCubes = networkedCubes;
        _cubeBlockIds = cubeBlockIds;
        _weldLinks = weldLinks;
    }

    public void ExecuteConstructionRaycast(FirstPersonController player, bool isPlaceMode, Action<uint, float3, float, float, bool, uint, uint> spawnLocalCallback)
    {
        float cosPitch = MathF.Cos(player.Pitch);
        float sinPitch = MathF.Sin(player.Pitch);
        float cosYaw = MathF.Cos(player.Yaw);
        float sinYaw = MathF.Sin(player.Yaw);

        float3 forward = new float3(sinYaw * cosPitch, sinPitch, cosYaw * cosPitch);
        float3 rayStart = player.Position + new float3(0f, 0.88f, 0f);
        float maxRayDistance = 15f;

        var phys = PhysicsManager.Instance;

        uint hitBodyId = 0;
        float closestT = 1.0f;
        float3 hitNormal = new float3(0f, 1f, 0f);
        uint targetNetId = 0;

        foreach (var kvp in _networkedCubes)
        {
            uint bodyId = kvp.Value;
            if (bodyId == player.PhysicsBodyId) continue;

            phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 center, out floatQuat rotation);

            bool isPlatform = false;
            if (_cubeBlockIds.TryGetValue(kvp.Key, out uint blockTypeId))
            {
                isPlatform = BlockRegistry.GetById(blockTypeId).Shape == BlockShape.Platform;
            }
            else
            {
                isPlatform = phys.BodyInterface.GetMotionType(bodyId) == MotionType.Static;
            }

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
                    targetNetId = kvp.Key;
                }
            }
        }

        float3 hitPoint = rayStart + (forward * (closestT * maxRayDistance));
        bool hasHit = false;

        if (hitBodyId != 0)
        {
            hasHit = true;
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
            BlockData currentBlock = BlockRegistry.GetById(SelectedBlockId);
            bool finalIsStatic = BuildIsStatic;

            float3 safeHitPoint = hitPoint - (forward * 0.005f);

            // Правильные полуразмеры для текущего блока по осям
            float3 currentExtents = (currentBlock.Shape == BlockShape.Platform) ? new float3(1.5f, 0.1f, 1.5f) : new float3(0.5f);

            float3 targetExtents = float3.Zero;
            bool targetIsPlatform = false;
            float3 targetCenter = float3.Zero;

            if (hitBodyId != 0)
            {
                phys.BodyInterface.GetPositionAndRotation(hitBodyId, out targetCenter, out _);
                if (_cubeBlockIds.TryGetValue(targetNetId, out uint blockTypeId))
                {
                    targetIsPlatform = BlockRegistry.GetById(blockTypeId).Shape == BlockShape.Platform;
                }
                else
                {
                    targetIsPlatform = phys.BodyInterface.GetMotionType(hitBodyId) == MotionType.Static;
                }

                targetExtents = targetIsPlatform ? new float3(1.5f, 0.1f, 1.5f) : new float3(0.5f);
            }

            // ВЫЧИСЛЕНИЕ ПРАВИЛЬНОГО ОТСТУПА С УЧЕТОМ НАПРАВЛЕНИЯ НОРМАЛИ
            // Теперь для горизонтальных граней платформ отступ будет честные 3.0 метра (1.5 + 1.5)
            float offsetDistance = 0.0f;
            if (hitBodyId != 0)
            {
                if (MathF.Abs(hitNormal.X) > 0.8f) offsetDistance = targetExtents.X + currentExtents.X;
                else if (MathF.Abs(hitNormal.Y) > 0.8f) offsetDistance = targetExtents.Y + currentExtents.Y;
                else if (MathF.Abs(hitNormal.Z) > 0.8f) offsetDistance = targetExtents.Z + currentExtents.Z;
            }
            else
            {
                // На земле берем просто полувысоту текущего блока
                offsetDistance = currentExtents.Y;
            }

            float3 spawnPos = safeHitPoint + (hitNormal * offsetDistance);
            uint parentCubeId = (BuildIsWelded && !finalIsStatic && hitBodyId != 0) ? targetNetId : 0;

            if (parentCubeId == 0)
            {
                if (hitBodyId != 0)
                {
                    // Строим относительно родителя
                    float3 localSpawn = spawnPos - targetCenter;

                    float stepX = (targetIsPlatform && currentBlock.Shape == BlockShape.Platform) ? 3.0f : 1.0f;
                    float stepZ = (targetIsPlatform && currentBlock.Shape == BlockShape.Platform) ? 3.0f : 1.0f;

                    float gridX = MathF.Round(localSpawn.X / stepX, MidpointRounding.AwayFromZero) * stepX;
                    float gridY = localSpawn.Y;
                    float gridZ = MathF.Round(localSpawn.Z / stepZ, MidpointRounding.AwayFromZero) * stepZ;

                    if (MathF.Abs(hitNormal.X) > 0.8f)
                    {
                        gridX = hitNormal.X * offsetDistance;
                    }
                    if (MathF.Abs(hitNormal.Z) > 0.8f)
                    {
                        gridZ = hitNormal.Z * offsetDistance;
                    }
                    if (MathF.Abs(hitNormal.Y) > 0.8f)
                    {
                        if (currentBlock.Shape == BlockShape.Platform)
                        {
                            float localY = hitNormal.Y > 0f ? MathF.Floor(localSpawn.Y * 5f) / 5f : MathF.Ceiling(localSpawn.Y * 5f) / 5f;
                            gridY = MathF.Round(localY * 5f, MidpointRounding.AwayFromZero) / 5f;
                        }
                        else
                        {
                            gridY = hitNormal.Y * offsetDistance;
                        }
                    }
                    else
                    {
                        gridY = 0f;
                    }

                    spawnPos = targetCenter + new float3(gridX, gridY, gridZ);
                }
                else
                {
                    // Строим на голой земле
                    float stepX = (currentBlock.Shape == BlockShape.Platform) ? 3.0f : 1.0f;
                    float stepZ = (currentBlock.Shape == BlockShape.Platform) ? 3.0f : 1.0f;

                    float gridX = MathF.Round(spawnPos.X / stepX, MidpointRounding.AwayFromZero) * stepX;
                    float gridY = spawnPos.Y;
                    float gridZ = MathF.Round(spawnPos.Z / stepZ, MidpointRounding.AwayFromZero) * stepZ;

                    if (MathF.Abs(hitNormal.X) > 0.8f) gridX = MathF.Round(spawnPos.X / stepX, MidpointRounding.AwayFromZero) * stepX;
                    if (MathF.Abs(hitNormal.Z) > 0.8f) gridZ = MathF.Round(spawnPos.Z / stepZ, MidpointRounding.AwayFromZero) * stepZ;

                    if (MathF.Abs(hitNormal.Y) > 0.8f)
                    {
                        if (currentBlock.Shape == BlockShape.Platform)
                        {
                            float localY = hitNormal.Y > 0f ? MathF.Floor(spawnPos.Y * 5f) / 5f : MathF.Ceiling(spawnPos.Y * 5f) / 5f;
                            gridY = MathF.Round(localY * 5f, MidpointRounding.AwayFromZero) / 5f;
                        }
                        else
                        {
                            gridY = MathF.Round(spawnPos.Y, MidpointRounding.AwayFromZero);
                        }
                    }
                    else
                    {
                        gridY = MathF.Round(spawnPos.Y, MidpointRounding.AwayFromZero);
                    }

                    spawnPos = new float3(gridX, gridY, gridZ);

                    if (hitBodyId == 0 && hitNormal.Y > 0.9f)
                    {
                        float extX = currentExtents.X;
                        float extZ = currentExtents.Z;

                        float h1 = _terrainManager.GetTerrainHeightAt(spawnPos.X - extX, spawnPos.Z - extZ);
                        float h2 = _terrainManager.GetTerrainHeightAt(spawnPos.X + extX, spawnPos.Z - extZ);
                        float h3 = _terrainManager.GetTerrainHeightAt(spawnPos.X - extX, spawnPos.Z + extZ);
                        float h4 = _terrainManager.GetTerrainHeightAt(spawnPos.X + extX, spawnPos.Z + extZ);
                        float hCenter = _terrainManager.GetTerrainHeightAt(spawnPos.X, spawnPos.Z);

                        float maxHeight = MathF.Max(hCenter, MathF.Max(MathF.Max(h1, h2), MathF.Max(h3, h4)));

                        // Ставим объект четко на самую высокую точку ландшафта под ним
                        spawnPos.Y = maxHeight + currentExtents.Y + 0.05f;
                    }
                }
            }

            uint idOffset = _app.Network.IsActive && !_app.Network.IsServer ? 500000u : 0u;
            uint newNetId = (currentBlock.Shape == BlockShape.Platform)
                ? (_platformIdCounter++ + idOffset)
                : (_buildIdCounter++ + idOffset);

            if (_app.Network.IsActive)
            {
                if (_app.Network.IsServer)
                {
                    Console.WriteLine($"[ОТЛАДКА ХОСТА] Локальная постройка. ID: {newNetId}, Static: {finalIsStatic}");
                    _app.Network.SendBuildOrDestroyToAll(newNetId, spawnPos, true, default, SelectedBlockId, parentCubeId, finalIsStatic);
                    spawnLocalCallback(newNetId, spawnPos, currentBlock.DefaultMass, currentBlock.DefaultFriction, finalIsStatic, parentCubeId, SelectedBlockId);
                }
                else
                {
                    // Клиент отправляет флаг статичности на сервер (вызов измененного метода с finalIsStatic)
                    Console.WriteLine($"[ОТЛАДКА КЛИЕНТА] Отправка запроса BuildObject на сервер. Врем. ID: {newNetId}, Static: {finalIsStatic}");
                    _app.Network.SendBuildOrDestroyToAll(newNetId, spawnPos, true, default, SelectedBlockId, parentCubeId, finalIsStatic);
                }
            }
            else
            {
                spawnLocalCallback(newNetId, spawnPos, currentBlock.DefaultMass, currentBlock.DefaultFriction, finalIsStatic, parentCubeId, SelectedBlockId);
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
                        // ХОСТ: удаляет мгновенно и рассылает всем
                        Console.WriteLine($"[ОТЛАДКА УДАЛЕНИЯ] Хост удаляет блок ID: {targetNetId}");
                        _weldLinks.Remove(targetNetId);
                        phys.BodyInterface.RemoveBody(hitBodyId);
                        phys.BodyInterface.DestroyBody(hitBodyId);
                        _networkedCubes.Remove(targetNetId);

                        // Рассылаем пакет удаления всем клиентам
                        _app.Network.SendBuildOrDestroyToAll(targetNetId, float3.Zero, false, default);
                    }
                    else
                    {
                        // КЛИЕНТ: Только отправляет запрос на сервер. Локально ничего не удаляет!
                        // Наш исправленный SendBuildOrDestroyToAll сам перенаправит пакет на _serverAddress
                        Console.WriteLine($"[ОТЛАДКА УДАЛЕНИЯ] Клиент отправляет запрос на удаление блока ID: {targetNetId}");
                        _app.Network.SendBuildOrDestroyToAll(targetNetId, float3.Zero, false, default);
                    }
                }
                else
                {
                    // Одиночная игра: удаляем сразу
                    _weldLinks.Remove(targetNetId);
                    phys.BodyInterface.RemoveBody(hitBodyId);
                    phys.BodyInterface.DestroyBody(hitBodyId);
                    _networkedCubes.Remove(targetNetId);
                }
            }
        }


    }
    public uint AllocateServerId(bool isPlatform)
    {
        return isPlatform ? _platformIdCounter++ : _buildIdCounter++;
    }
}