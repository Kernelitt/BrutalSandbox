using Brutal;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.ImGuiApi.FreeType;
using Brutal.Numerics;
using System.Numerics;
using BrutalSandbox.Framework;
using BrutalSandbox.Framework.Audio;
using BrutalSandbox.Framework.Files;
using BrutalSandbox.Framework.Localization;
using BrutalSandbox.Framework.Network;
using BrutalSandbox.Framework.Physics;
using BrutalSandbox.Framework.Scenes;
using BrutalSandbox.Framework.UI;

namespace BrutalSandbox;

public sealed class MainApp : IDisposable
{
    private bool _showDebugMenu = false;
    private bool _f3WasPressed = false;
    private float _fpsTimer = 0f;
    private int _fpsCounter = 0;
    private int _currentFps = 0;
    private float _currentFrameTime = 0f;
    private readonly GlfwWindow _window;
    private readonly IRenderer _renderer;
    public IRenderer Renderer => _renderer;
    private readonly bool _useOpenGl;

    private readonly ImGuiContextPtr _imGuiContext;
    private readonly GlfwBackend _glfwBackend;
    private readonly OpenGlBackend? _openGlBackend;

    private readonly AssetManager _assetManager;
    private readonly LocalizationManager _locManager;

    private IScene? _currentScene;
    private GameState _nextState = GameState.Loading;

    private double2 _lastCursor;
    private ImFontPtr _imGuiFont;

    public AssetManager AssetManager => _assetManager;
    public LocalizationManager LocManager => _locManager;
    public GlfwWindow Window => _window;

    private NetworkManager _networkManager = new();
    public NetworkManager Network => _networkManager;

    public MainApp(GlfwWindow window, bool useOpenGl)
    {
        _window = window;
        _useOpenGl = useOpenGl;

        AudioManager.Initialize();
        _assetManager = new AssetManager();
        _assetManager.LoadAllAssets();

        PhysicsManager.Initialize();

        _locManager = new LocalizationManager();
        _locManager.SetLanguage("ru_RU");

        ConfigManager.Load(_locManager, AudioManager.Instance);

        _imGuiContext = ImGui.CreateContext();
        ImGui.GetIO().IniFilename = null;

        _renderer = useOpenGl ? new OpenGlRenderer(window, _assetManager) : new VulkanRenderer(window);
        _assetManager.InitializeTextures(_renderer);

        var clientApi = useOpenGl ? GlfwBackend.ClientApi.OpenGL : GlfwBackend.ClientApi.Vulkan;
        _glfwBackend = new GlfwBackend(window, clientApi);
        if (_useOpenGl) _openGlBackend = new OpenGlBackend();

        var io = ImGui.GetIO();
        io.DisplaySize = new float2(1280, 720);
        io.DeltaTime = 1f / 60f;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;

        var fonts = io.Fonts;
        fonts.SetFontLoader(ImGuiFreeType.GetFontLoader());
        _imGuiFont = fonts.AddFontFromFileTTF("fonts\\BrianneTod.ttf"u8, 36f);

        _lastCursor = window.CursorPos;

        // Начинаем с главного меню
        ChangeScene(GameState.MainMenu);
    }

    public void ChangeScene(GameState state)
    {
        _nextState = state;
    }

    private void ApplySceneChange()
    {
        if (_nextState == GameState.Loading) return;

        _currentScene?.Dispose();
        _currentScene = null;

        switch (_nextState)
        {
            case GameState.MainMenu:
                AudioManager.Instance.PlayMusic(_assetManager.GetMusic("Investigations"));
                _currentScene = new MainMenuScene(this);
                break;
            case GameState.Gameplay:
                AudioManager.Instance.PlayMusic(_assetManager.GetMusic("Chipper-Doodle"));
                _currentScene = new GameplayScene(this);
                break;
        }

        _currentScene?.Initialize();
        _nextState = GameState.Loading; // Сбрасываем триггер перехода
    }

    // Добавьте эти поля в класс MainApp


    public void Frame()
    {
        Profiler.BeginMemorySample();
        Profiler.BeginSample("Total Frame");

        ApplySceneChange();

        var framebufferSize = _window.FramebufferSize;
        if (framebufferSize.X == 0 || framebufferSize.Y == 0)
        {
            Profiler.EndSample("Total Frame");
            Profiler.EndMemorySample();
            return;
        }

        float dt = ImGui.GetIO().DeltaTime;

        // Обновление подсистем
        Profiler.BeginSample("Audio Update");
        AudioManager.Instance.Update();
        Profiler.EndSample("Audio Update");

        Profiler.BeginSample("Physics Update");
        _currentScene?.Update(dt);
        Profiler.EndSample("Physics Update");

        _renderer.Resize(framebufferSize.X, framebufferSize.Y);

        // Ввод
        Profiler.BeginSample("Input & Logic");
        ProcessInput();
        Profiler.EndSample("Input & Logic");

        var aspect = _renderer.Height > 0 ? (float)_renderer.Width / _renderer.Height : 1f;

        // Рендер кадров UI
        Profiler.BeginSample("Render");
        _glfwBackend.NewFrame();
        if (_useOpenGl) _openGlBackend?.NewFrame();
        ImGui.NewFrame();
        ImString.SharedStorage.Reset();

        ImGui.PushFont(_imGuiFont, 14f);
        _currentScene?.RenderUi();
        if (_showDebugMenu) BuildDebugMenu();
        ImGui.PopFont();

        ImGui.Render();

        // 3D Рендеринг сцены
        float4x4 viewProj = _currentScene?.GetViewProjection(aspect) ?? float4x4.Identity;
        _renderer.BeginFrame(viewProj);

        _currentScene?.Render(_renderer, viewProj);

        _renderer.EndFrame(() =>
        {
            if (_useOpenGl) _openGlBackend?.RenderDrawData(ImGui.GetDrawData());
        });
        Profiler.EndSample("Render");

        Profiler.EndSample("Total Frame");
        Profiler.EndMemorySample();

        UpdateFpsMetrics();
    }

    private void ProcessInput()
    {

        bool f3Pressed = _window.GetKey(GlfwKey.F3) == GlfwKeyAction.Press;
        if (f3Pressed && !_f3WasPressed)
        {
            _showDebugMenu = !_showDebugMenu;
        }
        _f3WasPressed = f3Pressed;

        var cursor = _window.CursorPos;
        var delta = new double2(cursor.X - _lastCursor.X, cursor.Y - _lastCursor.Y);

        if (!ImGui.GetIO().WantCaptureMouse)
        {
            var shiftHeld = _window.GetKey(GlfwKey.LeftShift) != GlfwKeyAction.Release;
            var dragging = _window.GetMouseButton(GlfwMouseButton.Number1) == GlfwButtonAction.Press;
            _currentScene?.HandleInput(_window, delta, shiftHeld, dragging);
        }

        _lastCursor = cursor;
    }

    private void UpdateFpsMetrics()
    {
        float dt = ImGui.GetIO().DeltaTime;
        _fpsTimer += dt;
        _fpsCounter++;
        if (_fpsTimer >= 1.0f)
        {
            _currentFps = _fpsCounter;
            _currentFrameTime = (_fpsTimer / _fpsCounter) * 1000f;
            _fpsCounter = 0;
            _fpsTimer = 0f;
        }
    }

    private void BuildDebugMenu()
    {
        ImGui.SetNextWindowPos(new float2(10, 400), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(450, 240), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Меню отладки (F3)"u8, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"FPS: {_currentFps} ({_currentFrameTime:F2} ms)");
            double allocatedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            ImGui.Text($"Использовано памяти: {allocatedMb:F2} MB");
            ImGui.Separator();

            ImGuiWidgets.PerformancePieChart("Время подсистем кадра:", Profiler.GetRawResults());
        }
        ImGui.End();
    }


    public void Dispose()
    {
        _currentScene?.Dispose();
        _glfwBackend.Dispose();
        _openGlBackend?.Dispose();
        ImGui.DestroyContext(_imGuiContext);
        _renderer.Dispose();
        PhysicsManager.Instance.Dispose();
    }
}
