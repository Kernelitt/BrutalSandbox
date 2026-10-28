using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using System.Diagnostics;
using BrutalSandbox.Framework.Audio;
using BrutalSandbox.Framework.Files;
using BrutalSandbox.Framework.Localization;
using BrutalSandbox.Framework.Scenes;

namespace BrutalSandbox;

public sealed class MainMenuScene : IScene
{
    private readonly MainApp _app;
    private byte[] _ipBuffer = System.Text.Encoding.UTF8.GetBytes("127.0.0.1" + new string('\0', 30));
    private int _portInt = 60000;
    private string _networkError = "";

    public MainMenuScene(MainApp app) => _app = app;

    public void Initialize() { }
    public void Update(float deltaTime) { }
    public void Render(IRenderer renderer, float4x4 viewProj) { }

    public void RenderUi()
    {
        ImGui.SetNextWindowPos(new float2(450, 150), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(500, 480), ImGuiCond.FirstUseEver);

        // Локализованный заголовок окна
        if (ImGui.Begin(LocalizationKeys.MenuTitle.ToUtf8()))
        {
            if (ImGui.BeginTabBar("MenuTabs"u8))
            {
                // Вкладка 1: Игра и Настройки
                if (ImGui.BeginTabItem(LocalizationKeys.MenuTitle.ToUtf8()))
                {
                    ImGui.Spacing();
                    if (ImGui.Button(LocalizationKeys.MenuStartGame.ToUtf8(), new float2(-1, 40)))
                    {
                        _app.ChangeScene(GameState.Gameplay);
                    }

                    ImGui.Separator();
                    ImGui.Text(LocalizationKeys.MenuNetworkParams.ToUtf8());

                    ImGui.InputText(LocalizationKeys.MenuIpAddress.ToUtf8(), _ipBuffer);
                    ImGui.InputInt(LocalizationKeys.MenuPort.ToUtf8(), ref _portInt);

                    if (!string.IsNullOrEmpty(_networkError))
                    {
                        ImGui.TextColored(new float4(1.0f, 0.3f, 0.3f, 1.0f), _networkError);
                    }

                    ushort targetPort = (ushort)Math.Clamp(_portInt, 1024, 65535);
                    string targetIp = System.Text.Encoding.UTF8.GetString(_ipBuffer).Split('\0')[0].Trim();

                    if (ImGui.Button(LocalizationKeys.MenuHostServer.ToUtf8(), new float2(-1, 30)))
                    {
                        if (_app.Network.StartServer(targetPort))
                        {
                            _networkError = "";
                            _app.ChangeScene(GameState.Gameplay);
                        }
                        else
                        {
                            _networkError = string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.MenuHostError.ToUtf8()), targetPort);
                        }
                    }

                    if (ImGui.Button(LocalizationKeys.MenuConnectClient.ToUtf8(), new float2(-1, 30)))
                    {
                        _networkError = "";
                        _app.Network.StartClient(targetIp, targetPort);
                        _app.ChangeScene(GameState.Gameplay);
                    }

                    ImGui.Separator();
                    ImGui.Text(LocalizationKeys.MenuSystemSettings.ToUtf8());

                    var loc = _app.LocManager;
                    ImGui.Text(LocalizationKeys.MenuLanguage.ToUtf8());
                    if (ImGui.BeginCombo("##langCombo"u8, loc.CurrentLanguage))
                    {
                        foreach (var lang in loc.AvailableLanguages)
                        {
                            if (ImGui.Selectable(lang, loc.CurrentLanguage == lang))
                            {
                                loc.SetLanguage(lang);
                                ConfigManager.Save(loc, AudioManager.Instance);
                            }
                        }
                        ImGui.EndCombo();
                    }

                    var audio = AudioManager.Instance;
                    bool volChanged = false;
                    if (ImGui.SliderFloat(LocalizationKeys.MenuMasterVolume.ToUtf8(), ref audio.MasterVolume, 0f, 1f)) volChanged = true;
                    if (ImGui.SliderFloat(LocalizationKeys.MenuMusicVolume.ToUtf8(), ref audio.MusicVolume, 0f, 1f)) volChanged = true;
                    if (ImGui.SliderFloat(LocalizationKeys.MenuSfxVolume.ToUtf8(), ref audio.SfxVolume, 0f, 1f)) volChanged = true;

                    if (volChanged)
                    {
                        audio.ApplyVolumes();
                        ConfigManager.Save(loc, audio);
                    }

                    ImGui.Separator();
                    if (ImGui.Button(LocalizationKeys.MenuExit.ToUtf8(), new float2(-1, 30))) _app.Window.ShouldClose = true;

                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(LocalizationKeys.MenuCreditsTab.ToUtf8()))
                {
                    ImGui.Spacing();
                    ImGui.Text(LocalizationKeys.MenuFrameworkLogo.ToUtf8());

                    // Проверяем, какой бэкенд сейчас активен в приложении
                    if (_app.AssetManager.GetVulkanTexture("brutal_logo") != null)
                    {
                        if (_app.Renderer is VulkanRenderer vkRenderer)
                        {
                            if (vkRenderer._vulkanAllocatedTextures.TryGetValue("brutal_logo", out var allocatedVk))
                            {
                                ImTextureRef vkLogoRef = new ImTextureRef { _TexID = (ImTextureID)(nint)allocatedVk.Set.VkHandle };
                                if (vkLogoRef._TexID.Value != 0)
                                {
                                    ImGui.Image(vkLogoRef, new float2(356, 84));
                                }
                            }
                        }
                    }
                    else
                    {
                        // === РЕЖИМ OPENGL ===
                        var logoTex = _app.AssetManager.GetGlTexture("brutal_logo");
                        if (logoTex.Id != 0)
                        {
                            ImGui.Image(new ImTextureRef { _TexID = (ImTextureID)(nint)logoTex.Id }, new float2(356, 84));
                        }
                    }
  
                    ImGui.Separator();
                    ImGui.Text(LocalizationKeys.MenuAssetsTitle.ToUtf8());
                    ImGui.Spacing();

                    // 1. Музыкальный ассет
                    ImGui.TextWrapped(LocalizationKeys.MenuAssetMusic1.ToUtf8());
                    ImGui.SameLine(380);

                    // Добавляем к тексту локализации уникальный маркер ##music с помощью интерполяции и u8-литерала
                    string musicBtnText = $"{LocalizationKeys.MenuOpenWebsite.Localized}##music";
                    if (ImGui.Button(musicBtnText))
                    {
                        OpenUrl("https://incompetech.com/music/royalty-free/music.html");
                    }

                    ImGui.Spacing();

                    // 2. Ассет текстур
                    ImGui.TextWrapped(LocalizationKeys.MenuAssetTextures.ToUtf8());
                    ImGui.SameLine(380);

                    // Добавляем к тексту локализации уникальный маркер ##textures
                    string texturesBtnText = $"{LocalizationKeys.MenuOpenWebsite.Localized}##textures";
                    if (ImGui.Button(texturesBtnText))
                    {
                        OpenUrl("https://screamingbrainstudios.itch.io/tiny-texture-pack");
                    }

                    ImGui.EndTabItem();
                }
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Не удалось открыть ссылку: {ex.Message}");
        }
    }

    public void HandleInput(GlfwWindow window, double2 cursorDelta, bool shiftHeld, bool dragging) { }
    public void Dispose() { }
}
