using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.OpenGlApi;
using Brutal.TextureApi;

namespace TestSandbox;

public sealed unsafe class OpenGlRenderer : IRenderer
{
    private const int VertexStride = 32;

    private readonly GlfwWindow _window;
    private GlVertexArray _vertexArray;
    private GlBuffer _vertexBuffer;
    private GlBuffer _indexBuffer;
    private GlProgram _program;
    private int _mvpLocation = -1;
    private int _textureLocation = -1;
    private int _indexCount;
    private int _width;
    private int _height;

    private uint _activeTextureId = 0; // 0 — дефолтная пустая текстура в OpenGL
    private int _modelLocation = -1;
    private int _normalMatrixLocation = -1;
    private int _viewPosLocation = -1;
    private readonly Framework.Audio.AssetManager _assetManager;

    private uint _shadowFbo;
    private uint _shadowMapTexture;
    private const int ShadowWidth = 2048, ShadowHeight = 2048;

    private GlProgram _shadowProgram;
    private int _shadowLightSpaceLocation = -1;
    private int _shadowModelLocation = -1;
    private int _mainLightSpaceLocation = -1;
    private int _shadowMapUniformLocation = -1;

    private float4x4 _lightSpaceMatrix;
    private bool _isShadowPass = false;

    public OpenGlRenderer(GlfwWindow window, Framework.Audio.AssetManager assetManager)
    {
        _window = window;
        _assetManager = assetManager; 

        window.MakeContextCurrent();
        Glfw.SwapInterval(1);

        if (!Glad.Initialize(name => Glfw.PInvoke.GetProcAddress(name)))
            throw new Exception("Failed to initialize OpenGL (GLAD).");


        _program = CreateProgram();
        _mvpLocation = _program.GetUniformLocation("mvp"u8);
        _textureLocation = _program.GetUniformLocation("uGameTexture"u8);
        _modelLocation = _program.GetUniformLocation("model"u8);
        _normalMatrixLocation = _program.GetUniformLocation("normalMatrix"u8);
        _viewPosLocation = _program.GetUniformLocation("uViewPos"u8);

        _vertexArray = OpenGl.GenVertexArray();
        _vertexArray.Bind();
        _vertexBuffer = OpenGl.GenBuffer();
        _indexBuffer = OpenGl.GenBuffer();

        OpenGl.Enable(GlCapability.DepthTest);
        OpenGl.DepthFunc(GlFunc.LessEqual);

        var framebufferSize = window.FramebufferSize;
        Resize(framebufferSize.X, framebufferSize.Y);
    }

    public int Width => _width;
    public int Height => _height;

    private float4x4 _currentViewProj;

    private uint _lastBoundVaoId = 0;
    private uint _lastBoundTextureId = 0;
    public void BeginFrame(float4x4 viewProjection)
    {
        var framebufferSize = _window.FramebufferSize;
        if (framebufferSize.X <= 0 || framebufferSize.Y <= 0) return;

        Resize(framebufferSize.X, framebufferSize.Y);
        OpenGl.ClearColor(new float4(0.05f, 0.06f, 0.08f, 1f));
        OpenGl.Clear(GlClearBit.Color | GlClearBit.Depth);

        _currentViewProj = viewProjection;
        _program.Use();

        _lastBoundVaoId = 0;
        _lastBoundTextureId = 0;
    }

    public void RenderMesh(MeshData data, float4x4 model, string assetName)
    {
        if (data.Vertices.Length == 0 || data.Indices.Length == 0) return;

        if (data.GlBuffers is null)
        {
            GlVertexArray localVao = OpenGl.GenVertexArray();
            localVao.Bind();

            GlBuffer localVbo = OpenGl.GenBuffer();
            localVbo.Bind(GlBindTarget.ArrayBuffer);
            OpenGl.BufferData(GlBindTarget.ArrayBuffer, data.Vertices, GlBufferUsage.StaticDraw);

            GlBuffer localIbo = OpenGl.GenBuffer();
            localIbo.Bind(GlBindTarget.ElementArrayBuffer);
            OpenGl.BufferData(GlBindTarget.ElementArrayBuffer, data.Indices.AsSpan(), GlBufferUsage.StaticDraw);

            OpenGl.VertexAttributes([
                new GlAttribute(0, 3, OpenGl.GL_FLOAT, GlScaling.Float, VertexStride, 0),
            new GlAttribute(1, 3, OpenGl.GL_FLOAT, GlScaling.Float, VertexStride, 12),
            new GlAttribute(2, 2, OpenGl.GL_FLOAT, GlScaling.Float, VertexStride, 24),
        ]);
            OpenGl.EnableVertexAttribArray(0);
            OpenGl.EnableVertexAttribArray(1);
            OpenGl.EnableVertexAttribArray(2);

            data.GlBuffers = (localVao, localVbo, localIbo);
        }

        var buffers = ((GlVertexArray Vao, GlBuffer Vbo, GlBuffer Ibo))data.GlBuffers;

        // ОПТИМИЗАЦИЯ: Биндим VAO только если изменился меш (например, перешли от земли к кубу)
        if (_lastBoundVaoId != buffers.Vao.Id)
        {
            buffers.Vao.Bind();
            _lastBoundVaoId = buffers.Vao.Id;
        }

        // ОПТИМИЗАЦИЯ: Переключаем текстуру только при необходимости
        GlTexture glTex = _assetManager.GetGlTexture(assetName);
        if (_lastBoundTextureId != glTex.Id)
        {
            if (_textureLocation != -1) OpenGl.Uniform(_textureLocation, 0);
            OpenGl.ActiveTexture(OpenGl.GL_TEXTURE0);
            OpenGl.BindTexture(OpenGl.GL_TEXTURE_2D, glTex.Id);
            _lastBoundTextureId = glTex.Id;
        }

        float4x4.Invert(model, out float4x4 invertedModel);
        float4x4 normalMatrix = float4x4.Transpose(invertedModel);

        float4x4 mvp = model * _currentViewProj;

        // Передаем матрицы в OpenGL шейдер
        OpenGl.Uniform(_mvpLocation, mvp, false);
        if (_modelLocation != -1) OpenGl.Uniform(_modelLocation, model, false);
        if (_normalMatrixLocation != -1) OpenGl.Uniform(_normalMatrixLocation, normalMatrix, false);

        // Передаем позицию глаза наблюдателя (временно зашьем константу орбитальной камеры, 
        // либо передавайте сюда реальный _camera.Position из MeshViewer)
        if (_viewPosLocation != -1) OpenGl.Uniform(_viewPosLocation, new float3(8f, 7f, 12f));

        OpenGl.DrawIndexed(GlTopology.Triangles, GlIndexType.UInt, data.Indices.Length, 1, 0, 0, 0);
    }


    public void EndFrame(Action? postDraw = null)
    {
        OpenGl.BindTexture(OpenGl.GL_TEXTURE_2D, 0);
        postDraw?.Invoke();
        _window.SwapBuffers();
    }

    public void BindMaterialTexture(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return;

        // Теперь мы гарантированно найдем текстуру "stone_wall", 
        // так как этот же ключ был записан при сканировании директории!
        GlTexture glTex = _assetManager.GetGlTexture(assetName);
        _activeTextureId = glTex.Id;
    }

    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        OpenGl.Viewport(0, 0, width, height);
    }

    public void Draw(float4x4 mvp, Action? postDraw = null)
    {
        var framebufferSize = _window.FramebufferSize;
        if (framebufferSize.X <= 0 || framebufferSize.Y <= 0) return;

        Resize(framebufferSize.X, framebufferSize.Y);
        OpenGl.ClearColor(new float4(0.05f, 0.06f, 0.08f, 1f));
        OpenGl.Clear(GlClearBit.Color | GlClearBit.Depth);

        if (_indexCount > 0)
        {
            _program.Use();
            OpenGl.Uniform(_mvpLocation, mvp, false);

            // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ: Явно говорим шейдеру читать текстуру из Текстурного Юнита 0.
            // Без этой строки видеокарта читала случайный кусок памяти (отсюда и полосы).
            if (_textureLocation != -1)
            {
                OpenGl.Uniform(_textureLocation, 0);
            }

            // Активируем Текстурный Юнит 0 и привязываем текстуру меша
            OpenGl.ActiveTexture(OpenGl.GL_TEXTURE0);
            OpenGl.BindTexture(OpenGl.GL_TEXTURE_2D, _activeTextureId);

            _vertexArray.Bind();
            OpenGl.DrawIndexed(GlTopology.Triangles, GlIndexType.UInt, _indexCount, 1, 0, 0, 0);

            // Сбрасываем стейт текстуры, чтобы ImGui/FreeType не затирали наш Текстурный Юнит 0
            OpenGl.BindTexture(OpenGl.GL_TEXTURE_2D, 0);
        }

        // Рендерим ImGui поверх 3D сцены (теперь он безопасно использует свои текстурные юниты)
        postDraw?.Invoke();
        _window.SwapBuffers();
    }
    public void InitializeTextureOnDevice(string assetName, ITexture texture) { }
    private static GlProgram CreateProgram()
    {
        var vertex = OpenGl.CreateShader(GlShaderType.Vertex).SetSource(Shaders.OpenGlVertex).Compile();
        var fragment = OpenGl.CreateShader(GlShaderType.Fragment).SetSource(Shaders.OpenGlFragment).Compile();
        var program = OpenGl.CreateProgram().Attach(vertex).Attach(fragment).Link();
        vertex.Dispose();
        fragment.Dispose();
        return program;
    }

    public void Dispose()
    {
        _program.Dispose();
        OpenGl.DeleteBuffers([_vertexBuffer, _indexBuffer]);
        OpenGl.DeleteVertexArrays([_vertexArray]);
    }
}
