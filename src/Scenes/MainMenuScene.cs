using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.OpenGlApi;
using BrutalSandbox.Framework;
using BrutalSandbox.Framework.Audio;
using BrutalSandbox.Framework.Files;
using BrutalSandbox.Framework.Localization;
using BrutalSandbox.Framework.Physics;
using BrutalSandbox.Framework.Scenes;
using System.Diagnostics;

namespace BrutalSandbox;

public sealed class MainMenuScene : IScene
{
    private readonly MainApp _app;
    private byte[] _ipBuffer = System.Text.Encoding.UTF8.GetBytes("127.0.0.1" + new string('\0', 30));
    private int _portInt = 60000;
    private string _networkError = "";

    public MainMenuScene(MainApp app) => _app = app;

    public void Initialize()
    {
        byte[] nickBytes = System.Text.Encoding.UTF8.GetBytes(_app.Network.MyNickname + new string('\0', 20));
        Array.Copy(nickBytes, _nickBuffer, Math.Min(nickBytes.Length, _nickBuffer.Length));
    }

    public void Update(float deltaTime) { }
    public void Render(IRenderer renderer, float4x4 viewProj) { }

    private byte[] _worldNameBuffer = System.Text.Encoding.UTF8.GetBytes("New World" + new string('\0', 20));
    private byte[] _nickBuffer = System.Text.Encoding.UTF8.GetBytes("Player_" + Random.Shared.Next(100, 999) + new string('\0', 20));
    private byte[] _renameWorldBuffer = new byte[20];
    private bool _showRenamePopup = false;

    private int _selectedWorldIndex = -1;

    public void RenderUi()
    {
        ImGui.SetNextWindowPos(new float2(450, 100), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(550, 550), ImGuiCond.FirstUseEver);

        if (ImGui.Begin(LocalizationKeys.MenuTitle.ToUtf8()))
        {
            if (ImGui.BeginTabBar("MenuTabs"u8))
            {
                // Вкладка 1: Миры и Одиночная игра
                if (ImGui.BeginTabItem(LocalizationKeys.MenuWorldSelectTab.ToUtf8()))
                {
                    ImGui.Spacing();
                    ImGui.Text(LocalizationKeys.MenuCreateNewWorld.ToUtf8());
                    ImGui.InputText(LocalizationKeys.MenuWorldNameLabel.ToUtf8(), _worldNameBuffer);

                    string newWorldName = System.Text.Encoding.UTF8.GetString(_worldNameBuffer).Split('\0')[0].Trim();

                    if (ImGui.Button(LocalizationKeys.MenuLaunchNewWorld.ToUtf8(), new float2(-1, 35)))
                    {
                        if (!string.IsNullOrEmpty(newWorldName))
                        {
                            _app.ChangeScene(GameState.Gameplay, newWorldName);
                        }
                    }

                    ImGui.Separator();
                    ImGui.Text(LocalizationKeys.MenuAvailableSaves.ToUtf8());

                    var worlds = WorldSerializer.GetAvailableWorlds();

                    if (ImGui.BeginChild("WorldsList"u8, new float2(-1, 150), ImGuiChildFlags.Borders))
                    {
                        if (worlds.Count == 0)
                        {
                            ImGui.Text(LocalizationKeys.MenuNoSavedWorlds.ToUtf8());
                        }
                        else
                        {
                            for (int i = 0; i < worlds.Count; i++)
                            {
                                if (ImGui.Selectable($"{worlds[i]}##world_{i}", _selectedWorldIndex == i))
                                {
                                    _selectedWorldIndex = i;
                                    byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(worlds[i] + new string('\0', 20));
                                    Array.Copy(nameBytes, _worldNameBuffer, Math.Min(nameBytes.Length, _worldNameBuffer.Length));
                                }
                            }
                        }
                    }
                    ImGui.EndChild();

                    if (_selectedWorldIndex >= 0 && _selectedWorldIndex < worlds.Count)
                    {
                        string targetWorld = worlds[_selectedWorldIndex];

                        string loadWorldFmt = string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.MenuLoadWorldBtn.ToUtf8()), targetWorld);
                        if (ImGui.Button(loadWorldFmt, new float2(-1, 35)))
                        {
                            _app.ChangeScene(GameState.Gameplay, targetWorld);
                        }

                        ImGui.Spacing();

                        if (ImGui.Button(LocalizationKeys.MenuRenameWorldBtn.ToUtf8(), new float2(260, 30)))
                        {
                            _showRenamePopup = true;
                            byte[] currentNameBytes = System.Text.Encoding.UTF8.GetBytes(targetWorld + new string('\0', 20));
                            Array.Copy(currentNameBytes, _renameWorldBuffer, Math.Min(currentNameBytes.Length, _renameWorldBuffer.Length));
                        }
                        ImGui.SameLine();
                        if (ImGui.Button(LocalizationKeys.MenuDeleteWorldBtn.ToUtf8(), new float2(260, 30)))
                        {
                            WorldSerializer.DeleteWorld(targetWorld);
                            _selectedWorldIndex = -1;
                        }
                    }

                    if (_showRenamePopup)
                    {
                        ImGui.OpenPopup(LocalizationKeys.MenuRenamePopupTitle.ToUtf8());
                        if (ImGui.BeginPopupModal(LocalizationKeys.MenuRenamePopupTitle.ToUtf8(), ref _showRenamePopup, ImGuiWindowFlags.AlwaysAutoResize))
                        {
                            ImGui.Text(LocalizationKeys.MenuEnterNewName.ToUtf8());
                            ImGui.InputText("##newName"u8, _renameWorldBuffer);

                            if (ImGui.Button(LocalizationKeys.MenuOk.ToUtf8(), new float2(120, 0)))
                            {
                                string oldName = worlds[_selectedWorldIndex];
                                string newName = System.Text.Encoding.UTF8.GetString(_renameWorldBuffer).Split('\0')[0].Trim();
                                if (!string.IsNullOrEmpty(newName) && oldName != newName)
                                {
                                    WorldSerializer.RenameWorld(oldName, newName);
                                    _selectedWorldIndex = -1;
                                }
                                _showRenamePopup = false;
                                ImGui.CloseCurrentPopup();
                            }
                            ImGui.SameLine();
                            if (ImGui.Button(LocalizationKeys.MenuCancel.ToUtf8(), new float2(120, 0)))
                            {
                                _showRenamePopup = false;
                                ImGui.CloseCurrentPopup();
                            }
                            ImGui.EndPopup();
                        }
                    }

                    ImGui.EndTabItem();
                }

                // Вкладка 2: Сетевая игра
                if (ImGui.BeginTabItem(LocalizationKeys.MenuMultiplayerTab.ToUtf8()))
                {
                    ImGui.Spacing();
                    ImGui.Text(LocalizationKeys.MenuProfileSettings.ToUtf8());

                    if (ImGui.InputText(LocalizationKeys.MenuNicknameLabel.ToUtf8(), _nickBuffer))
                    {
                        string currentNick = System.Text.Encoding.UTF8.GetString(_nickBuffer).Split('\0')[0].Trim();
                        if (!string.IsNullOrEmpty(currentNick))
                        {
                            _app.Network.MyNickname = currentNick;
                            ConfigManager.Save(_app.LocManager, AudioManager.Instance, _app.Network);
                        }
                    }

                    ImGui.Separator();
                    ImGui.Text(LocalizationKeys.MenuNetworkParams.ToUtf8());

                    ImGui.InputText(LocalizationKeys.MenuIpAddress.ToUtf8(), _ipBuffer);
                    ImGui.InputInt(LocalizationKeys.MenuPort.ToUtf8(), ref _portInt);

                    string activeWorld = System.Text.Encoding.UTF8.GetString(_worldNameBuffer).Split('\0')[0].Trim();
                    string hostInfoFmt = string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.MenuHostingWorldInfo.ToUtf8()), activeWorld);
                    ImGui.TextColored(new float4(1f, 0.7f, 0f, 1f), hostInfoFmt);

                    if (!string.IsNullOrEmpty(_networkError))
                    {
                        ImGui.TextColored(new float4(1.0f, 0.3f, 0.3f, 1.0f), _networkError);
                    }

                    ushort targetPort = (ushort)Math.Clamp(_portInt, 1024, 65535);
                    string targetIp = System.Text.Encoding.UTF8.GetString(_ipBuffer).Split('\0')[0].Trim();

                    if (ImGui.Button(LocalizationKeys.MenuHostServer.ToUtf8(), new float2(-1, 35)))
                    {
                        if (_app.Network.StartServer(targetPort))
                        {
                            _networkError = "";
                            _app.ChangeScene(GameState.Gameplay, activeWorld);
                        }
                        else
                        {
                            _networkError = string.Format(System.Text.Encoding.UTF8.GetString(LocalizationKeys.MenuHostError.ToUtf8()), targetPort);
                        }
                    }

                    if (ImGui.Button(LocalizationKeys.MenuConnectClient.ToUtf8(), new float2(-1, 35)))
                    {
                        _networkError = "";
                        _app.Network.StartClient(targetIp, targetPort);
                        _app.ChangeScene(GameState.Gameplay, "MultiplayerClientWorld");
                    }
                    ImGui.EndTabItem();
                }

                // Вкладка 3: Настройки
                if (ImGui.BeginTabItem(LocalizationKeys.MenuSettingsTab.ToUtf8()))
                {
                    ImGui.Spacing();
                    var loc = _app.LocManager;
                    ImGui.Text(LocalizationKeys.MenuLanguage.ToUtf8());
                    if (ImGui.BeginCombo("##langCombo"u8, loc.CurrentLanguage))
                    {
                        foreach (var lang in loc.AvailableLanguages)
                        {
                            if (ImGui.Selectable(lang, loc.CurrentLanguage == lang))
                            {
                                loc.SetLanguage(lang);
                                ConfigManager.Save(loc, AudioManager.Instance, _app.Network);
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
                        ConfigManager.Save(loc, audio, _app.Network);
                    }
                    ImGui.Separator();
                    if (ImGui.Button(LocalizationKeys.MenuExit.ToUtf8(), new float2(-1, 30))) _app.Window.ShouldClose = true;
                    ImGui.EndTabItem();
                }
                // Вкладка 4: Модификации
                if (ImGui.BeginTabItem(LocalizationKeys.MenuModsTab.ToUtf8()))
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new float4(0.2f, 0.8f, 1.0f, 1.0f), LocalizationKeys.MenuModsInstalled.ToUtf8());
                    ImGui.Separator();
                    var mods = ModManager.LoadedMods;
                    if (mods.Count == 0)
                    {
                        ImGui.Text(LocalizationKeys.MenuNoMods.ToUtf8());
                    }
                    else
                    {
                        foreach (var mod in mods)
                        {
                            if (ImGui.TreeNode($"{mod.Name} v{mod.Version}"))
                            {
                                ImGui.Text($"{System.Text.Encoding.UTF8.GetString(LocalizationKeys.MenuModAuthor.ToUtf8())}: {mod.Author}");
                                ImGui.Text($"{System.Text.Encoding.UTF8.GetString(LocalizationKeys.MenuModAssembly.ToUtf8())}: {mod.AssemblyName}");
                                ImGui.TreePop();
                            }
                        }
                    }
                    ImGui.EndTabItem();
                }
                // Вкладка 5: Авторы
                if (ImGui.BeginTabItem(LocalizationKeys.MenuCreditsTab.ToUtf8()))
                {
                    ImGui.Spacing();
                    ImGui.Text(LocalizationKeys.MenuFrameworkLogo.ToUtf8());
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
                        var logoTex = _app.AssetManager.GetGlTexture("brutal_logo");
                        if (logoTex.Id != 0)
                        {
                            ImGui.Image(new ImTextureRef { _TexID = (ImTextureID)(nint)logoTex.Id }, new float2(356, 84));
                        }
                    }
                    ImGui.Separator();
                    ImGui.Text(LocalizationKeys.MenuAssetsTitle.ToUtf8());
                    ImGui.Spacing();
                    ImGui.TextWrapped(LocalizationKeys.MenuAssetMusic1.ToUtf8());
                    ImGui.SameLine(380);
                    string musicBtnText = $"{LocalizationKeys.MenuOpenWebsite.Localized}##music";
                    if (ImGui.Button(musicBtnText))
                    {
                        OpenUrl("incompetech.com");
                    }
                    ImGui.Spacing();
                    ImGui.TextWrapped(LocalizationKeys.MenuAssetTextures.ToUtf8());
                    ImGui.SameLine(380);
                    string texturesBtnText = $"{LocalizationKeys.MenuOpenWebsite.Localized}##textures";
                    if (ImGui.Button(texturesBtnText))
                    {
                        OpenUrl("itch.io");
                    }
                    ImGui.TextWrapped(LocalizationKeys.MenuAssetAudioEngine.ToUtf8());
                    ImGui.SameLine(380);
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