using System;
using System.Collections.Generic;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace TestSandbox.Framework.Physics;

public sealed class PhysicsManager : IDisposable
{
    private static PhysicsManager? _instance;
    public static PhysicsManager Instance => _instance ?? throw new InvalidOperationException("PhysicsManager не инициализирован!");

    public const ushort LayerStatic = 0;
    public const ushort LayerMoving = 1;

    private TempAllocatorImpl _tempAllocator;
    private JobSystemThreadPool _jobSystem;
    private BroadPhaseLayerInterfaceTable _broadPhaseLayers;
    private ObjectLayerPairFilterTable _pairFilter;
    private ObjectVsBroadPhaseLayerFilterTable _broadPhaseFilter;
    private PhysicsSystem _physicsSystem;
    private BodyInterface _bodyInterface;

    private readonly List<(uint BodyId, string AssetName)> _physicsObjects = new();
    private double _accumulator;
    private const float TimeStep = 1.0f / 60.0f;

    public BodyInterface BodyInterface => _bodyInterface;

    public static void Initialize()
    {
        if (_instance != null) return;

        // Библиотечная инициализация Jolt подсистемы
        Jolt.RegisterDefaultAllocator();
        Factory.Instance = Factory.Create();
        Jolt.RegisterTypes();

        _instance = new PhysicsManager();
    }

    private PhysicsManager()
    {
        // Выделение памяти под временные вычисления физики (10 МБ, как в примерах Brutal)
        _tempAllocator = TempAllocatorImpl.Create(100 * 1024 * 1024);

        // Пул потоков для многопоточной обработки физики (8 потоков)
        _jobSystem = JobSystemThreadPool.Create(2048, 16, -1);

        // Настройка слоев широкой фазы коллизий
        _broadPhaseLayers = BroadPhaseLayerInterfaceTable.Create(2, 2);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(LayerStatic, 0);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(LayerMoving, 1);

        // Настройка фильтрации пар (статическое с движущимся, движущееся с движущимся)
        _pairFilter = ObjectLayerPairFilterTable.Create(2);
        _pairFilter.EnableCollision(LayerStatic, LayerMoving);
        _pairFilter.EnableCollision(LayerMoving, LayerMoving);

        _broadPhaseFilter = ObjectVsBroadPhaseLayerFilterTable.Create(_broadPhaseLayers, 2, _pairFilter, 2);

        // Создание и запуск ядра физического движка
        _physicsSystem = PhysicsSystem.Create();
        _physicsSystem.Init(10240, 0, 10240, 10240, _broadPhaseLayers, _broadPhaseFilter, _pairFilter);

        _bodyInterface = _physicsSystem.GetBodyInterface();
    }

    /// <summary>
    /// Создает и регистрирует физическое тело в мире.
    /// </summary>
    public uint CreateBody(ShapeSettings shapeSettings, float3 position, floatQuat rotation, MotionType motionType, ushort layer, string assetName)
    {
        using var settings = BodyCreationSettings.Create(shapeSettings, position, rotation, motionType, layer);
        var activation = (motionType == MotionType.Static) ? Activation.DontActivate : Activation.Activate;

        uint bodyId = _bodyInterface.CreateAndAddBody(settings, activation);
        _physicsObjects.Add((bodyId, assetName));

        return bodyId;
    }

    /// <summary>
    /// Продвигает симуляцию вперед с фиксированным шагом (фикс накопителя времени).
    /// </summary>
    public void Update(double deltaTime)
    {
        // Ограничение накопления при сильных лагах / фризах
        _accumulator += Math.Min(deltaTime, 0.25);

        while (_accumulator >= TimeStep)
        {
            _physicsSystem.Update(TimeStep, 1, _tempAllocator, _jobSystem);
            _accumulator -= TimeStep;
        }
    }

    /// <summary>
    /// Возвращает список всех зарегистрированных физических тел для последующего рендеринга.
    /// </summary>
    public IEnumerable<(uint BodyId, string AssetName)> GetActiveObjects() => _physicsObjects;

    public void Dispose()
    {

        foreach (var obj in _physicsObjects)
        {
            _bodyInterface.RemoveBody(obj.BodyId);
            _bodyInterface.DestroyBody(obj.BodyId);
        }
        _physicsObjects.Clear();
        
        _physicsSystem.Dispose();
        _broadPhaseFilter.Dispose();
        _pairFilter.Dispose();
        _broadPhaseLayers.Dispose();
        _jobSystem.Dispose();
        _tempAllocator.Dispose();

        Jolt.UnregisterTypes();
        Factory.DestroyInstance();

        _instance = null;
    }
}
