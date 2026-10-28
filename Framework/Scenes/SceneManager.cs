namespace TestSandbox.Framework.Scenes;

public sealed class SceneManager
{
    private static SceneManager? _instance;
    public static SceneManager Instance => _instance ??= new SceneManager();

    public GameState CurrentState { get; private set; } = GameState.Loading;

    // Событие для отслеживания смены состояния (например, для автозапуска музыки при переходе в меню)
    public event Action<GameState>? OnStateChanged;

    private SceneManager() { }

    public void ChangeState(GameState newState)
    {
        if (CurrentState == newState) return;

        CurrentState = newState;
        OnStateChanged?.Invoke(newState);
        Console.WriteLine($"[SceneManager] Состояние игры изменено на: {newState}");
    }
}
