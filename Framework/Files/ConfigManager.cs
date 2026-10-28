using System.IO;
using TestSandbox.Framework.Audio;
using TestSandbox.Framework.Localization;

namespace TestSandbox.Framework.Files;

public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.cfg");

    public static void Load(LocalizationManager loc, AudioManager audio)
    {
        if (!File.Exists(ConfigPath))
        {
            Save(loc, audio); // Создаем дефолтный конфиг, если файла нет
            return;
        }

        try
        {
            var lines = File.ReadAllLines(ConfigPath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim();

                switch (key)
                {
                    case "Language":
                        loc.SetLanguage(value);
                        break;
                    case "Volume.Master":
                        if (float.TryParse(value, out float m)) audio.MasterVolume = m;
                        break;
                    case "Volume.Music":
                        if (float.TryParse(value, out float mu)) audio.MusicVolume = mu;
                        break;
                    case "Volume.SFX":
                        if (float.TryParse(value, out float s)) audio.SfxVolume = s;
                        break;
                }
            }
            audio.ApplyVolumes();
            Console.WriteLine("[Config] Конфигурация успешно загружена.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConfigError] Не удалось загрузить конфиг: {ex.Message}");
        }
    }

    public static void Save(LocalizationManager loc, AudioManager audio)
    {
        try
        {
            using var sw = new StreamWriter(ConfigPath, false);
            sw.WriteLine("# Игровые настройки TestSandbox");
            sw.WriteLine($"Language={loc.CurrentLanguage}");
            sw.WriteLine($"Volume.Master={audio.MasterVolume:F2}");
            sw.WriteLine($"Volume.Music={audio.MusicVolume:F2}");
            sw.WriteLine($"Volume.SFX={audio.SfxVolume:F2}");
            Console.WriteLine("[Config] Конфигурация сохранена.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConfigError] Не удалось сохранить конфиг: {ex.Message}");
        }
    }
}
