using Brutal;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.ImGuiApi.FreeType;
using Brutal.JoltApi;
using Brutal.Numerics;
using System.Numerics;
using TestSandbox.Framework;
using TestSandbox.Framework.Audio;
using TestSandbox.Framework.Files;
using TestSandbox.Framework.Localization;
using TestSandbox.Framework.Physics;
using TestSandbox.Framework.Scenes;
using TestSandbox.Framework.UI;

namespace TestSandbox;

public sealed class MeshViewer : IDisposable
{
    private readonly GlfwWindow _window;
    private readonly IRenderer _renderer;
    private readonly OrbitCamera _camera = new();
    private readonly bool _useOpenGl;

    private readonly ImGuiContextPtr _imGuiContext;
    private readonly GlfwBackend _glfwBackend;
    private readonly OpenGlBackend? _openGlBackend;

    private LocalizationManager _locManager;

    private bool _orbiting;
    private bool _panning;
    private double2 _lastCursor;

    private ImFontPtr imGuiFont;

    // Состояния для F3 меню отладки
    private bool _showDebugMenu = false;
    private bool _f3WasPressed = false;

    // Метрики FPS
    private float _fpsTimer = 0f;
    private int _fpsCounter = 0;
    private int _currentFps = 0;
    private float _currentFrameTime = 0f;

    private readonly AssetManager _assetManager;
    private MeshData _graphicCubeMesh;
    private MeshData _graphicPlaneMesh;
    public MeshViewer(GlfwWindow window, bool useOpenGl)
    {
        _window = window;
        _useOpenGl = useOpenGl;

        // 1. Инициализируем аудиоподсистему и загрузчик ассетов в самом начале
        AudioManager.Initialize();
        _assetManager = new AssetManager();
        _assetManager.LoadAllAssets();

        PhysicsManager.Initialize();

        _locManager = new LocalizationManager();
        _locManager.SetLanguage("ru_RU");

        // Загружаем сохраненные настройки или создаем их
        ConfigManager.Load(_locManager, AudioManager.Instance);

        SceneManager.Instance.OnStateChanged += (state) => {
            if (state == GameState.MainMenu)
                AudioManager.Instance.PlayMusic(_assetManager.GetMusic("ambient"));
        };

        // Симулируем долгую загрузку ассетов, после чего переходим в главное меню
        SceneManager.Instance.ChangeState(GameState.MainMenu);

        _imGuiContext = ImGui.CreateContext();
        ImGui.GetIO().IniFilename = null;

        _renderer = useOpenGl ? new OpenGlRenderer(window, _assetManager) : new VulkanRenderer(window);
        _assetManager.InitializeTextures(_renderer);

        _window.OnScroll += (_, offset) => _camera.Zoom(offset.Y);

        var clientApi = useOpenGl ? GlfwBackend.ClientApi.OpenGL : GlfwBackend.ClientApi.Vulkan;
        _glfwBackend = new GlfwBackend(window, clientApi);

        if (_useOpenGl) _openGlBackend = new OpenGlBackend();

        var io = ImGui.GetIO();
        io.DisplaySize = new float2(1280, 720);
        io.DeltaTime = 1f / 60f;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;

        var fonts = io.Fonts;
        fonts.SetFontLoader(ImGuiFreeType.GetFontLoader());
        imGuiFont = fonts.AddFontFromFileTTF("fonts\\BrianneTod.ttf", 24f);

        _graphicCubeMesh = Framework.Graphics.PrimitiveGenerator.CreateCube();
        _graphicPlaneMesh = Framework.Graphics.PrimitiveGenerator.CreatePlane();

        Console.WriteLine(LocalizationKeys.ControlsHint.Format());
    }

    public void Frame()
    {
        float3 lightPos = new float3(15f, 25f, 10f);
        float3 lightTarget = new float3(0f, 0f, 0f);

        // Матрицы светового конвейера (Орто-проекция, так как свет направленный)
        float4x4 lightView = float4x4.CreateLookAt(lightPos, lightTarget, float3.UnitY);
        float4x4 lightProjection = float4x4.CreateOrthographicOffCenter(-30f, 35f, -30f, 30f, 1f, 100f);
        float4x4 lightSpaceMatrix = lightView * lightProjection;

        var framebufferSize = _window.FramebufferSize;
        if (framebufferSize.X == 0 || framebufferSize.Y == 0) return;

        Profiler.BeginMemorySample();
        Profiler.BeginSample("Total Frame");

        // Обновляем микшер звукового движка FMOD каждый кадр
        Profiler.BeginSample("Audio Update");
        AudioManager.Instance.Update();
        Profiler.EndSample("Audio Update");

        Profiler.BeginSample("Physics Update");
        PhysicsManager.Instance.Update(ImGui.GetIO().DeltaTime);
        Profiler.EndSample("Physics Update");

        Profiler.BeginSample("Input & Logic");
        _renderer.Resize(framebufferSize.X, framebufferSize.Y);
        HandleInput();
        var aspect = _renderer.Height > 0 ? (float)_renderer.Width / _renderer.Height : 1f;
        var viewProj = _camera.ViewProjection(aspect);
        Profiler.EndSample("Input & Logic");

        _glfwBackend.NewFrame();
        if (_useOpenGl) _openGlBackend?.NewFrame();
        ImGui.NewFrame();
        ImString.SharedStorage.Reset();

        BuildGameInterface();
        if (_showDebugMenu) BuildDebugMenu();

        ImGui.Render();

        _renderer.BeginFrame(viewProj);

        var phys = TestSandbox.Framework.Physics.PhysicsManager.Instance;
        var bodyInterface = phys.BodyInterface;

        // 3. Перебираем объекты, используя постоянные меши из полей класса
        foreach (var (bodyId, assetName) in phys.GetActiveObjects())
        {
            bodyInterface.GetPositionAndRotation(bodyId, out var position, out var rotation);
            bool isStatic = bodyInterface.GetMotionType(bodyId) == MotionType.Static;

            if (isStatic)
            {
                float4x4 model = float4x4.CreateScale(new float3(25f, 1f, 25f))
                                * float4x4.CreateFromQuaternion(rotation)
                                * float4x4.CreateTranslation(position);

                // Передаем постоянный меш земли
                _renderer.RenderMesh(_graphicPlaneMesh, model, assetName);
            }
            else
            {
                float4x4 model = float4x4.CreateFromQuaternion(rotation)
                                * float4x4.CreateTranslation(position);

                // Передаем постоянный меш куба
                _renderer.RenderMesh(_graphicCubeMesh, model, assetName);
            }
        }

        // 4. Закрываем кадр кастомной лямбдой для отрисовки ImGui поверх 3D
        _renderer.EndFrame(() =>
        {
            if (_useOpenGl) _openGlBackend?.RenderDrawData(ImGui.GetDrawData());
        });

        Profiler.EndSample("Total Frame");
        Profiler.EndMemorySample();

        UpdateFpsMetrics();
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

    private void BuildGameInterface()
    {
        ImGui.PushFont(imGuiFont, 14f);

        // Распределяем рендер интерфейса в зависимости от текущего состояния
        switch (SceneManager.Instance.CurrentState)
        {
            case GameState.Loading:
                RenderLoadingScreen();
                break;
            case GameState.MainMenu:
                RenderMainMenuScreen();
                break;
            case GameState.Gameplay:
                RenderGameplayUi();
                break;
        }

        ImGui.PopFont();
    }
    private void BuildPhysicsScene()
    {
        var phys = PhysicsManager.Instance;
        var bodyInterface = phys.BodyInterface;

        // 1. Создаем землю на высоте Y = 0 (Размеры 50х1х50, полуразмер — 25х0.5х25)
        var groundHalfSize = new float3(25f, 0.5f, 25f);
        using (var groundShapeSettings = BoxShapeSettings.Create(groundHalfSize, 0.05f, default))
        {
            phys.CreateBody(
                groundShapeSettings,
                new float3(0f, -0.5f, 0f), // Верхняя грань ровно в Y = 0
                floatQuat.Identity,
                MotionType.Static,
                PhysicsManager.LayerStatic,
                "stone_wall" // Текстура для земли
            );
        }

        // 2. Создаем башню 5х5х25 из динамических кубов
        // Каждый куб имеет размер 1х1х1 (полуразмер — 0.5х0.5х0.5)
        var boxHalfSize = new float3(0.5f);
        using (var boxShapeSettings = BoxShapeSettings.Create(boxHalfSize, 0.05f, default))
        {
            int sizeX = 5;
            int sizeZ = 5;
            int sizeY = 25;

            float spacing = 1.02f; // Небольшой зазор между кубами, чтобы они не спавнились друг в друге

            for (int y = 0; y < sizeY; y++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    for (int z = 0; z < sizeZ; z++)
                    {
                        // Центрируем башню по осям X и Z относительно центра координат
                        float posX = (x - sizeX / 2f) * spacing;
                        float posZ = (z - sizeZ / 2f) * spacing;
                        float posY = 0.5f + (y * spacing); // Первый ряд ложится строго на землю (Y = 0)

                        phys.CreateBody(
                            boxShapeSettings,
                            new float3(posX, posY, posZ),
                            floatQuat.Identity,
                            MotionType.Dynamic,
                            PhysicsManager.LayerMoving,
                            "stone_wall" // Текстура для кубов башни
                        );
                    }
                }
            }
        }

        // Автоматически наводим камеру повыше на нашу башню
        _camera.Distance = 35f;
    }
    private void RenderLoadingScreen()
    {
        ImGui.SetNextWindowPos(new float2(500, 300), ImGuiCond.Always);
        if (ImGui.Begin("Загрузка ассетов..."u8, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Пожалуйста, подождите. Идет распаковка шейдеров и текстур..."u8);
        }
        ImGui.End();
    }

    private void RenderMainMenuScreen()
    {
        ImGui.SetNextWindowPos(new float2(450, 200), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(380, 360), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Главное Меню игры"u8))
        {
            ImGui.Text("Добро пожаловать в 3D Brutal Engine!"u8);
            ImGui.Separator();

            if (ImGui.Button("ЗАПУСТИТЬ ИГРУ (ГЕЙМПЛЕЙ)"u8, new float2(-1, 40)))
            {
                SceneManager.Instance.ChangeState(GameState.Gameplay);
            }

            ImGui.Spacing();
            ImGui.Text("Настройки системы:"u8);

            // Выбор языка
            ImGui.Text("Язык (Language):"u8);
            if (ImGui.BeginCombo("##langCombo"u8, _locManager.CurrentLanguage))
            {
                foreach (var lang in _locManager.AvailableLanguages)
                {
                    if (ImGui.Selectable(lang, _locManager.CurrentLanguage == lang))
                    {
                        _locManager.SetLanguage(lang);
                        ConfigManager.Save(_locManager, AudioManager.Instance); // Сохраняем
                    }
                }
                ImGui.EndCombo();
            }

            // Настройки звука
            var audio = AudioManager.Instance;
            bool volChanged = false;

            if (ImGui.SliderFloat("Общая громкость"u8, ref audio.MasterVolume, 0f, 1f)) volChanged = true;
            if (ImGui.SliderFloat("Музыка"u8, ref audio.MusicVolume, 0f, 1f)) volChanged = true;
            if (ImGui.SliderFloat("Эффекты"u8, ref audio.SfxVolume, 0f, 1f)) volChanged = true;

            if (volChanged)
            {
                audio.ApplyVolumes();
                ConfigManager.Save(_locManager, audio); // Автосохранение при сдвиге
            }

            ImGui.Separator();
            if (ImGui.Button("Выйти из игры"u8, new float2(-1, 30))) _window.ShouldClose = true;
        }
        ImGui.End();
    }

    private void RenderGameplayUi()
    {
        // Внутри метода RenderGameplayUi()
        if (SceneManager.Instance.CurrentState == GameState.Gameplay)
        {
            // Просто передаем имя ассета, которое мы хотим наложить на меш!
            _renderer.BindMaterialTexture("stone_wall");
        }


        ImGui.SetNextWindowPos(new float2(10, 10), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(350, 250), ImGuiCond.FirstUseEver);

        if (ImGui.Begin(LocalizationKeys.DevPanelTitle.ToUtf8()))
        {
            ImGui.Text("Режим: Симуляция 3D Мира"u8);
            if (ImGui.Button("Вернуться в Главное Меню"u8))
            {
                SceneManager.Instance.ChangeState(GameState.MainMenu);
            }
            ImGui.Separator();

            // НОВЫЙ БЛОК: Процедурные примитивы с гарантированной UV разверткой
            ImGui.Text("Генератор тестовых мешей:"u8);


            ImGui.Separator();
            if (ImGui.Button("Сгенерировать Физическую Башню (5х5х25)"u8, new float2(-1, 40)))
            {
                BuildPhysicsScene();
            }
            ImGui.SliderFloat(LocalizationKeys.CameraDistance.ToUtf8(), ref _camera.Distance, 0.5f, 50f);
        }
        ImGui.End();
    }


    private void BuildDebugMenu()
    {
        ImGui.PushFont(imGuiFont, 14f);
        ImGui.SetNextWindowPos(new float2(10, 400), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(450, 240), ImGuiCond.FirstUseEver);

        if (ImGui.Begin(LocalizationKeys.DebugMenuTitle.ToUtf8(), ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text(LocalizationKeys.DebugFps.ToUtf8(_currentFps, _currentFrameTime));
            double allocatedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            ImGui.Text(LocalizationKeys.DebugMemory.ToUtf8(allocatedMb));
            ImGui.Separator();

            // ВЫЗОВ УНИВЕРСАЛЬНОГО КАСОМНОГО МЕТОДА ИЗ ОТДЕЛЬНОГО ФАЙЛА
            ImGuiWidgets.PerformancePieChart("Время подсистем кадра:", Profiler.GetRawResults());
        }
        ImGui.End();
        ImGui.PopFont();
    }

    private void HandleInput()
    {
        // Проверка нажатия кнопки F3 (с защитой от непрерывного срабатывания)
        bool f3Pressed = _window.GetKey(GlfwKey.F3) == GlfwKeyAction.Press;
        if (f3Pressed && !_f3WasPressed)
        {
            _showDebugMenu = !_showDebugMenu; // Меняем состояние видимости
        }
        _f3WasPressed = f3Pressed;
        if (ImGui.GetIO().WantCaptureMouse)
        {
            _lastCursor = _window.CursorPos;
            return;
        }
        if (_window.GetKey(GlfwKey.Escape) == GlfwKeyAction.Press) _window.ShouldClose = true;
        if (_window.GetKey(GlfwKey.Tab) == GlfwKeyAction.Press) LoadMesh();

        var cursor = _window.CursorPos;
        var deltaX = cursor.X - _lastCursor.X;
        var deltaY = cursor.Y - _lastCursor.Y;
        var shiftHeld = _window.GetKey(GlfwKey.LeftShift) != GlfwKeyAction.Release;
        var dragging = _window.GetMouseButton(GlfwMouseButton.Number1) == GlfwButtonAction.Press;
        if (dragging && shiftHeld)
        {
            if (_panning) _camera.Pan(deltaX, deltaY);
            _panning = true; _orbiting = false;
        }
        else if (dragging)
        {
            if (_orbiting) _camera.Orbit(deltaX, deltaY);
            _orbiting = true; _panning = false;
        }
        else
        {
            _orbiting = false; _panning = false;
        }
        _lastCursor = cursor;
    }
    private void LoadMesh()
    {
        var mesh = MeshImport.Load("test.glb");
        if (mesh is null) return;

        _camera.Frame(BoundingRadius(mesh));
    }
    private static float BoundingRadius(MeshData mesh)
    {
        var radiusSquared = 0f;
        foreach (var vertex in mesh.Vertices)
        {
            var distanceSquared = vertex.Position.X * vertex.Position.X +
            vertex.Position.Y * vertex.Position.Y +
            vertex.Position.Z * vertex.Position.Z;
            radiusSquared = MathF.Max(radiusSquared, distanceSquared);
        }
        return MathF.Sqrt(radiusSquared);
    }
    public void Dispose()
    {
        _glfwBackend.Dispose();
        _openGlBackend?.Dispose();
        ImGui.DestroyContext(_imGuiContext);
        _renderer.Dispose();
    }
}