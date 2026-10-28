using Brutal.Localization;

namespace BrutalSandbox.Framework.Localization;

public static class LocalizationKeys
{
    // Меню
    public static readonly LString MenuTitle = LStrings.Create("Menu.Title", "Main Menu");
    public static readonly LString MenuStartGame = LStrings.Create("Menu.StartGame", "START GAME");
    public static readonly LString MenuNetworkParams = LStrings.Create("Menu.NetworkParams", "Network Settings:");
    public static readonly LString MenuIpAddress = LStrings.Create("Menu.IpAddress", "IP Address");
    public static readonly LString MenuPort = LStrings.Create("Menu.Port", "Port");
    public static readonly LString MenuHostServer = LStrings.Create("Menu.HostServer", "HOST");
    public static readonly LString MenuConnectClient = LStrings.Create("Menu.ConnectClient", "CONNECT");
    public static readonly LString MenuSystemSettings = LStrings.Create("Menu.SystemSettings", "System Settings:");
    public static readonly LString MenuLanguage = LStrings.Create("Menu.Language", "Language:");
    public static readonly LString MenuMasterVolume = LStrings.Create("Menu.MasterVolume", "Master Volume");
    public static readonly LString MenuMusicVolume = LStrings.Create("Menu.MusicVolume", "Music");
    public static readonly LString MenuSfxVolume = LStrings.Create("Menu.SfxVolume", "SFX");
    public static readonly LString MenuExit = LStrings.Create("Menu.Exit", "Exit Game");
    public static readonly LString MenuHostError = LStrings.Create("Menu.HostError", "Port {0} is busy!");

    // Авторы / Ассеты
    public static readonly LString MenuCreditsTab = LStrings.Create("Menu.CreditsTab", "Credits");
    public static readonly LString MenuFrameworkLogo = LStrings.Create("Menu.FrameworkLogo", "Game framework based on:");
    public static readonly LString MenuAssetsTitle = LStrings.Create("Menu.AssetsTitle", "Used Assets:");
    public static readonly LString MenuAssetMusic1 = LStrings.Create("Menu.AssetMusic1", "Music: Chipper Doodle & Investigations by Kevin MacLeod");
    public static readonly LString MenuAssetTextures = LStrings.Create("Menu.AssetTextures", "Textures: Tiny Texture Pack 1 by Screaming Brain Studios");
    public static readonly LString MenuOpenWebsite = LStrings.Create("Menu.OpenWebsite", "Open Website");

    // Геймплей
    public static readonly LString GameplayBackToMenu = LStrings.Create("Gameplay.BackToMenu", "To Main Menu");
    public static readonly LString GameplayRenderedChunks = LStrings.Create("Gameplay.RenderedChunks", "Chunks: {0}");
    public static readonly LString GameplayPosition = LStrings.Create("Gameplay.Position", "Pos: {0}, {1}, {2}");
    public static readonly LString GameplayFlightMode = LStrings.Create("Gameplay.FlightMode", "Flight: {0}");
    public static readonly LString GameplayBuildSettings = LStrings.Create("Gameplay.BuildSettings", "Construction:");
    public static readonly LString GameplayStaticObject = LStrings.Create("Gameplay.StaticObject", "Static Object");
    public static readonly LString GameplayWeldObject = LStrings.Create("Gameplay.WeldObject", "Weld to Base");
    public static readonly LString GameplayObjectMass = LStrings.Create("Gameplay.ObjectMass", "Mass");
    public static readonly LString GameplayFrictionCoeff = LStrings.Create("Gameplay.FrictionCoeff", "Friction");
    public static readonly LString GameplayOn = LStrings.Create("Gameplay.On", "ON");
    public static readonly LString GameplayOff = LStrings.Create("Gameplay.Off", "OFF");
}
