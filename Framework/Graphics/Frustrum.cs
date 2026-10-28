using Brutal.Numerics;

namespace BrutalSandbox.Framework.Graphics;

public sealed class Frustum
{
    private readonly float4[] _planes = new float4[6];

    public void Update(float4x4 viewProj)
    {
        // Левая плоскость
        _planes[0] = new float4(viewProj.X.W + viewProj.X.X, viewProj.Y.W + viewProj.Y.X, viewProj.Z.W + viewProj.Z.X, viewProj.W.W + viewProj.W.X);
        // Правая плоскость
        _planes[1] = new float4(viewProj.X.W - viewProj.X.X, viewProj.Y.W - viewProj.Y.X, viewProj.Z.W - viewProj.Z.X, viewProj.W.W - viewProj.W.X);
        // Нижняя плоскость
        _planes[2] = new float4(viewProj.X.W + viewProj.X.Y, viewProj.Y.W + viewProj.Y.Y, viewProj.Z.W + viewProj.Z.Y, viewProj.W.W + viewProj.W.Y);
        // Верхняя плоскость
        _planes[3] = new float4(viewProj.X.W - viewProj.X.Y, viewProj.Y.W - viewProj.Y.Y, viewProj.Z.W - viewProj.Z.Y, viewProj.W.W - viewProj.W.Y);
        // Ближняя плоскость
        _planes[4] = new float4(viewProj.X.W + viewProj.X.Z, viewProj.Y.W + viewProj.Y.Z, viewProj.Z.W + viewProj.Z.Z, viewProj.W.W + viewProj.W.Z);
        // Дальняя плоскость
        _planes[5] = new float4(viewProj.X.W - viewProj.X.Z, viewProj.Y.W - viewProj.Y.Z, viewProj.Z.W - viewProj.Z.Z, viewProj.W.W - viewProj.W.Z);

        // Нормализуем плоскости
        for (int i = 0; i < 6; i++)
        {
            float length = MathF.Sqrt(_planes[i].X * _planes[i].X + _planes[i].Y * _planes[i].Y + _planes[i].Z * _planes[i].Z);
            if (length > 0f) _planes[i] /= length;
        }
    }

    public bool IsBoxVisible(float3 min, float3 max)
    {
        for (int i = 0; i < 6; i++)
        {
            float3 positiveVertex = new float3(
                _planes[i].X >= 0 ? max.X : min.X,
                _planes[i].Y >= 0 ? max.Y : min.Y,
                _planes[i].Z >= 0 ? max.Z : min.Z
            );

            if (_planes[i].X * positiveVertex.X + _planes[i].Y * positiveVertex.Y + _planes[i].Z * positiveVertex.Z + _planes[i].W < 0)
            {
                return false; // Бокс полностью за плоскостью
            }
        }
        return true;
    }
}
