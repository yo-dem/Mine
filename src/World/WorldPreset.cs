namespace Mine.World;

/// <summary>
/// The knobs of world generation, switched at runtime with Ctrl+1..3 to compare kinds of world.
/// <see cref="Current"/> is read by the terrain, the biomes (C# and, through uniforms set with the
/// world uniforms, the shaders) and the trees; changing it means rebuilding the world (see
/// Game.SetWorld).
/// </summary>
public sealed record WorldPreset(
    string Name,
    // The desert noise range over which the desert takes over (higher = rarer deserts).
    float DesertLow, float DesertHigh,
    // The forest noise must exceed this for woods to grow (higher = rarer woods).
    float WoodsThreshold,
    // Share of lake cells that hold a lake.
    float LakeChance,
    // An archipelago: sea everywhere but islands, sandy desert shores, lush woods inland.
    bool Islands,
    // Metres the whole land is raised by (a dry world keeps water only in its deepest hollows).
    float LandLift = 0f,
    // How much of the land's relief is kept (1 = as generated, lower = flatter, and no rock spires).
    float Relief = 1f,
    // How magical the light is: 1 = full bioluminescence and a blazing night sky; lower dims the
    // water's glow, the glitter, the galaxy, nebulae, stars, auroras and the planet's halo.
    float Magic = 1f)
{
    /// <summary>The world as it was built: woods, meadows, lakes, a few deserts.</summary>
    public static readonly WorldPreset Classic = new("classico", 0.70f, 0.76f, 0.0f, 0.6f, false);

    /// <summary>Mostly desert, with rare woods and lakes.</summary>
    public static readonly WorldPreset Desert = new("deserto", 0.30f, 0.38f, 0.3f, 0.01f, false, LandLift: 9f);

    /// <summary>Islands in the sea: desert on the coasts, lush toward the middle.</summary>
    public static readonly WorldPreset Archipelago = new("arcipelago", 0.70f, 0.76f, 0.0f, 0.0f, true);

    /// <summary>A quiet, flat world: gentle plains, moderate woods, calmer water and a modest night sky.</summary>
    public static readonly WorldPreset Flat = new("piatto", 0.72f, 0.78f, 0.12f, 0.35f, false, LandLift: 1.5f, Relief: 0.3f, Magic: 0.3f);

    public static readonly WorldPreset[] All = [Classic, Desert, Archipelago, Flat];

    public static WorldPreset Current { get; set; } = Classic;

    // Archipelago shape (see GroundMaterials.IslandLand): the coast is where the land noise crosses
    // IslandCoast; `inland` counts IslandSpan steps of it from there (0 at the coast, 1 well inland).
    public const float IslandCoast = 0.6f, IslandSpan = 0.12f;
}
