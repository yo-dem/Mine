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
    float Magic = 1f,
    // Shares of the usual grass blades and trees.
    float GrassDensity = 1f, float TreeDensity = 1f,
    // Colour saturation of the final image (1.25 is the usual rich grade; 1 = neutral).
    float Saturation = 1.25f,
    // Clears the sky: 0 = the usual clouds, 1 = hardly any.
    float ClearSky = 0f,
    // A sober night: the planet only as a dark shadow against the stars (no lit crescent, rim,
    // halo or small moons), no auroras, no galaxy or nebulae.
    bool PlainNight = false,
    // How varied the grass colours are: 1 = patches of every hue, 0 = one muted tone.
    float ColorVariety = 1f,
    // Share of the usual lone trees scattered outside the woods (low: vegetation grows in groups).
    float LoneTrees = 1f,
    // With PlainNight: the planet's dark disk keeps a glowing rim.
    bool PlanetRim = false)
{
    /// <summary>The world as it was built: woods, meadows, lakes, a few deserts.</summary>
    public static readonly WorldPreset Classic = new("classico", 0.70f, 0.76f, 0.0f, 0.6f, false);

    /// <summary>Mostly desert, with rare woods and lakes.</summary>
    public static readonly WorldPreset Desert = new("deserto", 0.30f, 0.38f, 0.3f, 0.01f, false, LandLift: 9f);

    /// <summary>Islands in the sea: desert on the coasts, lush toward the middle.</summary>
    public static readonly WorldPreset Archipelago = new("arcipelago", 0.70f, 0.76f, 0.0f, 0.0f, true);

    /// <summary>A quiet, flat world: gentle plains, moderate woods, calmer water and a modest night sky.</summary>
    public static readonly WorldPreset Flat = new("piatto", 0.72f, 0.78f, 0.25f, 0.35f, false, LandLift: 1.5f, Relief: 0.3f, Magic: 0.3f,
        GrassDensity: 0.35f, TreeDensity: 0.35f, Saturation: 0.8f, ClearSky: 0.7f, PlainNight: true, ColorVariety: 0.25f);

    /// <summary>
    /// The base world, built from the flat one: gentle plains, readable biomes, vegetation in groves
    /// of one kind with hardly a lone tree, a sober night where the giant planet shows as a dark
    /// disk with a glowing rim.
    /// </summary>
    public static readonly WorldPreset Base = new("base", 0.72f, 0.78f, 0.15f, 0.35f, false, LandLift: 1.5f, Relief: 0.3f, Magic: 0.35f,
        GrassDensity: 0.5f, TreeDensity: 0.75f, Saturation: 0.95f, ClearSky: 0.6f, PlainNight: true, ColorVariety: 0.6f,
        LoneTrees: 0.08f, PlanetRim: true);

    public static readonly WorldPreset[] All = [Classic, Desert, Archipelago, Flat, Base];

    /// <summary>The desert world (Ctrl+2) is the main one: the game starts there.</summary>
    public static WorldPreset Current { get; set; } = Desert;

    // Archipelago shape (see GroundMaterials.IslandLand): the coast is where the land noise crosses
    // IslandCoast; `inland` counts IslandSpan steps of it from there (0 at the coast, 1 well inland).
    public const float IslandCoast = 0.6f, IslandSpan = 0.12f;
}
