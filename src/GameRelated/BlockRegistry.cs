using Brutal.Numerics;
using System.Collections.Generic;

namespace BrutalSandbox;

public enum BlockShape
{
    Cube,
    Capsule,
    Platform
}

public struct BlockData
{
    public uint Id;
    public string Name;
    public string TextureName; // Ключ для AssetManager (например, "stone_wall", "grass_tile")
    public BlockShape Shape;
    public float DefaultMass;
    public float DefaultFriction;
    public bool IsStatic;
}

public static class BlockRegistry
{
    public static List<BlockData> RegisteredBlocks { get; } = new();
    private static readonly Dictionary<uint, BlockData> _blocksById = new();

    static BlockRegistry()
    {
        // 1. Каменный куб
        Register(new BlockData
        {
            Id = 1,
            Name = "Stone Bricks",
            TextureName = "stone_wall",
            Shape = BlockShape.Cube,
            DefaultMass = 20.0f,
            DefaultFriction = 0.3f,
            IsStatic = false
        });

        // 2. Земляной блок
        Register(new BlockData
        {
            Id = 2,
            Name = "Dirt Cube",
            TextureName = "grass_tile",
            Shape = BlockShape.Cube,
            DefaultMass = 7.0f,
            DefaultFriction = 0.6f,
            IsStatic = false
        });

        // 3. Статическая платформа
        Register(new BlockData
        {
            Id = 3,
            Name = "Stone Platform",
            TextureName = "stone_wall",
            Shape = BlockShape.Platform,
            DefaultMass = 40f,
            DefaultFriction = 0.5f,
            IsStatic = true
        });

        // 4. Капсула-колонна
        Register(new BlockData
        {
            Id = 4,
            Name = "Stone Capsule",
            TextureName = "stone_wall",
            Shape = BlockShape.Capsule,
            DefaultMass = 10.0f,
            DefaultFriction = 0.2f,
            IsStatic = false
        });

        Register(new BlockData
        {
            Id = 5,
            Name = "Wood Block",
            TextureName = "wood_tile",
            Shape = BlockShape.Cube,
            DefaultMass = 4.0f,
            DefaultFriction = 0.4f,
            IsStatic = false
        });

        Register(new BlockData
        {
            Id = 6,
            Name = "Planks Block",
            TextureName = "planks_tile",
            Shape = BlockShape.Cube,
            DefaultMass = 5.0f,
            DefaultFriction = 0.5f,
            IsStatic = false
        });

        Register(new BlockData
        {
            Id = 7,
            Name = "Planks Platform",
            TextureName = "planks_tile",
            Shape = BlockShape.Platform,
            DefaultMass = 9.0f,
            DefaultFriction = 0.6f,
            IsStatic = false
        });

        Register(new BlockData
        {
            Id = 8,
            Name = "Ice Block",
            TextureName = "ice_tile",
            Shape = BlockShape.Cube,
            DefaultMass = 5.0f,
            DefaultFriction = 0.02f,
            IsStatic = false
        });

        Register(new BlockData
        {
            Id = 9,
            Name = "Ice Platform",
            TextureName = "ice_tile",
            Shape = BlockShape.Platform,
            DefaultMass = 9.0f,
            DefaultFriction = 0.04f,
            IsStatic = false
        });
    }

    private static void Register(BlockData block)
    {
        RegisteredBlocks.Add(block);
        _blocksById[block.Id] = block;
    }

    public static BlockData GetById(uint id)
    {
        if (_blocksById.TryGetValue(id, out var block)) return block;
        return RegisteredBlocks[0]; // Дефолтный блок
    }
}
