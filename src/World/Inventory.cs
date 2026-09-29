namespace Mine.World;

public enum ItemKind : byte
{
    Crystal, // a small glowing crystal (ObjectKind.Crystal), placed as a world object
    Block,   // a 1 m block, placed on the building grid
}

/// <summary>Something the player can carry. Blocks carry their material and profile.</summary>
public readonly record struct Item(ItemKind Kind, BlockMaterial Material = default, ushort Profile = 0)
{
    public static readonly Item Crystal = new(ItemKind.Crystal);

    public static Item Block(BlockMaterial material) => new(ItemKind.Block, material, Profiles.Full);

    public string Name => Kind switch
    {
        ItemKind.Crystal => "cristallo",
        _ => $"{Profiles.Name(Profile)} di {Materials.Name(Material)}",
    };
}

/// <summary>
/// What the player carries: <see cref="SlotCount"/> slots, each a stack of one item (up to
/// <see cref="StackSize"/>), shown in the hotbar; the selected slot is what is in hand.
/// </summary>
public sealed class Inventory
{
    public const int SlotCount = 8;
    public const int StackSize = 64;

    private readonly Item?[] _items = new Item?[SlotCount];
    private readonly int[] _counts = new int[SlotCount];

    public int SelectedSlot { get; private set; }

    public Item? Selected => _items[SelectedSlot];

    public (Item? Item, int Count) Slot(int index) => (_items[index], _counts[index]);

    public int Count(Item item)
    {
        int total = 0;
        for (int i = 0; i < SlotCount; i++)
            if (_items[i] == item) total += _counts[i];
        return total;
    }

    /// <summary>Whether one more of the item fits: on a stack of it that is not full, or in an empty slot.</summary>
    public bool HasRoomFor(Item item)
    {
        for (int i = 0; i < SlotCount; i++)
            if (_items[i] is null || _items[i] == item && _counts[i] < StackSize) return true;
        return false;
    }

    /// <summary>
    /// Adds the items to the stacks of them already carried, then to the first empty slots.
    /// Returns how many found no room.
    /// </summary>
    public int Add(Item item, int count = 1)
    {
        for (int i = 0; i < SlotCount && count > 0; i++)
        {
            if (_items[i] != item) continue;
            int put = Math.Min(count, StackSize - _counts[i]);
            _counts[i] += put;
            count -= put;
        }
        for (int i = 0; i < SlotCount && count > 0; i++)
        {
            if (_items[i] is not null) continue;
            int put = Math.Min(count, StackSize);
            _items[i] = item;
            _counts[i] = put;
            count -= put;
        }
        return count;
    }

    /// <summary>Takes one of the item, from the slot in hand if it holds it, else from any other.</summary>
    public bool Take(Item item)
    {
        int slot = _items[SelectedSlot] == item ? SelectedSlot : Array.FindIndex(_items, i => i == item);
        if (slot < 0) return false;
        if (--_counts[slot] == 0) _items[slot] = null;
        return true;
    }

    /// <summary>Moves the hand to the next (or previous) slot.</summary>
    public void Cycle(int step) => SelectedSlot = ((SelectedSlot + step) % SlotCount + SlotCount) % SlotCount;

    public string Describe() => Selected is { } item ? $"{item.Name} x{_counts[SelectedSlot]}" : "mani vuote";
}
