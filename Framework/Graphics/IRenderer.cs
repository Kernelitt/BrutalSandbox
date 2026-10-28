using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace BrutalSandbox;

public interface IRenderer : IDisposable
{
    int Width { get; }
    int Height { get; }

    void Resize(int width, int height);
    void InitializeTextureOnDevice(string assetName, Brutal.TextureApi.ITexture texture);

    void BeginFrame(float4x4 viewProjection);
    void RenderMesh(MeshData data, float4x4 model, string assetName);
    void EndFrame(Action? postDraw = null);
    void UpdateViewProjection(float4x4 viewProjection);
    void BindMaterialTexture(string assetName) { }
    void Draw(float4x4 mvp, Action? postDraw = null) { }

    ImTextureRef GetImGuiTexture(string assetName);
}
