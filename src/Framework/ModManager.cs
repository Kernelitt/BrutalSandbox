using System.Reflection;
using System.Text.Json;

namespace BrutalSandbox.Framework
{
    public class ModManifest
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Author { get; set; } = "";
        public string AssemblyName { get; set; } = "";
    }

        public static class ModManager
        {
            private static readonly Dictionary<string, string> ModDirectories = [];
            public static List<ModManifest> LoadedMods { get; } = [];

            // События жизненного цикла
            public static event Action<float> OnUpdate;
            public static event Action OnRender;
            public static event Action<int, int> OnResize;

            // Триггеры, которые будет вызывать игра
            public static void TriggerUpdate(float dt) => OnUpdate?.Invoke(dt);
            public static void TriggerRender() => OnRender?.Invoke();
            public static void TriggerResize(int width, int height) => OnResize?.Invoke(width, height);

        public static void InitAndLoadMods(string modsRootPath, MainApp gameWindow)
        {
            if (!Directory.Exists(modsRootPath))
                Directory.CreateDirectory(modsRootPath);

            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            // Сохраняем или передаем контекст окна в моды, если нужно. 
            // Но проще передавать его через рефлексию/параметр в метод Init мода.

            string[] modFolders = Directory.GetDirectories(modsRootPath);
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            foreach (string modFolder in modFolders)
            {
                string manifestPath = Path.Combine(modFolder, "mod.manifest");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    string json = File.ReadAllText(manifestPath);
                    ModManifest manifest = JsonSerializer.Deserialize<ModManifest>(json, jsonOptions);
                    if (manifest == null) continue;

                    string mainDllPath = Path.Combine(modFolder, manifest.AssemblyName);
                    if (!File.Exists(mainDllPath)) continue;

                    string assemblyNameWithoutExt = Path.GetFileNameWithoutExtension(manifest.AssemblyName);
                    ModDirectories[assemblyNameWithoutExt] = modFolder;

                    Assembly asm = Assembly.LoadFrom(mainDllPath);

                    bool initFound = false;
                    foreach (Type type in asm.GetTypes())
                    {
                        // ИСПРАВЛЕНО: Теперь ищем Init(GameWindow window) вместо Init() без параметров
                        MethodInfo initMethod = type.GetMethod("Init", BindingFlags.Public | BindingFlags.Static, null, [typeof(MainApp)], null);

                        if (initMethod != null)
                        {
                            // Передаем экземпляр игры прямо в мод!
                            initMethod.Invoke(null, [gameWindow]);
                            initFound = true;
                            Console.WriteLine($"[Mods] Мод '{manifest.Name}' v{manifest.Version} успешно загружен.");
                            LoadedMods.Add(manifest);
                            break;
                        }
                    }

                    if (!initFound)
                    {
                        Console.WriteLine($"[Warning] Мод '{manifest.Name}' не смог инициализироваться. Проверьте сигнатуру static void Init(MainApp window).");
                    }
                }
                catch (Exception ex)
                {
                    // Если ошибка произошла внутри вызванного метода, выводим InnerException
                    if (ex is TargetInvocationException && ex.InnerException != null)
                    {
                        Console.WriteLine($"[Error] КРИТИЧЕСКАЯ ОШИБКА ВНУТРИ МОДА : {ex.InnerException.Message}");
                        Console.WriteLine($"[Stack Trace]: {ex.InnerException.StackTrace}");
                    }
                    else
                    {
                        Console.WriteLine($"[Error] Ошибка при загрузке мода из {modFolder}: {ex.Message}");
                    }
                }
            }
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var requestingName = new AssemblyName(args.Name);
            string shortName = requestingName.Name;

            // 1. Ищем в папке того мода, который запросил зависимость
            Assembly requestingAssembly = args.RequestingAssembly;
            string modFolder = null;

            if (requestingAssembly != null)
            {
                ModDirectories.TryGetValue(requestingAssembly.GetName().Name, out modFolder);
            }

            if (!string.IsNullOrEmpty(modFolder))
            {
                string dependencyPath = Path.Combine(modFolder, shortName + ".dll");
                if (File.Exists(dependencyPath)) return Assembly.LoadFrom(dependencyPath);
            }

            // 2. ДОБАВЛЕНО: Ищем в корневой папке самой игры (AppDomain.CurrentDomain.BaseDirectory)
            // Это позволит модам мгновенно подхватывать OpenTK, KrutolFramework и саму PVZRemake.dll
            string gameRootPath = AppDomain.CurrentDomain.BaseDirectory;
            string gameDependencyPath = Path.Combine(gameRootPath, shortName + ".dll");

            if (File.Exists(gameDependencyPath))
            {
                return Assembly.LoadFrom(gameDependencyPath);
            }

            // 3. Если не нашли в корне игры, проверяем папки других модов (на случай общих библиотек между модами)
            foreach (string folder in ModDirectories.Values)
            {
                string dependencyPath = Path.Combine(folder, shortName + ".dll");
                if (File.Exists(dependencyPath)) return Assembly.LoadFrom(dependencyPath);
            }

            return null;
        }

    }
}

