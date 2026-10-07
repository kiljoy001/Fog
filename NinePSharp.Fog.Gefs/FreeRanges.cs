using System.Collections;

namespace NinePSharp.Fog.Gefs;

// An arena's free space as disjoint ranges, merged with their neighbours: blk.c's freerange and
// grabrange over its AVL tree.
internal sealed class FreeRanges : IEnumerable<(long Offset, long Length)>
{
    private readonly SortedSet<(long Offset, long Length)> ranges = new(Comparer<(long Offset, long Length)>.Create((a, b) => a.Offset.CompareTo(b.Offset)));

    public int Count => ranges.Count;

    public void Free(long offset, long length)
    {
        long end = offset + length;
        var before = Before(offset);
        var after = After(offset);
        if ((before is { } b && b.Offset + b.Length > offset) || (after is { } a && a.Offset < end))
        {
            throw new GefsException("block freed twice");
        }

        if (before is { } left && left.Offset + left.Length == offset)
        {
            ranges.Remove(left);
            offset = left.Offset;
        }

        if (after is { } right && right.Offset == end)
        {
            ranges.Remove(right);
            end = right.Offset + right.Length;
        }

        ranges.Add((offset, end - offset));
    }

    public void Take(long offset, long length)
    {
        long end = offset + length;
        if (Before(offset) is not { } r || end > r.Offset + r.Length)
        {
            throw new GefsException("block not free");
        }

        ranges.Remove(r);
        if (offset > r.Offset)
        {
            ranges.Add((r.Offset, offset - r.Offset));
        }

        if (end < r.Offset + r.Length)
        {
            ranges.Add((end, r.Offset + r.Length - end));
        }
    }

    // blkalloc_lk: a block from the top of the last range, or from the bottom of the first.
    public long? TakeHighest() => Take(ranges.Count == 0 ? null : ranges.Max, top: true);

    public long? TakeLowest() => Take(ranges.Count == 0 ? null : ranges.Min, top: false);

    public IEnumerator<(long Offset, long Length)> GetEnumerator() => ranges.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private long? Take((long Offset, long Length)? range, bool top)
    {
        if (range is not { } r)
        {
            return null;
        }

        long block = top ? r.Offset + r.Length - Format.BlockSize : r.Offset;
        Take(block, Format.BlockSize);
        return block;
    }

    // The range starting at or before offset, and the one starting after it.
    private (long Offset, long Length)? Before(long offset)
        => ranges.Count == 0 || ranges.Min.Offset > offset ? null : ranges.GetViewBetween(ranges.Min, (offset, 0)).Max;

    private (long Offset, long Length)? After(long offset)
        => ranges.Count == 0 || ranges.Max.Offset <= offset ? null : ranges.GetViewBetween((offset + 1, 0), ranges.Max).Min;
}
