using Brutal.FmodApi;
using Brutal.Strings;

namespace BrutalSandbox.Framework.Audio;

public sealed class AudioManager : IDisposable
{
    private static AudioManager? _instance;
    public static AudioManager Instance => _instance ?? throw new InvalidOperationException("AudioManager не инициализирован.");

    private readonly FmodSystem _system;
    private Channel _musicChannel;

    // Канальные группы FMOD для иерархического микширования
    private ChannelGroup _masterGroup;
    private ChannelGroup _musicGroup;
    private ChannelGroup _sfxGroup;

    // Локальные параметры для отображения в ползунках ImGui
    public float MasterVolume = 1.0f;
    public float MusicVolume = 0.8f;
    public float SfxVolume = 1.0f;

    public FmodSystem System => _system;

    public static void Initialize()
    {
        _instance = new AudioManager();
    }

    private AudioManager()
    {
        _system = Fmod.SystemCreate(Fmod.VERSION);
        _system.Init(32, InitFlags.Normal, IntPtr.Zero);

        // 1. Получаем доступ к главному мастер-каналу FMOD
        _masterGroup = _system.GetMasterChannelGroup();

        // 2. Создаем отдельные подгруппы для типов ассетов
        _musicGroup = _system.CreateChannelGroup(new String8("MusicGroup"));
        _sfxGroup = _system.CreateChannelGroup(new String8("SfxGroup"));

        // 3. Строим иерархию: добавляем подгруппы внутрь мастер-группы
        _masterGroup.AddGroup(_musicGroup, false);
        _masterGroup.AddGroup(_sfxGroup, false);

        // Применяем начальные значения громкости
        ApplyVolumes();

        _system.GetVersion(out var version, out _);
        Console.WriteLine($"[Audio] Иерархический микшер FMOD запущен.");
    }

    public void Update()
    {
        _system.Update();
    }

    /// <summary>
    /// Принудительно обновляет уровни громкости в микшере FMOD
    /// </summary>
    public void ApplyVolumes()
    {
        _masterGroup.SetVolume(MasterVolume);
        _musicGroup.SetVolume(MusicVolume);
        _sfxGroup.SetVolume(SfxVolume);
    }

    public void PlaySound(Sound sound, float volume = 1.0f)
    {
        if (sound.IsNull()) return;
        
        // Передаем _sfxGroup в качестве родительской группы канала.
        // FMOD автоматически применит к этому звуку множитель громкости SFX и Master.
        var channel = _system.PlaySound(sound, _sfxGroup, false);
        channel.SetVolume(volume);
    }

    public void PlayMusic(Sound musicStream, float volume = 1.0f)
    {
        if (musicStream.IsNull()) return;

        if (!_musicChannel.IsNull())
        {
            try
            {
                _musicChannel.Stop();
            }
            catch (FmodResultException) { }
        }

        // Запускаем музыку внутри изолированной группы _musicGroup
        _musicChannel = _system.PlaySound(musicStream, _musicGroup, false);
        _musicChannel.SetVolume(volume);
    }

    public void Dispose()
    {
        _system.Close();
        _system.Release();
    }
}
