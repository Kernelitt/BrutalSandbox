namespace BrutalSandbox.Framework.Network;

public enum GameMessageID : byte
{
    Welcome = 1,
    PlayerTransform = 2,
    PlayerDisconnected = 3,
    SyncDynamicObjects = 4,
    BuildObject = 5,     
    DestroyObject = 6     
}
