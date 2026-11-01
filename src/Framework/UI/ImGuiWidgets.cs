using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace BrutalSandbox.Framework.UI;

public static class ImGuiWidgets
{
    private static readonly uint[] PieColors = [
        0xFF3B52F6, // Ярко-красный (Input/Logic)
        0xFF10B981, // Зеленый (Render 3D)
        0xFFF59E0B, // Оранжевый (ImGui/UI)
        0xFF6366F1, // Фиолетовый (Swap Buffers)
        0xFF8B5CF6, // Пурпурный (Остальное)
    ];

    /// <summary>
    /// Отрисовывает универсальную круговую диаграмму производительности с цветной легендой справа.
    /// </summary>
    public static void PerformancePieChart(string label, Dictionary<string, double> rawSamples, string excludeKey = "Total Frame")
    {
        ImGui.Text(label);

        var drawList = ImGui.GetWindowDrawList();
        var cursorPos = ImGui.GetCursorScreenPos();

        float radius = 70f;
        float2 center = new float2(cursorPos.X + radius + 10, cursorPos.Y + radius + 10);

        double totalRecordedTime = 0;
        var targetSamples = rawSamples.Where(r => r.Key != excludeKey).ToList();

        foreach (var sample in targetSamples) totalRecordedTime += sample.Value;
        if (totalRecordedTime <= 0) totalRecordedTime = 1.0;

        float currentAngle = -MathF.PI / 2f; // Старт с 12 часов
        int colorIndex = 0;

        // Рисуем секторы круга
        for (int i = 0; i < targetSamples.Count; i++)
        {
            var sample = targetSamples[i];
            float percentage = (float)(sample.Value / totalRecordedTime);
            float segmentAngle = percentage * (MathF.PI * 2f);

            if (segmentAngle > 0.01f)
            {
                uint color = PieColors[colorIndex % PieColors.Length];
                drawList.PathLineTo(center);
                drawList.PathArcTo(center, radius, currentAngle, currentAngle + segmentAngle, 32);
                drawList.PathFillConvex(color);
                currentAngle += segmentAngle;
            }
            colorIndex++;
        }

        // Рисуем столбец легенды справа
        float legendStartX = center.X + radius + 20;
        ImGui.SetCursorScreenPos(new float2(legendStartX, cursorPos.Y + 10));

        ImGui.BeginGroup();
        colorIndex = 0;
        foreach (var sample in targetSamples)
        {
            uint color = PieColors[colorIndex % PieColors.Length];

            var pMin = ImGui.GetCursorScreenPos();
            var pMax = new float2(pMin.X + 12, pMin.Y + 12);
            drawList.AddRectFilled(pMin, pMax, color);

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 15);

            // Окрашиваем текст в цвет куска диаграммы
            float4 textColor = new float4((color & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, ((color >> 16) & 0xFF) / 255f, ((color >> 24) & 0xFF) / 255f);
            ImGui.TextColored(textColor, $"{sample.Key}: {sample.Value:F2} ms");

            colorIndex++;
        }
        ImGui.EndGroup();

        // Резервируем место под окном
        ImGui.SetCursorScreenPos(new float2(cursorPos.X, cursorPos.Y));
        ImGui.Dummy(new float2(400f, (radius * 2) + 30f));
    }
}
