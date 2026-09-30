namespace Mine.World;

/// <summary>The materials the player gathers around the world and builds with.</summary>
public enum Resource
{
    Wood,    // cubes of wood, from trees and palms
    Stone,   // cubes of stone, from rocks
    Crystal, // cubes of glowing crystal, from the veins in the rock spires
    Seeds,   // grass seeds, from tearing up meadows: sown elsewhere, where grass springs up (not a block)
    Glass,   // blocks of clear glass, from the veins in the rock spires: windows
}

/// <summary>A quantity of one material: a line of a recipe or of a harvest.</summary>
public readonly record struct Amount(Resource Resource, int Count);

/// <summary>A stack in an inventory slot, as saved.</summary>
public readonly record struct SlotSave(Resource Resource, int Count);

/// <summary>
/// What the player carries: <see cref="SlotCount"/> slots, each holding a stack of one material
/// (any size). Picked-up material joins its stack, or the first empty slot.
/// </summary>
public sealed class Inventory
{
    public static readonly string[] Names = ["Legno", "Pietra", "Cristallo", "Semi d'erba", "Vetro"];
    public const int SlotCount = 5;

    private readonly Amount?[] _slots = new Amount?[SlotCount];

    public Amount? this[int slot] => _slots[slot];

    /// <summary>How much of a material there is, over all slots.</summary>
    public int Count(Resource resource)
    {
        int total = 0;
        foreach (var slot in _slots)
            if (slot is { } s && s.Resource == resource) total += s.Count;
        return total;
    }

    /// <summary>Whether there is a stack of this material or an empty slot to put it in.</summary>
    public bool HasRoomFor(Resource resource) => _slots.Any(s => s is null || s.Value.Resource == resource);

    /// <summary>Adds to the material's stack, or to the first empty slot; false if there is no room.</summary>
    public bool Add(Resource resource, int count)
    {
        if (count <= 0) return true;
        int index = Array.FindIndex(_slots, s => s is { } a && a.Resource == resource);
        if (index < 0) index = Array.FindIndex(_slots, s => s is null);
        if (index < 0) return false;
        _slots[index] = new Amount(resource, (_slots[index]?.Count ?? 0) + count);
        return true;
    }

    /// <summary>Takes some of a material (from the last stacks first), if there is enough.</summary>
    public bool Take(Resource resource, int count)
    {
        if (Count(resource) < count) return false;
        for (int i = SlotCount - 1; i >= 0 && count > 0; i--)
        {
            if (_slots[i] is not { } s || s.Resource != resource) continue;
            int taken = Math.Min(count, s.Count);
            count -= taken;
            _slots[i] = s.Count == taken ? null : s with { Count = s.Count - taken };
        }
        return true;
    }

    public bool Has(IReadOnlyList<Amount> cost)
    {
        foreach (var a in cost)
            if (Count(a.Resource) < a.Count) return false;
        return true;
    }

    /// <summary>Takes the materials of <paramref name="cost"/>, if there are enough of all of them.</summary>
    public bool Spend(IReadOnlyList<Amount> cost)
    {
        if (!Has(cost)) return false;
        foreach (var a in cost) Take(a.Resource, a.Count);
        return true;
    }

    public void Refund(IReadOnlyList<Amount> cost)
    {
        foreach (var a in cost) Add(a.Resource, a.Count);
    }

    public List<SlotSave?> Save() => _slots.Select(s => s is { } a ? new SlotSave(a.Resource, a.Count) : (SlotSave?)null).ToList();

    public void Load(IReadOnlyList<SlotSave?> slots)
    {
        Array.Clear(_slots);
        for (int i = 0; i < Math.Min(slots.Count, SlotCount); i++)
            if (slots[i] is { Count: > 0 } s && Enum.IsDefined(s.Resource)) _slots[i] = new Amount(s.Resource, s.Count);
    }
}
