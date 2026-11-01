using Brutal.Numerics;

namespace BrutalSandbox.Framework.Graphics;

public static class PrimitiveGenerator
{
    /// <summary>
    /// Генерирует горизонтальную текстурированную плоскость (Quad) размером 2x2 на высоте Y = 0.
    /// </summary>
    public static MeshData CreatePlane()
    {
        // 4 вершины с правильными текстурными координатами (UV)
        Vertex[] vertices = [
            new Vertex(new float3(-1f, 0f,  1f), new float3(0f, 1f, 0f), new float2(0f, 0f)), // Лев. ближний
            new Vertex(new float3( 1f, 0f,  1f), new float3(0f, 1f, 0f), new float2(1f, 0f)), // Прав. ближний
            new Vertex(new float3( 1f, 0f, -1f), new float3(0f, 1f, 0f), new float2(1f, 1f)), // Прав. дальний
            new Vertex(new float3(-1f, 0f, -1f), new float3(0f, 1f, 0f), new float2(0f, 1f))  // Лев. дальний
        ];

        // 2 треугольника (6 индексов)
        uint[] indices = [
            0, 1, 2,
            0, 2, 3
        ];

        return new MeshData
        {
            Name = "Procedural_Plane",
            Vertices = vertices,
            Indices = indices
        };
    }

    /// <summary>
    /// Генерирует полноценный текстурированный 3D куб размером 1x1x1.
    /// Каждая грань имеет свои честные нормали и UV-развёртку от 0 до 1.
    /// </summary>
    public static MeshData CreateCube()
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        uint vIndex = 0;

        void AddFace(float3 p0, float3 p1, float3 p2, float3 p3, float3 normal)
        {
            vertices.Add(new Vertex(p0, normal, new float2(0f, 0f)));
            vertices.Add(new Vertex(p1, normal, new float2(1f, 0f)));
            vertices.Add(new Vertex(p2, normal, new float2(1f, 1f)));
            vertices.Add(new Vertex(p3, normal, new float2(0f, 1f)));

            indices.Add(vIndex + 0); indices.Add(vIndex + 1); indices.Add(vIndex + 2);
            indices.Add(vIndex + 0); indices.Add(vIndex + 2); indices.Add(vIndex + 3);
            vIndex += 4;
        }

        AddFace(new float3(-0.5f, -0.5f, 0.5f), new float3(0.5f, -0.5f, 0.5f), new float3(0.5f, 0.5f, 0.5f), new float3(-0.5f, 0.5f, 0.5f), new float3(0, 0, 1));
        AddFace(new float3(0.5f, -0.5f, -0.5f), new float3(-0.5f, -0.5f, -0.5f), new float3(-0.5f, 0.5f, -0.5f), new float3(0.5f, 0.5f, -0.5f), new float3(0, 0, -1));
        AddFace(new float3(-0.5f, -0.5f, -0.5f), new float3(-0.5f, -0.5f, 0.5f), new float3(-0.5f, 0.5f, 0.5f), new float3(-0.5f, 0.5f, -0.5f), new float3(-1, 0, 0));
        AddFace(new float3(0.5f, -0.5f, 0.5f), new float3(0.5f, -0.5f, -0.5f), new float3(0.5f, 0.5f, -0.5f), new float3(0.5f, 0.5f, 0.5f), new float3(1, 0, 0));
        AddFace(new float3(-0.5f, 0.5f, 0.5f), new float3(0.5f, 0.5f, 0.5f), new float3(0.5f, 0.5f, -0.5f), new float3(-0.5f, 0.5f, -0.5f), new float3(0, 1, 0));
        AddFace(new float3(-0.5f, -0.5f, -0.5f), new float3(0.5f, -0.5f, -0.5f), new float3(0.5f, -0.5f, 0.5f), new float3(-0.5f, -0.5f, 0.5f), new float3(0, -1, 0));

        return new MeshData
        {
            Name = "Procedural_Cube",
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray()
        };
    }

    /// <summary>
    /// Генерирует процедурную капсулу с заданной высотой цилиндра и радиусом сфер на концах.
    /// </summary>
    public static MeshData CreateCapsule(float cylinderHeight, float radius, int segments = 16, int rings = 8)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        float halfExtents = cylinderHeight * 0.5f;

        // 1. Генерируем вершины сверху вниз
        // Верхнее полушарие (от полюса до экватора)
        for (int ring = 0; ring <= rings; ring++)
        {
            float phi = (MathF.PI * 0.5f) * (float)ring / rings;
            float sinPhi = MathF.Sin(phi);
            float cosPhi = MathF.Cos(phi);

            for (int seg = 0; seg <= segments; seg++)
            {
                float theta = (MathF.PI * 2f) * (float)seg / segments;
                float sinTheta = MathF.Sin(theta);
                float cosTheta = MathF.Cos(theta);

                float3 normal = new float3(sinPhi * cosTheta, cosPhi, sinPhi * sinTheta);
                float3 pos = normal * radius + new float3(0f, halfExtents, 0f);
                float2 uv = new float2((float)seg / segments, 0.5f * (float)ring / rings);

                vertices.Add(new Vertex(pos, normal, uv));
            }
        }

        // Нижнее полушарие (от экватора до нижнего полюса)
        for (int ring = 0; ring <= rings; ring++)
        {
            float phi = (MathF.PI * 0.5f) + (MathF.PI * 0.5f) * (float)ring / rings;
            float sinPhi = MathF.Sin(phi);
            float cosPhi = MathF.Cos(phi);

            for (int seg = 0; seg <= segments; seg++)
            {
                float theta = (MathF.PI * 2f) * (float)seg / segments;
                float sinTheta = MathF.Sin(theta);
                float cosTheta = MathF.Cos(theta);

                float3 normal = new float3(sinPhi * cosTheta, cosPhi, sinPhi * sinTheta);
                float3 pos = normal * radius - new float3(0f, halfExtents, 0f);
                float2 uv = new float2((float)seg / segments, 0.5f + 0.5f * (float)ring / rings);

                vertices.Add(new Vertex(pos, normal, uv));
            }
        }

        // 2. Сшиваем треугольники колец (включая цилиндрический стык между полушариями)
        int vStride = segments + 1;
        int totalRings = (rings + 1) * 2;

        // Находим этот блок в самом низу метода CreateCapsule и меняем r1 и r2 местами:
        for (int ring = 0; ring < totalRings - 1; ring++)
        {
            for (int seg = 0; seg < segments; seg++)
            {
                uint r0 = (uint)(ring * vStride + seg);
                uint r1 = (uint)(ring * vStride + (seg + 1));
                uint r2 = (uint)((ring + 1) * vStride + seg);
                uint r3 = (uint)((ring + 1) * vStride + (seg + 1));

                // ИСПРАВЛЕНО: Изменен порядок вершин, нормали теперь смотрят НАРУЖУ
                indices.Add(r0); indices.Add(r1); indices.Add(r2);
                indices.Add(r1); indices.Add(r3); indices.Add(r2);
            }
        }


        return new MeshData
        {
            Name = "Procedural_Capsule",
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray()
        };
    }

}
