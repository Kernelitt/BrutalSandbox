using System;
using System.Collections.Generic;
using Brutal.Numerics;
using BrutalSandbox.Framework.Graphics;

namespace BrutalSandbox.Framework.Terrain;

public sealed class TerrainManager : IDisposable
{
    private readonly Dictionary<int2, TerrainChunk> _activeChunks = [];
    private readonly List<TerrainChunk> _visibleChunks = [];

    private int2 _lastCenterChunkPos = new int2(int.MaxValue, int.MaxValue);
    private int _lastViewRadius = -1;

    public int ViewRadius { get; set; } = 6;

    public void UpdateLoadGrid(float3 centerWorldPos, Frustum frustum)
    {
        float chunkWorldSize = TerrainChunk.ChunkSize * TerrainChunk.CellSize;
        int centerChunkX = (int)MathF.Floor(centerWorldPos.X / chunkWorldSize);
        int centerChunkZ = (int)MathF.Floor(centerWorldPos.Z / chunkWorldSize);
        int2 currentCenterChunk = new int2(centerChunkX, centerChunkZ);

        if (currentCenterChunk == _lastCenterChunkPos && ViewRadius == _lastViewRadius)
        {
            _visibleChunks.Clear();
            foreach (var chunk in _activeChunks.Values)
            {
                if (frustum.IsBoxVisible(chunk.BoundingMin, chunk.BoundingMax))
                {
                    _visibleChunks.Add(chunk);
                }
            }
            return;
        }

        _lastCenterChunkPos = currentCenterChunk;
        _lastViewRadius = ViewRadius;

        _visibleChunks.Clear();
        var currentFrameKeys = new HashSet<int2>();

        for (int z = -ViewRadius; z <= ViewRadius; z++)
        {
            for (int x = -ViewRadius; x <= ViewRadius; x++)
            {
                int2 chunkPos = new int2(centerChunkX + x, centerChunkZ + z);
                currentFrameKeys.Add(chunkPos);

                if (!_activeChunks.TryGetValue(chunkPos, out var chunk))
                {
                    chunk = new TerrainChunk(chunkPos);
                    chunk.GenerateHeightMap();
                    chunk.CreatePhysicsBody();
                    _activeChunks[chunkPos] = chunk;
                }

                int distance = Math.Max(Math.Abs(x), Math.Abs(z));
                int targetLod = 0;
                if (distance > 16) targetLod = 3;     // Дальний план (LOD 3)
                else if (distance > 8) targetLod = 2; // Средний дальний (LOD 2)
                else if (distance > 3) targetLod = 1; // Средний ближний (LOD 1)

                chunk.BuildMesh(targetLod);

                if (frustum.IsBoxVisible(chunk.BoundingMin, chunk.BoundingMax))
                {
                    _visibleChunks.Add(chunk);
                }
            }
        }

        var keysToRemove = new List<int2>();
        foreach (var key in _activeChunks.Keys)
        {
            if (!currentFrameKeys.Contains(key) &&
                (Math.Abs(key.X - centerChunkX) > ViewRadius + 2 || Math.Abs(key.Y - centerChunkZ) > ViewRadius + 2))
            {
                keysToRemove.Add(key);
            }
        }

        foreach (var key in keysToRemove)
        {
            _activeChunks[key].Dispose();
            _activeChunks.Remove(key);
        }
    }

    public float GetTerrainHeightAt(float worldX, float worldZ)
    {
        float chunkWorldSize = TerrainChunk.ChunkSize * TerrainChunk.CellSize;

        // Определяем координаты чанка, в котором находится точка
        int chunkX = (int)MathF.Floor(worldX / chunkWorldSize);
        int chunkZ = (int)MathF.Floor(worldZ / chunkWorldSize);
        int2 chunkPos = new int2(chunkX, chunkZ);

        if (_activeChunks.TryGetValue(chunkPos, out var chunk))
        {
            // Переводим мировые координаты в локальные индексы массива чанка (от 0 до ChunkSize)
            float localWorldX = worldX - (chunkX * chunkWorldSize);
            float localWorldZ = worldZ - (chunkZ * chunkWorldSize);

            int xIdx = Math.Clamp((int)MathF.Floor(localWorldX / TerrainChunk.CellSize), 0, TerrainChunk.ChunkSize);
            int zIdx = Math.Clamp((int)MathF.Floor(localWorldZ / TerrainChunk.CellSize), 0, TerrainChunk.ChunkSize);

            // Билинейную интерполяцию для идеальной точности опустим, берем ближайшую высоту сетки
            return chunk.GetHeightAt(xIdx, zIdx);
        }

        return 0f; // Фолбэк, если чанк еще не сгенерирован
    }

    public IEnumerable<TerrainChunk> GetVisibleChunks() => _visibleChunks;

    public void Dispose()
    {
        foreach (var chunk in _activeChunks.Values) chunk.Dispose();
        _activeChunks.Clear();
        _visibleChunks.Clear();
    }
}
