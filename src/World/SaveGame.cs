using System.Text.Json;

namespace Mine.World;

/// <summary>Everything that is saved: the inventory, and for each kind of world what the player changed in it.</summary>
public sealed class SaveData
{
    /// <summary>The inventory's slots (null: empty); null for a new game.</summary>
    public List<SlotSave?>? Slots { get; set; }
    public GameOptions Options { get; set; } = new();
    public Dictionary<string, WorldSave> Worlds { get; set; } = new();
}

/// <summary>The options switched in the menu (Esc) (null: not chosen yet, the default applies).</summary>
public sealed class GameOptions
{
    public bool? VSync { get; set; }
    public bool SaveWorld { get; set; } = true; // off: nothing of the worlds is kept, each start is a fresh world
}

/// <summary>What the player changed in one world (by <see cref="WorldPreset.Name"/>).</summary>
public sealed class WorldSave
{
    public List<BlockSave> Blocks { get; set; } = new();
    public List<DugSave> Dug { get; set; } = new();
    public List<GatheredSave> Gathered { get; set; } = new();
    public List<long> TakenObjects { get; set; } = new();
    public List<PlacedSave> PlacedObjects { get; set; } = new();
    public List<DropSave> Drops { get; set; } = new();
}

/// <summary>Something the player broke (by its exact position): it never comes back.</summary>
public readonly record struct GatheredSave(float X, float Y, float Z);

/// <summary>A cube of material lying on the ground, not picked up yet.</summary>
public readonly record struct DropSave(Resource Resource, float X, float Y, float Z);

public readonly record struct PlacedSave(ObjectKind Kind, float X, float Y, float Z, float Yaw);

/// <summary>
/// Reads and writes the save file: JSON in the user's application data folder (Mine/salvataggio.json).
/// MINE_SAVE=path uses another file, MINE_SAVE=none saves nothing.
/// </summary>
public static class SaveGame
{
    public static readonly string? FilePath = ResolvePath();

    private static string? ResolvePath()
    {
        string? custom = Environment.GetEnvironmentVariable("MINE_SAVE");
        if (custom == "none") return null;
        if (!string.IsNullOrEmpty(custom)) return custom;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mine", "salvataggio.json");
    }

    /// <summary>The saved game, or null if there is none (or it cannot be read).</summary>
    public static SaveData? Load()
    {
        try
        {
            if (FilePath is null || !File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<SaveData>(File.ReadAllText(FilePath));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Salvataggio illeggibile ({e.Message}): si riparte da capo.");
            return null;
        }
    }

    /// <summary>Writes the save (to a temporary file first, so a crash never leaves it half written).</summary>
    public static void Write(SaveData data)
    {
        if (FilePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(data));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Impossibile salvare: {e.Message}");
        }
    }
}
