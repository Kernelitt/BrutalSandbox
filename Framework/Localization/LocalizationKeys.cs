using Brutal.Localization;

namespace TestSandbox.Framework.Localization;

public static class LocalizationKeys
{
    // Группа: Интерфейс разработчика
    public static readonly LString DevPanelTitle = LStrings.Create("DevPanel.Title", "Developer Panel");
    public static readonly LString ActiveApi = LStrings.Create("DevPanel.ActiveApi", "Active Graphics API: {API}");
    public static readonly LString CameraControl = LStrings.Create("DevPanel.CameraControl", "Camera Controls:");
    public static readonly LString CameraDistance = LStrings.Create("DevPanel.CameraDistance", "Distance");
    public static readonly LString CameraYaw = LStrings.Create("DevPanel.CameraYaw", "Yaw");
    public static readonly LString CameraPitch = LStrings.Create("DevPanel.CameraPitch", "Pitch");
    public static readonly LString ResetCamera = LStrings.Create("DevPanel.ResetCamera", "Reset Camera");

    // Группа: Настройки сцены
    public static readonly LString SceneSettings = LStrings.Create("DevPanel.SceneSettings", "Scene Settings:");
    public static readonly LString LightIntensity = LStrings.Create("DevPanel.LightIntensity", "Light Intensity");

    // Группа: Управление
    public static readonly LString ControlsHint = LStrings.Create("Game.ControlsHint", "Controls: [Tab] import a model, left-drag to orbit, scroll to zoom.");
    // Группа: Отладка
    public static readonly LString DebugMenuTitle = LStrings.Create("Debug.Title", "Debug Information (F3)");
    public static readonly LString DebugFps = LStrings.Create("Debug.Fps", "FPS: {0} ({1:F2} ms)");
    public static readonly LString DebugMemory = LStrings.Create("Debug.Memory", "GC Allocated Memory: {0:F2} MB");
}
