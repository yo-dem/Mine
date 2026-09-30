namespace Mine.World;

/// <summary>
/// The look of the world: how magical its light is and how rich its colours. There is one world
/// (<see cref="Current"/>), whose land is shaped by its biomes (<see cref="GroundMaterials.Biomes"/>);
/// these knobs only change how it looks. Its name keys what is saved of it.
/// </summary>
public sealed record WorldPreset(
    string Name,
    // How magical the light is: 1 = full bioluminescence and a blazing night sky; lower dims the
    // water's glow, the glitter, the galaxy, nebulae, stars, auroras and the planet's halo.
    float Magic = 1f,
    // Colour saturation of the final image (1.25 is the usual rich grade; 1 = neutral).
    float Saturation = 1.25f,
    // Clears the sky: 0 = the usual clouds, 1 = hardly any.
    float ClearSky = 0f,
    // A sober night: the planet only as a dark shadow against the stars (no lit crescent, rim,
    // halo or small moons), no auroras, no galaxy or nebulae.
    bool PlainNight = false,
    // How varied the grass colours are: 1 = patches of every hue, 0 = one muted tone.
    float ColorVariety = 1f,
    // With PlainNight: the planet's dark disk keeps a glowing rim.
    bool PlanetRim = false)
{
    /// <summary>The world: desert, prairie, hills, islands and snowy lands, blending into each other.</summary>
    public static readonly WorldPreset World = new("mondo");

    public static WorldPreset Current { get; set; } = World;

    // Island shape (see GroundMaterials.IslandLand): the coast is where the land noise crosses
    // IslandCoast; `inland` counts IslandSpan steps of it from there (0 at the coast, 1 well inland).
    public const float IslandCoast = 0.6f, IslandSpan = 0.12f;
}
