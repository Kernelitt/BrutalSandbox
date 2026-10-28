using Brutal.Numerics;
using System;
using System.Collections.Generic;
using System.Text;

namespace BrutalSandbox.GameRelated
{
    internal static class RayCasting
    {
        public static bool RayIntersectsScaledBox(float3 rayOrigin, float3 rayDir, float3 boxMin, float3 boxMax, out float tHit, out float3 normal)
        {
            tHit = 0f;
            normal = new float3(0f, 1f, 0f);

            float maxDistance = 15f; // Длина луча
            float3 invDir = new float3(1f / rayDir.X, 1f / rayDir.Y, 1f / rayDir.Z);

            float t1 = (boxMin.X - rayOrigin.X) * invDir.X;
            float t2 = (boxMax.X - rayOrigin.X) * invDir.X;
            float tmin = MathF.Min(t1, t2);
            float tmax = MathF.Max(t1, t2);

            float t3 = (boxMin.Y - rayOrigin.Y) * invDir.Y;
            float t4 = (boxMax.Y - rayOrigin.Y) * invDir.Y;
            tmin = MathF.Max(tmin, MathF.Min(t3, t4));
            tmax = MathF.Min(tmax, MathF.Max(t3, t4));

            float t5 = (boxMin.Z - rayOrigin.Z) * invDir.Z;
            float t6 = (boxMax.Z - rayOrigin.Z) * invDir.Z;
            tmin = MathF.Max(tmin, MathF.Min(t5, t6));
            tmax = MathF.Min(tmax, MathF.Max(t5, t6));

            if (tmax < 0 || tmin > tmax) return false;

            // Переводим абсолютное расстояние в нормализованную долю луча (0..1)
            tHit = tmin / maxDistance;
            if (tHit > 1.0f) return false;

            // Рассчитываем точную нормаль поверхности столкновения на основе попавшей точки
            float3 hitPoint = rayOrigin + rayDir * tmin;
            float3 center = (boxMin + boxMax) * 0.5f;
            float3 p = hitPoint - center;

            // Вычисляем, к какой из граней точка находится ближе всего, учитывая пропорции
            float3 halfSize = (boxMax - boxMin) * 0.5f;
            float minDiff = float.MaxValue;

            float diffX = MathF.Abs(halfSize.X - MathF.Abs(p.X));
            if (diffX < minDiff) { minDiff = diffX; normal = new float3(MathF.Sign(p.X), 0f, 0f); }

            float diffY = MathF.Abs(halfSize.Y - MathF.Abs(p.Y));
            if (diffY < minDiff) { minDiff = diffY; normal = new float3(0f, MathF.Sign(p.Y), 0f); }

            float diffZ = MathF.Abs(halfSize.Z - MathF.Abs(p.Z));
            if (diffZ < minDiff) { normal = new float3(0f, 0f, MathF.Sign(p.Z)); }

            return true;
        }

    }
}
