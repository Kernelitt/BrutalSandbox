using Brutal.ImGuiApi;
using Brutal.Localization;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace TestSandbox.Framework.Localization;

public sealed class LocalizationManager
{
    private readonly string _localizationsFolder;
    private readonly Dictionary<string, string> _currentTranslations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _availableLanguages = new();
    private string _currentLanguage = "en_US";

    public IReadOnlyList<string> AvailableLanguages => _availableLanguages;
    public string CurrentLanguage => _currentLanguage;

    public LocalizationManager(string folderPath = "localizations")
    {
        _localizationsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folderPath);

        // Регистрируем делегат как глобальный провайдер
        LStrings.SetLocalizedStringProvider(BrutalLocalizationProvider);

        ScanLocalizations();
    }

    public void ScanLocalizations()
    {
        _availableLanguages.Clear();

        if (!Directory.Exists(_localizationsFolder))
        {
            try
            {
                Directory.CreateDirectory(_localizationsFolder);
                File.WriteAllText(Path.Combine(_localizationsFolder, "ru_RU.lang"), "# Пример\nDevPanel.Title=Панель разработчика");
            }
            catch
            {
                return;
            }
        }

        var files = Directory.GetFiles(_localizationsFolder, "*.lang");
        foreach (var file in files)
        {
            var langName = Path.GetFileNameWithoutExtension(file);
            _availableLanguages.Add(langName);
        }

        if (!_availableLanguages.Contains("en_US"))
        {
            _availableLanguages.Insert(0, "en_US");
        }
    }

    public bool SetLanguage(string languageName)
    {
        _currentTranslations.Clear();
        _currentLanguage = languageName;

        // Для en_US оставляем словарь пустым, чтобы ToUtf8() сбросил строки к дефолтным значениям
        if (languageName.Equals("en_US", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var filePath = Path.Combine(_localizationsFolder, $"{languageName}.lang");
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var lines = File.ReadAllLines(filePath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith(';'))
                    continue;

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex > 0)
                {
                    var key = line.Substring(0, separatorIndex).Trim();
                    var value = line.Substring(separatorIndex + 1).Trim();
                    _currentTranslations[key] = value;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LocError] Failed to load language {languageName}: {ex.Message}");
            return false;
        }
    }

    private bool BrutalLocalizationProvider(LString lString, [NotNullWhen(true)] out string? text)
    {
        return _currentTranslations.TryGetValue(lString.Id.ToString(), out text);
    }
}

public static class LocalizationExtensions
{
    /// <summary>
    /// Автоматически синхронизирует перевод для строк без аргументов и выводит в ImGui.
    /// </summary>
    public static ReadOnlySpan<byte> ToUtf8(this LString lString)
    {
        var provider = LStrings.LocalizedStringProvider;
        if (provider != null)
        {
            if (provider(lString, out string? newTranslation) && newTranslation != null)
            {
                if (lString.Localized != newTranslation)
                {
                    lString.Localize(newTranslation);
                }
            }
            else
            {
                if (lString.Localized != lString.Template)
                {
                    lString.RemoveLocalization();
                }
            }
        }

        string localized = lString.Format();
        ImString imguiStr = new ImString(localized.AsSpan());
        return imguiStr.AsSpan();
    }

    /// <summary>
    /// Исправлено: Синхронизирует шаблон и производит точечное форматирование аргументов.
    /// Использование явного string.Format гарантирует, что {API} заменится на строку "Vulkan 1.3",
    /// а не на имя типа массива.
    /// </summary>
    public static ReadOnlySpan<byte> ToUtf8(this LString lString, params object?[] args)
    {
        var provider = LStrings.LocalizedStringProvider;
        if (provider != null)
        {
            if (provider(lString, out string? newTranslation) && newTranslation != null)
            {
                if (lString.Localized != newTranslation)
                {
                    lString.Localize(newTranslation);
                }
            }
            else
            {
                if (lString.Localized != lString.Template)
                {
                    lString.RemoveLocalization();
                }
            }
        }

        // РЕШЕНИЕ ПРОБЛЕМЫ: Сначала берем актуальный чистый перевод (например, "Active API: {0}")
        // и форматируем его через стандартный string.Format, передавая массив аргументов напрямую.
        // Это обходит внутренний баг упаковки params во фреймворке.
        string rawText = lString.Localized;

        // Подстраховка для формата: если в .lang файле используется старый синтаксис {API},
        // временно подменяем его на индекс {0} для совместимости со string.Format, либо используем lString.Format
        string formatted;
        try
        {
            // Прямая замена через штатный обработчик LString, но с развертыванием параметров
            formatted = string.Format(rawText.Replace("{API}", "{0}"), args);
        }
        catch
        {
            // На случай, если в шаблоне используется другой вид форматирования
            formatted = lString.Format(args);
        }

        ImString imguiStr = new ImString(formatted.AsSpan());
        return imguiStr.AsSpan();
    }
}