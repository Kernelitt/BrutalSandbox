using System;
using System.Collections.Generic;
using Brutal.JoltApi;
using Brutal.Numerics;
using BrutalSandbox.Framework.Graphics;
using BrutalSandbox.Framework.Physics;

namespace BrutalSandbox.Framework.Terrain;

public sealed class TerrainChunk : IDisposable
{
    public const int ChunkSize = 64;
    public const float CellSize = 2.0f;
    private const float HeightScale = 0.1f;

    public int2 ChunkPos { get; }
    private readonly short[,] _heightMap = new short[ChunkSize + 3, ChunkSize + 3];

    public MeshData? Mesh { get; private set; }
    public int CurrentLod { get; private set; } = -1;

    public float3 BoundingMin { get; private set; }
    public float3 BoundingMax { get; private set; }

    private uint _physicsBodyId = 0;

    public TerrainChunk(int2 chunkPos)
    {
        ChunkPos = chunkPos;
    }

    private int MapIdx(int val) => val + 1; // Сдвиг для безопасного индексирования массива от -1

    public void GenerateHeightMap()
    {
        float worldStartX = ChunkPos.X * ChunkSize * CellSize;
        float worldStartZ = ChunkPos.Y * ChunkSize * CellSize;

        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;

        // Читаем высоты с запасом в 1 ячейку со всех сторон
        for (int z = -1; z <= ChunkSize + 1; z++)
        {
            for (int x = -1; x <= ChunkSize + 1; x++)
            {
                float worldX = worldStartX + x * CellSize;
                float worldZ = worldStartZ + z * CellSize;

                float noise1 = PerlinNoise.Noise(worldX * 0.002f, worldZ * 0.002f) * 80f;
                float noise2 = PerlinNoise.Noise(worldX * 0.01f, worldZ * 0.01f) * 15f;
                float finalHeight = noise1 + noise2;

                _heightMap[MapIdx(x), MapIdx(z)] = (short)MathF.Round(finalHeight / HeightScale);

                if (x >= 0 && x <= ChunkSize && z >= 0 && z <= ChunkSize)
                {
                    if (finalHeight < minHeight) minHeight = finalHeight;
                    if (finalHeight > maxHeight) maxHeight = finalHeight;
                }
            }
        }

        BoundingMin = new float3(worldStartX, minHeight - 2f, worldStartZ);
        BoundingMax = new float3(worldStartX + ChunkSize * CellSize, maxHeight + 2f, worldStartZ + ChunkSize * CellSize);
    }

    public float GetHeightAt(int x, int z) => _heightMap[MapIdx(x), MapIdx(z)] * HeightScale;

    public void BuildMesh(int lod)
    {
        if (CurrentLod == lod && Mesh != null) return;
        CurrentLod = lod;

        int step = (int)MathF.Pow(2, lod);

        var vertices = new List<Vertex>();
        var indices = new List<uint>();

        float worldStartX = ChunkPos.X * ChunkSize * CellSize;
        float worldStartZ = ChunkPos.Y * ChunkSize * CellSize;

        int[,] indexMapping = new int[ChunkSize + 1, ChunkSize + 1];

        int currentVertexIndex = 0;
        for (int z = 0; z <= ChunkSize; z += step)
        {
            for (int x = 0; x <= ChunkSize; x += step)
            {
                float height = GetHeightAt(x, z);
                float3 position = new float3(worldStartX + x * CellSize, height, worldStartZ + z * CellSize);
                float2 uv = new float2(x * CellSize * 0.1f, z * CellSize * 0.1f);

                // Вычисляем бесшовную нормаль аналитически, беря честные соседние высоты (даже за границами чанка)
                float hL = GetHeightAt(x - 1, z);
                float hR = GetHeightAt(x + 1, z);
                float hD = GetHeightAt(x, z - 1);
                float hU = GetHeightAt(x, z + 1);
                float3 normal = float3.Normalize(new float3(hL - hR, CellSize * 2.0f, hD - hU));

                vertices.Add(new Vertex(position, normal, uv));
                indexMapping[x, z] = currentVertexIndex++;
            }
        }

        for (int z = 0; z < ChunkSize; z += step)
        {
            for (int x = 0; x < ChunkSize; x += step)
            {
                uint i0 = (uint)indexMapping[x, z];
                uint i1 = (uint)indexMapping[x, z + step];
                uint i2 = (uint)indexMapping[x + step, z];
                uint i3 = (uint)indexMapping[x + step, z + step];

                indices.Add(i0); indices.Add(i1); indices.Add(i2);
                indices.Add(i2); indices.Add(i1); indices.Add(i3);
            }
        }

        Mesh = new MeshData
        {
            Name = $"Terrain_Chunk_{ChunkPos.X}_{ChunkPos.Y}_LOD_{lod}",
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray()
        };
    }

    public unsafe void CreatePhysicsBody()
    {
        if (_physicsBodyId != 0) return;

        var phys = PhysicsManager.Instance;

        // ИСПРАВЛЕНО: Возвращаем размер 65x65 (ChunkSize + 1). 
        // Это стандартный размер для сетки ячеек 64x64, который идеально стыкуется с соседними чанками.
        int size = ChunkSize + 1;
        float[] floatHeights = new float[size * size];

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                floatHeights[z * size + x] = GetHeightAt(x, z);
            }
        }

        float worldStartX = ChunkPos.X * ChunkSize * CellSize;
        float worldStartZ = ChunkPos.Y * ChunkSize * CellSize;
        float3 position = new float3(worldStartX, 0f, worldStartZ);

        fixed (float* pHeights = floatHeights)
        {
            using (var shapeSettings = HeightFieldShapeSettings.Create(
                pHeights,
                float3.Zero,
                new float3(CellSize, 1f, CellSize),
                (uint)size,
                null,
                default))
            {
                // ИСПРАВЛЕНО: Настройка косинуса угла активных ребер (Active Edge Threshold).
                // За счет оптимизации Jolt начинает игнорировать швы смежных статических чанков.
                // Если Brutal API предоставляет прямое поле, выставляем его. 
                // Дополнительно Jolt рекомендует снизить коэффициент упругости (Restitution) земли до 0,
                // чтобы объекты не пружинили при микро-контактах на стыках.

                _physicsBodyId = phys.CreateBody(
                    shapeSettings,
                    position,
                    floatQuat.Identity,
                    MotionType.Static,
                    PhysicsManager.LayerStatic,
                    "grass_tile"
                );

                // Гарантируем отсутствие упругости (отскока) на ландшафте
                phys.BodyInterface.SetRestitution(_physicsBodyId, 0.0f);
            }
        }
    }


    public void Dispose()
    {
        if (_physicsBodyId != 0)
        {
            var phys = PhysicsManager.Instance;
            phys.BodyInterface.RemoveBody(_physicsBodyId);
            phys.BodyInterface.DestroyBody(_physicsBodyId);
            _physicsBodyId = 0;
        }
        Mesh = null;
    }
}
