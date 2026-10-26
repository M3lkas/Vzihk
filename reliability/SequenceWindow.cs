namespace Vzihk.Common.Reliability;

/// <summary>Bounded replay window with 16-bit serial arithmetic; one instance per UDP endpoint.</summary>
public sealed class SequenceWindow
{
    private readonly HashSet<ushort> seen = [];
    private readonly int capacity;
    private ushort newest;
    private bool initialized;
    public int Count => seen.Count;
    public SequenceWindow(int capacity = 2048)
    {
        if (capacity < 1 || capacity >= 32768) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }
    public bool TryAccept(ushort sequence)
    {
        if (!initialized) { initialized = true; newest = sequence; }
        else
        {
            var forward = (ushort)(sequence - newest);
            if (forward != 0 && forward < 32768)
            {
                newest = sequence;
                seen.RemoveWhere(s => (ushort)(newest - s) >= capacity);
            }
            else if ((ushort)(newest - sequence) >= capacity) return false;
        }
        return seen.Add(sequence);
    }
}
