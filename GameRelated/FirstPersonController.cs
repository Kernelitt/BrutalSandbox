using Brutal.GlfwApi;
using Brutal.JoltApi;
using Brutal.Numerics;
using BrutalSandbox.Framework.Physics;
using BrutalSandbox.Framework.Terrain;
using BrutalSandbox.GameRelated;

namespace BrutalSandbox.Framework.UI;

public sealed class FirstPersonController
{
    public float3 Position = new(32f, 80f, 32f);
    public float Yaw = 0f;
    public float Pitch = 0f;

    public bool IsNoclip = false;

    private const float MoveSpeed = 12f;
    private const float NoclipSpeed = 45f;
    private const float MouseSensitivity = 0.002f;
    private const float JumpForce = 8.5f; 

    private uint _physicsBodyId = 0;
    private bool _isGrounded = false;
    private uint _standingOnBodyId = 0;
    private float3 _platformVelocityAtFeet = float3.Zero;
    public uint PhysicsBodyId => _physicsBodyId;

    public void Initialize()
    {
        var phys = PhysicsManager.Instance;
        var bodyInterface = phys.BodyInterface;


        using (var capsuleSettings = CapsuleShapeSettings.Create(0.5f, 0.4f, default))
        {
            _physicsBodyId = phys.CreateBody(
                capsuleSettings,
                Position,
                floatQuat.Identity,
                MotionType.Dynamic,
                PhysicsManager.LayerMoving,
                "stone_wall"
            );
        }
        bodyInterface.SetMotionQuality(_physicsBodyId, MotionQuality.LinearCast);
    }

    public float4x4 GetViewMatrix()
    {
        float cosPitch = MathF.Cos(Pitch);
        float sinPitch = MathF.Sin(Pitch);
        float cosYaw = MathF.Cos(Yaw);
        float sinYaw = MathF.Sin(Yaw);

        float3 forward = new float3(sinYaw * cosPitch, sinPitch, cosYaw * cosPitch);
        float3 up = new float3(0f, 1f, 0f);

        float3 eyePosition = Position + new float3(0f, 0.88f, 0f);

        return float4x4.CreateLookAt(eyePosition, eyePosition + forward, up);
    }

    public void UpdateLogic(GlfwWindow window, float deltaTime, TerrainManager terrainManager)
    {
        var phys = PhysicsManager.Instance;
        var bodyInterface = phys.BodyInterface;

        if (IsNoclip)
        {
            bodyInterface.SetMotionType(_physicsBodyId, MotionType.Kinematic, Activation.DontActivate);
            HandleNoclipMovement(window, deltaTime);
        }
        else
        {
            bodyInterface.SetMotionType(_physicsBodyId, MotionType.Dynamic, Activation.Activate);
            bodyInterface.GetPositionAndRotation(_physicsBodyId, out Position, out _);

            bodyInterface.SetPositionAndRotation(_physicsBodyId, Position, floatQuat.Identity, Activation.DontActivate);
            bodyInterface.SetAngularVelocity(_physicsBodyId, float3.Zero);

            CheckGrounded(terrainManager);
            HandlePhysicsMovement(window, deltaTime);
        }
    }




    private void CheckGrounded(TerrainManager terrainManager)
    {
        var phys = PhysicsManager.Instance;

        // Сбрасываем старые значения каждый кадр проверки
        _standingOnBodyId = 0;
        _platformVelocityAtFeet = float3.Zero;

        // 1. ПРОВЕРКА ЛАНДШАФТА
        float currentTerrainHeight = terrainManager.GetTerrainHeightAt(Position.X, Position.Z);
        float feetY = Position.Y - 0.9f;
        bool touchingTerrain = feetY <= currentTerrainHeight + 0.15f && feetY >= currentTerrainHeight - 0.2f;

        if (touchingTerrain)
        {
            _isGrounded = true;
            return;
        }

        // 2. ВЫСОКОТОЧНАЯ ПРОВЕРКА НАХОЖДЕНИЯ НА ОБЪЕКТАХ
        float3 rayStart = Position;
        float3 rayDir = new float3(0f, -1f, 0f);
        float maxDistance = 1.05f;

        bool standingOnObject = false;

        foreach (var (bodyId, _) in phys.GetActiveObjects())
        {
            if (bodyId == _physicsBodyId) continue;
            if (phys.BodyInterface.GetMotionType(bodyId) == MotionType.Kinematic) continue;

            phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 center, out _);
            bool isPlatform = phys.BodyInterface.GetMotionType(bodyId) == MotionType.Static;
            float3 halfExtents = isPlatform ? new float3(1.5f, 0.1f, 1.5f) : new float3(0.5f);

            float3 boxMin = center - halfExtents;
            float3 boxMax = center + halfExtents;

            if (RayCasting.RayIntersectsScaledBox(rayStart, rayDir, boxMin, boxMax, out float t, out _))
            {
                if (t * 15f <= maxDistance)
                {
                    _standingOnBodyId = bodyId;
                    standingOnObject = true;
                    break;
                }
            }
        }

        // ИСПРАВЛЕНО: Если стоим на динамическом объекте, рассчитываем его скорость в точке контакта
        if (standingOnObject && _standingOnBodyId != 0 && phys.BodyInterface.GetMotionType(_standingOnBodyId) == MotionType.Dynamic)
        {
            float3 linVel = phys.BodyInterface.GetLinearVelocity(_standingOnBodyId);
            float3 angVel = phys.BodyInterface.GetAngularVelocity(_standingOnBodyId);
            phys.BodyInterface.GetPositionAndRotation(_standingOnBodyId, out float3 blockCenter, out _);

            // Рассчитываем тангенциальную скорость от вращения блока (V = w x r)
            float3 contactRadius = Position - blockCenter;
            float3 rotationalVelocity = float3.Cross(angVel, contactRadius);

            // Полная скорость платформы под ногами игрока
            _platformVelocityAtFeet = linVel + rotationalVelocity;
        }

        _isGrounded = standingOnObject;
    }

    public void HandleMouseLook(double2 cursorDelta)
    {
        Yaw -= (float)cursorDelta.X * MouseSensitivity;
        Pitch = Math.Clamp(Pitch - (float)cursorDelta.Y * MouseSensitivity, -1.4f, 1.4f);
    }

    private void HandleNoclipMovement(GlfwWindow window, float deltaTime)
    {
        float3 moveDir = CalculateInputDirection(window);
        Position += moveDir * NoclipSpeed * deltaTime;

        var phys = PhysicsManager.Instance;
        phys.BodyInterface.SetPositionAndRotation(_physicsBodyId, Position, floatQuat.Identity, Activation.DontActivate);
    }

    private void HandlePhysicsMovement(GlfwWindow window, float deltaTime)
    {
        var phys = PhysicsManager.Instance;
        float3 moveDir = CalculateInputDirection(window);

        float3 currentVelocity = phys.BodyInterface.GetLinearVelocity(_physicsBodyId);

        // ИСПРАВЛЕНО: Целевая скорость игрока теперь рассчитывается относительно скорости блока!
        float3 localTargetVelocity = moveDir * MoveSpeed;
        float3 globalTargetVelocity = localTargetVelocity + _platformVelocityAtFeet;

        float accelerationRate = _isGrounded ? 8f * deltaTime : 2f * deltaTime;

        float newX = float3.Lerp(new float3(currentVelocity.X, 0f, 0f), new float3(globalTargetVelocity.X, 0f, 0f), accelerationRate).X;
        float newZ = float3.Lerp(new float3(0f, 0f, currentVelocity.Z), new float3(0f, 0f, globalTargetVelocity.Z), accelerationRate).Z;

        float3 newVelocity = new float3(newX, currentVelocity.Y, newZ);

        if (_isGrounded && window.GetKey(GlfwKey.Space) == GlfwKeyAction.Press)
        {
            // При прыжке к силе прыжка добавляется вертикальный импульс платформы (если она летит вверх)
            newVelocity.Y = JumpForce + _platformVelocityAtFeet.Y;
            _isGrounded = false;
        }

        phys.BodyInterface.SetLinearVelocity(_physicsBodyId, newVelocity);
    }


    private float3 CalculateInputDirection(GlfwWindow window)
    {
        float3 forward = new float3(MathF.Sin(Yaw), 0f, MathF.Cos(Yaw));
        float3 right = float3.Normalize(float3.Cross(forward, new float3(0f, 1f, 0f)));

        float3 moveDir = float3.Zero;

        if (window.GetKey(GlfwKey.W) == GlfwKeyAction.Press) moveDir += forward;
        if (window.GetKey(GlfwKey.S) == GlfwKeyAction.Press) moveDir -= forward;
        if (window.GetKey(GlfwKey.D) == GlfwKeyAction.Press) moveDir += right;
        if (window.GetKey(GlfwKey.A) == GlfwKeyAction.Press) moveDir -= right;

        if (IsNoclip)
        {
            if (window.GetKey(GlfwKey.Space) == GlfwKeyAction.Press) moveDir += new float3(0f, 1f, 0f);
            if (window.GetKey(GlfwKey.LeftControl) == GlfwKeyAction.Press) moveDir -= new float3(0f, 1f, 0f);
        }

        if (moveDir != float3.Zero) moveDir = float3.Normalize(moveDir);
        return moveDir;
    }

    public void ToggleNoclip()
    {
        IsNoclip = !IsNoclip;
        var phys = PhysicsManager.Instance;
        phys.BodyInterface.SetLinearVelocity(_physicsBodyId, float3.Zero);
    }
}
