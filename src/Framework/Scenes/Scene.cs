using Brutal.GlfwApi;
using Brutal.Numerics;

namespace BrutalSandbox.Framework.Scenes;

public interface IScene : IDisposable
{
    void Initialize();
    void Update(float deltaTime);
    void Render(IRenderer renderer, float4x4 viewProj);
    void RenderUi();
    void HandleInput(GlfwWindow window, double2 cursorDelta, bool shiftHeld, bool dragging);

    // НОВЫЙ МЕТОД: запрашивает матрицу камеры у сцены для BeginFrame
    float4x4 GetViewProjection(float aspectRatio) => float4x4.Identity;
}
