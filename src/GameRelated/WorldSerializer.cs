using Brutal.JoltApi;
using Brutal.Numerics;
using BrutalSandbox.Framework.Physics;
using System;
using System.Collections.Generic;
using System.IO;

namespace BrutalSandbox.Framework.Files;

public static class WorldSerializer
{
    private const uint FormatVersion = 2; // Версия обновлена для поддержки скоростей и игроков

    public static void SaveWorld(string worldName, Dictionary<uint, uint> cubeBlockIds, Dictionary<uint, uint> networkedCubes, Dictionary<uint, GameplayScene.WeldLink> weldLinks, List<SavedPlayerData> players)
    {
        string savesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saves");
        if (!Directory.Exists(savesDir)) Directory.CreateDirectory(savesDir);

        string filePath = Path.Combine(savesDir, $"{worldName}.bsm");

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        // Header
        writer.Write(FormatVersion);

        // 1. Блоки
        writer.Write(networkedCubes.Count);
        var phys = PhysicsManager.Instance;

        foreach (var kvp in networkedCubes)
        {
            uint netId = kvp.Key;
            uint bodyId = kvp.Value;

            phys.BodyInterface.GetPositionAndRotation(bodyId, out float3 pos, out floatQuat rot);
            float3 linVel = phys.BodyInterface.GetLinearVelocity(bodyId);
            float3 angVel = phys.BodyInterface.GetAngularVelocity(bodyId);
            bool isStatic = phys.BodyInterface.GetMotionType(bodyId) == MotionType.Static;

            if (!cubeBlockIds.TryGetValue(netId, out uint blockId)) blockId = 1;

            writer.Write(netId);
            writer.Write(blockId);
            writer.Write(isStatic);

            // Трансформ куба
            writer.Write(pos.X); writer.Write(pos.Y); writer.Write(pos.Z);
            writer.Write(rot.X); writer.Write(rot.Y); writer.Write(rot.Z); writer.Write(rot.W);

            // Скорости куба
            writer.Write(linVel.X); writer.Write(linVel.Y); writer.Write(linVel.Z);
            writer.Write(angVel.X); writer.Write(angVel.Y); writer.Write(angVel.Z);

            // Weld Link данные
            bool hasWeld = weldLinks.TryGetValue(netId, out var link);
            writer.Write(hasWeld);
            if (hasWeld)
            {
                writer.Write(link.ParentNetId);
                writer.Write(link.LocalOffset.X); writer.Write(link.LocalOffset.Y); writer.Write(link.LocalOffset.Z);
                writer.Write(link.LocalRotation.X); writer.Write(link.LocalRotation.Y); writer.Write(link.LocalRotation.Z); writer.Write(link.LocalRotation.W);
            }
        }

        // 2. Игроки
        writer.Write(players.Count);
        foreach (var p in players)
        {
            writer.Write(p.Nickname);
            writer.Write(p.Position.X); writer.Write(p.Position.Y); writer.Write(p.Position.Z);
            writer.Write(p.LinearVelocity.X); writer.Write(p.LinearVelocity.Y); writer.Write(p.LinearVelocity.Z);
        }
    }

    public static bool LoadWorld(string worldName, out List<SavedBlockData> loadedBlocks, out List<SavedPlayerData> loadedPlayers)
    {
        loadedBlocks = new List<SavedBlockData>();
        loadedPlayers = new List<SavedPlayerData>();

        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saves", $"{worldName}.bsm");
        if (!File.Exists(filePath)) return false;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            uint version = reader.ReadUInt32();
            if (version < 1 || version > FormatVersion) return false;

            // Чтение блоков
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var block = new SavedBlockData();
                block.NetId = reader.ReadUInt32();
                block.BlockId = reader.ReadUInt32();
                block.IsStatic = reader.ReadBoolean();

                block.Position = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                block.Rotation = new floatQuat(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                if (version >= 2)
                {
                    block.LinearVelocity = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    block.AngularVelocity = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                }

                if (reader.ReadBoolean()) // hasWeld
                {
                    block.HasWeld = true;
                    block.ParentNetId = reader.ReadUInt32();
                    block.LocalOffset = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    block.LocalRotation = new floatQuat(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                }

                loadedBlocks.Add(block);
            }

            // Чтение игроков (только для версии 2+)
            if (version >= 2)
            {
                int playerCount = reader.ReadInt32();
                for (int i = 0; i < playerCount; i++)
                {
                    var p = new SavedPlayerData
                    {
                        Nickname = reader.ReadString(),
                        Position = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        LinearVelocity = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
                    };
                    loadedPlayers.Add(p);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
    public static bool DeleteWorld(string worldName)
    {
        try
        {
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saves", $"{worldName}.bsm");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Console.WriteLine($"[WorldSerializer] Мир {worldName} успешно удален.");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WorldSerializerError] Не удалось удалить мир {worldName}: {ex.Message}");
            return false;
        }
    }

    public static bool RenameWorld(string oldWorldName, string newWorldName)
    {
        try
        {
            string savesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saves");
            string oldPath = Path.Combine(savesDir, $"{oldWorldName}.bsm");
            string newPath = Path.Combine(savesDir, $"{newWorldName}.bsm");

            if (File.Exists(oldPath) && !File.Exists(newPath))
            {
                File.Move(oldPath, newPath);
                Console.WriteLine($"[WorldSerializer] Мир {oldWorldName} успешно переименован в {newWorldName}.");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WorldSerializerError] Не удалось переименовать мир {oldWorldName}: {ex.Message}");
            return false;
        }
    }
    public static List<string> GetAvailableWorlds()
    {
        string savesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "saves");
        var list = new List<string>();
        if (!Directory.Exists(savesDir)) return list;

        foreach (var file in Directory.GetFiles(savesDir, "*.bsm"))
        {
            list.Add(Path.GetFileNameWithoutExtension(file));
        }
        return list;
    }
}

public struct SavedBlockData
{
    public uint NetId;
    public uint BlockId;
    public bool IsStatic;
    public float3 Position;
    public floatQuat Rotation;
    public float3 LinearVelocity;
    public float3 AngularVelocity;
    public bool HasWeld;
    public uint ParentNetId;
    public float3 LocalOffset;
    public floatQuat LocalRotation;
}

public struct SavedPlayerData
{
    public string Nickname;
    public float3 Position;
    public float3 LinearVelocity;
}
