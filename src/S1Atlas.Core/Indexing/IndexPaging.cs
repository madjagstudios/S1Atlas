namespace S1Atlas.Core.Indexing;

// Shared page-window math for paged queries. Pages fetch one row past the
// limit so callers learn whether more rows remain without a separate count.
public static class IndexPaging
{
    public static void ValidateOffset(int offset)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "The query offset must not be negative.");
    }

    // Fetch window covering one page plus the one row past it that decides
    // whether more rows remain.
    public static int PageWindow(int offset, int limit)
    {
        ValidateOffset(offset);
        return (long)offset + limit + 1 > int.MaxValue ? int.MaxValue : offset + limit + 1;
    }

    // Merge span covering a page without the probe row. Fan-out callers pass
    // the span as the per-side limit so the per-side query adds the single
    // +1 probe row itself instead of stacking a second one.
    public static int PageSpan(int offset, int limit)
    {
        ValidateOffset(offset);
        return (long)offset + limit > int.MaxValue ? int.MaxValue : offset + limit;
    }

    public static (IReadOnlyList<T> Rows, bool HasMore) TakePage<T>(IReadOnlyList<T> ordered, int offset, int limit)
    {
        ValidateOffset(offset);
        var window = limit >= int.MaxValue ? limit : limit + 1;
        var taken = ordered.Skip(offset).Take(window).ToArray();
        return taken.Length > limit
            ? (taken.Take(limit).ToArray(), true)
            : (taken, false);
    }
}
