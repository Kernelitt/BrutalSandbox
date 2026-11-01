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

    // Моды и расширенный UI блоков
    public static readonly LString MenuModsTab = LStrings.Create("Menu.ModsTab", "Mods");
    public static readonly LString MenuNoMods = LStrings.Create("Menu.NoMods", "No mods loaded.");
    public static readonly LString GameplayPlayerPanelTitle = LStrings.Create("Gameplay.PlayerPanelTitle", "Player Panel");
    public static readonly LString BlockLibraryTitle = LStrings.Create("Block.LibraryTitle", "Block Library (Press B to close)");
    public static readonly LString BlockLibraryChoosePrompt = LStrings.Create("Block.ChoosePrompt", "Select an item for your Hotbar:");
    public static readonly LString BlockInfoTitle = LStrings.Create("Block.InfoTitle", "Block Info");
    public static readonly LString BlockInfoGeometry = LStrings.Create("Block.InfoGeometry", "Geometry: {0}");
    public static readonly LString BlockInfoMass = LStrings.Create("Block.InfoMass", "Mass: {0} kg");
    public static readonly LString BlockInfoFriction = LStrings.Create("Block.InfoFriction", "Friction: {0}");
    public static readonly LString BlockInfoType = LStrings.Create("Block.InfoType", "Type: {0}");

    public static readonly LString MenuWorldSelectTab = LStrings.Create("Menu.WorldSelectTab", "World Selection");
    public static readonly LString MenuCreateNewWorld = LStrings.Create("Menu.CreateNewWorld", "Create New World:");
    public static readonly LString MenuWorldNameLabel = LStrings.Create("Menu.WorldNameLabel", "World Name##new");
    public static readonly LString MenuLaunchNewWorld = LStrings.Create("Menu.LaunchNewWorld", "Launch New World");
    public static readonly LString MenuAvailableSaves = LStrings.Create("Menu.AvailableSaves", "Available Saves:");
    public static readonly LString MenuNoSavedWorlds = LStrings.Create("Menu.NoSavedWorlds", "No saved worlds.");
    public static readonly LString MenuLoadWorldBtn = LStrings.Create("Menu.LoadWorldBtn", "Load World: {0}");
    public static readonly LString MenuRenameWorldBtn = LStrings.Create("Menu.RenameWorldBtn", "Rename World");
    public static readonly LString MenuDeleteWorldBtn = LStrings.Create("Menu.DeleteWorldBtn", "Delete World");
    public static readonly LString MenuRenamePopupTitle = LStrings.Create("Menu.RenamePopupTitle", "Rename WorldPopup");
    public static readonly LString MenuEnterNewName = LStrings.Create("Menu.EnterNewName", "Enter new world name:");
    public static readonly LString MenuOk = LStrings.Create("Menu.Ok", "OK");
    public static readonly LString MenuCancel = LStrings.Create("Menu.Cancel", "Cancel");
    public static readonly LString MenuMultiplayerTab = LStrings.Create("Menu.MultiplayerTab", "Multiplayer");
    public static readonly LString MenuProfileSettings = LStrings.Create("Menu.ProfileSettings", "Profile Settings:");
    public static readonly LString MenuNicknameLabel = LStrings.Create("Menu.NicknameLabel", "Your Nickname##profile");
    public static readonly LString MenuHostingWorldInfo = LStrings.Create("Menu.HostingWorldInfo", "Selected world for hosting: {0}");
    public static readonly LString MenuSettingsTab = LStrings.Create("Menu.SettingsTab", "Settings");

    public static readonly LString GameplaySaveWorldBtn = LStrings.Create("Gameplay.SaveWorldBtn", "Save World");
    public static readonly LString GameplayWorldLabel = LStrings.Create("Gameplay.WorldLabel", "World: {0}");
    public static readonly LString ChatSystemNoRights = LStrings.Create("Chat.SystemNoRights", "[System] You do not have permissions to execute commands (you are a client).");
    public static readonly LString ChatServerKicked = LStrings.Create("Chat.ServerKicked", "[Server] Player {0} has been kicked.");
    public static readonly LString ChatServerBannedNick = LStrings.Create("Chat.ServerBannedNick", "[Server] Player {0} has been banned by nickname.");
    public static readonly LString ChatServerBannedIp = LStrings.Create("Chat.ServerBannedIp", "[Server] IP {0} added to ban list.");
    public static readonly LString ChatServerClosing = LStrings.Create("Chat.ServerClosing", "[Server] Closing server and disconnecting players...");
    public static readonly LString ChatSystemUnknownCmd = LStrings.Create("Chat.SystemUnknownCmd", "[System] Unknown command: {0}");

    public static readonly LString MenuModsInstalled = LStrings.Create("Menu.ModsInstalled", "Installed Modifications:");
    public static readonly LString MenuModAuthor = LStrings.Create("Menu.ModAuthor", "Author");
    public static readonly LString MenuModAssembly = LStrings.Create("Menu.ModAssembly", "Assembly");
    public static readonly LString MenuAssetAudioEngine = LStrings.Create("Menu.AssetAudioEngine", "Audio engine built using FMOD Engine by Firelight Technologies Pty Ltd.");
}
