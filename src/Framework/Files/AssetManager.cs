using Brutal.FmodApi;
using Brutal.ImGuiApi;
using Brutal.OpenGlApi;
using Brutal.Strings;
using Brutal.TextureApi;
using System.IO;

namespace BrutalSandbox.Framework.Audio;

public sealed class AssetManager : IDisposable
{
    private readonly string _soundsPath;
    private readonly string _musicPath;
    private readonly string _texturesPath;

    private readonly Dictionary<string, Sound> _sounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Sound> _music = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GlTexture> _glTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITexture> _vulkanTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITexture> _cpuTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GlTexture> _generatedIconTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, ImTextureRef> _imguiIcons = new();

    public AssetManager(string rootAssetsFolder = "assets")
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _soundsPath = Path.Combine(baseDir, rootAssetsFolder, "sounds");
        _musicPath = Path.Combine(baseDir, rootAssetsFolder, "music");
        _texturesPath = Path.Combine(baseDir, rootAssetsFolder, "textures");

        EnsureDirectoriesExist();
    }

    private void EnsureDirectoriesExist()
    {
        if (!Directory.Exists(_soundsPath)) Directory.CreateDirectory(_soundsPath);
        if (!Directory.Exists(_musicPath)) Directory.CreateDirectory(_musicPath);
        if (!Directory.Exists(_texturesPath)) Directory.CreateDirectory(_texturesPath);
    }
    private readonly Dictionary<string, string> _texturePaths = new(StringComparer.OrdinalIgnoreCase);

    public void LoadAllAssets()
    {
        LoadAllAudio();

        if (Directory.Exists(_texturesPath))
        {
            var textureFiles = Directory.GetFiles(_texturesPath, "*.*")
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase) || // Добавили KTX2
                            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)).ToArray();

            foreach (var file in textureFiles)
            {
                string assetName = Path.GetFileNameWithoutExtension(file);
                _texturePaths[assetName] = file;
                Console.WriteLine($"[Assets] Найдена текстура на диске: '{assetName}'");
            }
        }
    }
    public void RegisterGeneratedIconRef(uint blockId, ImTextureRef textureRef)
    {
        _imguiIcons[blockId] = textureRef;
    }

    public ImTextureRef GetGeneratedIconRef(uint blockId)
    {
        // Если у нас есть зарегистрированная 3D-иконка (из OpenGL или Vulkan), возвращаем её
        if (_imguiIcons.TryGetValue(blockId, out var iconRef))
        {
            return iconRef;
        }

        // Откатная логика, если иконка ещё не создана
        if (_glTextures.TryGetValue("stone_wall", out var glTex))
        {
            return new ImTextureRef { _TexID = new ImTextureID((nint)glTex.Id) };
        }

        return default;
    }

    public unsafe void InitializeTextures(IRenderer renderer)
    {
        // Проверяем режим рендеринга
        bool isGlMode = renderer is OpenGlRenderer;

        foreach (var kvp in _texturePaths)
        {
            string assetName = kvp.Key;
            string filePath = kvp.Value;

            var loadedTex = TextureLoader.LoadFromFile(filePath);

            if (!isGlMode)
            {
                // === РЕЖИМ VULKAN (РАБОТАЕТ ОТЛИЧНО) ===
                renderer.InitializeTextureOnDevice(assetName, loadedTex);
                _vulkanTextures[assetName] = loadedTex;
                continue;
            }

            // === РЕЖИМ OPENGL (СИНХРОНИЗИРОВАН С ЛОГИКОЙ VULKAN) ===
            GlTexture[] glTexturesArray = [default];
            OpenGl.GenTextures(glTexturesArray);
            GlTexture glTexture = glTexturesArray[0];
            OpenGl.BindTexture(OpenGl.GL_TEXTURE_2D, glTexture.Id);

            int levels = loadedTex.LevelCount > 0 ? loadedTex.LevelCount : 1;

            for (var level = 0; level < levels; level++)
            {
                var width = loadedTex.Extent.X >> level;
                var height = loadedTex.Extent.Y >> level;
                if (width <= 0) width = 1;
                if (height <= 0) height = 1;

                var rawData = loadedTex.ImageData(level, 0, 0);
                if (rawData.Length == 0) continue;

                // ВЫЧИСЛЯЕМ РЕАЛЬНОЕ КОЛИЧЕСТВО БАЙТ НА ПИКСЕЛЬ (как в вашем коде Vulkan)
                int bytesPerPixel = rawData.Length / (width * height);

                int internalFormat;
                uint pixelFormat;
                uint dataType = (uint)OpenGl.GL_UNSIGNED_BYTE;

                byte[] managedArray;

                // Конвертируем ТАК ЖЕ, КАК В ВАШЕМ VULKAN-РЕНДЕРЕРЕ:
                // Если в файле физически 3 байта на пиксель (RGB), пересобираем буфер в совместимый 4-канальный RGBA
                if (bytesPerPixel == 3)
                {
                    managedArray = new byte[width * height * 4];
                    for (int i = 0; i < width * height; i++)
                    {
                        if (i * 3 + 2 < rawData.Length)
                        {
                            managedArray[i * 4 + 0] = rawData[i * 3 + 0]; // R
                            managedArray[i * 4 + 1] = rawData[i * 3 + 1]; // G
                            managedArray[i * 4 + 2] = rawData[i * 3 + 2]; // B
                            managedArray[i * 4 + 3] = 255;                // A (Непрозрачно)
                        }
                    }

                    internalFormat = (int)OpenGl.GL_RGBA8;
                    pixelFormat = (uint)OpenGl.GL_RGBA;
                }
                else
                {
                    // Для grass_tile, stone_wall и остальных изначально 4-канальных текстур
                    string formatStr = loadedTex.Format.ToString().ToLowerInvariant();
                    if (formatStr.Contains("bgra"))
                    {
                        internalFormat = (int)OpenGl.GL_RGBA8;
                        pixelFormat = (uint)OpenGl.GL_BGRA;
                    }
                    else
                    {
                        internalFormat = (int)OpenGl.GL_RGBA8;
                        pixelFormat = (uint)OpenGl.GL_RGBA;
                    }

                    // Переводим Span в управляемый стабильный массив C# .ToArray()
                    managedArray = rawData.ToArray();
                }

                // Выставляем стандартные безопасные параметры выравнивания под 4-байтовую RGBA сетку
                OpenGl.PixelStorei(OpenGl.GL_UNPACK_ALIGNMENT, 4);
                OpenGl.PixelStorei(OpenGl.GL_UNPACK_ROW_LENGTH, 0);

                fixed (byte* ptr = managedArray)
                {
                    OpenGl.TexImage2D(OpenGl.GL_TEXTURE_2D, level, internalFormat, width, height, 0, pixelFormat, dataType, ptr);
                }
            }

            OpenGl.TexParameterFloat(OpenGl.GL_TEXTURE_2D, OpenGl.GL_TEXTURE_MAX_LEVEL, levels - 1);
            OpenGl.TexParameterFloat(OpenGl.GL_TEXTURE_2D, OpenGl.GL_TEXTURE_MIN_FILTER, levels > 1 ? (int)OpenGl.GL_LINEAR_MIPMAP_LINEAR : (int)OpenGl.GL_LINEAR);
            OpenGl.TexParameterFloat(OpenGl.GL_TEXTURE_2D, OpenGl.GL_TEXTURE_MAG_FILTER, (int)OpenGl.GL_LINEAR);
            OpenGl.TexParameterFloat(OpenGl.GL_TEXTURE_2D, OpenGl.GL_TEXTURE_WRAP_S, (int)OpenGl.GL_REPEAT);
            OpenGl.TexParameterFloat(OpenGl.GL_TEXTURE_2D, OpenGl.GL_TEXTURE_WRAP_T, (int)OpenGl.GL_REPEAT);

            _glTextures[assetName] = glTexture;
            TextureLoader.Unload(loadedTex);
        }

        // Возвращаем дефолтное выравнивание для корректной отрисовки шрифтов и интерфейса ImGui
        if (isGlMode) OpenGl.PixelStorei(OpenGl.GL_UNPACK_ALIGNMENT, 4);
        _texturePaths.Clear();
    }

    // Добавьте метод получения текстуры для Vulkan:
    public ITexture GetVulkanTexture(string name) => _vulkanTextures.TryGetValue(name, out var tex) ? tex : null;

    private unsafe void LoadAllAudio()
    {
        var fmodSystem = AudioManager.Instance.System;

        if (Directory.Exists(_soundsPath))
        {
            var soundFiles = Directory.GetFiles(_soundsPath, "*.ogg");
            foreach (var file in soundFiles)
            {
                string assetName = Path.GetFileNameWithoutExtension(file);
                String8 fmodPath = new String8(file);
                var sound = fmodSystem.CreateSound(fmodPath, Mode.Default | Mode._2d, ref *(CreateSoundExInfo*)null);
                _sounds[assetName] = sound;
            }
        }

        if (Directory.Exists(_musicPath))
        {
            var musicFiles = Directory.GetFiles(_musicPath, "*.ogg");
            foreach (var file in musicFiles)
            {
                string assetName = Path.GetFileNameWithoutExtension(file);
                String8 fmodPath = new String8(file);
                var stream = fmodSystem.CreateSound(fmodPath, Mode.CreateStream | Mode._2d | Mode.LoopNormal, ref *(CreateSoundExInfo*)null);
                _music[assetName] = stream;
            }
        }
    }

    public Sound GetSound(string name) => _sounds.TryGetValue(name, out var s) ? s : default;
    public Sound GetMusic(string name) => _music.TryGetValue(name, out var m) ? m : default;

    public GlTexture GetGlTexture(string name)
    {
        if (_glTextures.TryGetValue(name, out var tex)) return tex;
        Console.Error.WriteLine($"[Assets] Текстура '{name}' не найдена в видеопамяти!");
        return default;
    }

    public void Dispose()
    {
        foreach (var sound in _sounds.Values) if (!sound.IsNull()) sound.Release();
        foreach (var music in _music.Values) if (!music.IsNull()) music.Release();

        if (_glTextures.Count > 0)
        {
            OpenGl.DeleteTextures(_glTextures.Values.ToArray());
        }

        foreach (var tex in _cpuTextures.Values) TextureLoader.Unload(tex);
        foreach (var tex in _vulkanTextures.Values) TextureLoader.Unload(tex);
        _vulkanTextures.Clear();

        _sounds.Clear();
        _music.Clear();
        _glTextures.Clear();
        _cpuTextures.Clear();
    }
}
