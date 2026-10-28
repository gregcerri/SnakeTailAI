namespace SnakeTailAI.Core;

public readonly record struct AppendResult(int Added, int ViewAdded, int ErrorsAdded);

/// <summary>
/// The lines of one tab: every line read (<see cref="All"/>) and the filtered subset shown (<see cref="View"/>).
/// It only works on what the viewer has read; nothing here ever writes to the log file.
/// Must be used from a single thread (the UI thread); only the filter scan runs in the background.
/// </summary>
public sealed class LogDocument
{
    private ChunkedList<LogLine> _all = new();
    private List<LogLine> _view = [];
    private CompiledFilter _filter = CompiledFilter.PassAll;
    private long _nextNumber = 1;
    private int _filterGeneration;
    private readonly int[] _levelCounts = new int[5];

    public IReadOnlyList<LogLine> All => _all;
    public IReadOnlyList<LogLine> View => _view;
    public CompiledFilter Filter => _filter;

    /// <summary>When more lines than this are held, the oldest are dropped (from memory only).</summary>
    public int MaxLines { get; set; } = 2_000_000;

    public int MaxLineLength { get; private set; }

    /// <summary>Width (in characters) of the device column of a timeline; 0 when lines have no device.</summary>
    public int OriginWidth { get; set; }

    /// <summary>View indices of error / warning / bookmarked lines, sorted. Used by the overview strip.</summary>
    public List<int> ErrorMarks { get; private set; } = [];
    public List<int> WarningMarks { get; private set; } = [];
    public List<int> BookmarkMarks { get; private set; } = [];

    /// <summary>Line counts per level bucket over all lines (Debug, Info, Warning, Error, Other).</summary>
    public IReadOnlyList<int> LevelCounts => _levelCounts;

    /// <summary>Lines were added at the end of <see cref="View"/>.</summary>
    public event EventHandler<AppendResult>? Appended;

    /// <summary><see cref="View"/> was replaced (filter change, clear, trim). Viewers should re-locate by line number.</summary>
    public event EventHandler? ViewReset;

    public AppendResult Append(IReadOnlyList<LogLine> batch)
    {
        int viewAdded = 0, errors = 0;
        foreach (var line in batch)
        {
            line.Number = _nextNumber++;
            _all.Add(line);
            if (!line.IsSeparator)
                _levelCounts[line.Level.BucketIndex()]++;
            if (line.Level >= LogLevel.Error)
                errors++;
            if (line.Raw.Length > MaxLineLength)
                MaxLineLength = line.Raw.Length;
            if (_filter.IsMatch(line))
            {
                AddToView(line);
                viewAdded++;
            }
        }

        var result = new AppendResult(batch.Count, viewAdded, errors);
        if (_all.Count > MaxLines + MaxLines / 10)
        {
            Trim();
            ViewReset?.Invoke(this, EventArgs.Empty);
        }
        else if (batch.Count > 0)
        {
            Appended?.Invoke(this, result);
        }
        return result;
    }

    /// <summary>Empties the screen: forgets every line held in memory. The file is not touched.</summary>
    public void Clear()
    {
        _all = new ChunkedList<LogLine>();
        _view = [];
        Array.Clear(_levelCounts);
        MaxLineLength = 0;
        RebuildMarks();
        ViewReset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies a new filter. The scan runs in the background; the result is installed on the calling thread.
    /// Returns false if a newer call superseded this one.
    /// </summary>
    public async Task<bool> ApplyFilterAsync(CompiledFilter filter, CancellationToken ct = default)
    {
        int generation = ++_filterGeneration;
        var all = _all;
        int snapshot = all.Count;

        var result = await Task.Run(() =>
        {
            var list = new List<LogLine>(filter.IsPassAll ? snapshot : 1024);
            for (int i = 0; i < snapshot; i++)
            {
                if ((i & 0xFFF) == 0)
                    ct.ThrowIfCancellationRequested();
                var line = all[i];
                if (filter.IsMatch(line))
                    list.Add(line);
            }
            return list;
        }, ct);

        if (generation != _filterGeneration || ct.IsCancellationRequested)
            return false;

        if (all != _all)
        {
            // Cleared or trimmed while the scan ran: filter what is held now.
            all = _all;
            snapshot = 0;
            result.Clear();
        }

        // Lines appended while the scan ran.
        for (int i = snapshot; i < all.Count; i++)
        {
            if (filter.IsMatch(all[i]))
                result.Add(all[i]);
        }

        _filter = filter;
        _view = result;
        RebuildMarks();
        ViewReset?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Index in <see cref="View"/> of the line with this number, or of the nearest line after it (clamped).</summary>
    public int FindViewIndex(long number)
    {
        if (_view.Count == 0)
            return -1;
        int lo = 0, hi = _view.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_view[mid].Number < number)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    public void ToggleBookmark(LogLine line)
    {
        line.IsBookmarked = !line.IsBookmarked;
        int index = FindViewIndex(line.Number);
        if (index < 0 || _view[index] != line)
            return;
        int pos = BookmarkMarks.BinarySearch(index);
        if (line.IsBookmarked && pos < 0)
            BookmarkMarks.Insert(~pos, index);
        else if (!line.IsBookmarked && pos >= 0)
            BookmarkMarks.RemoveAt(pos);
    }

    public void ClearBookmarks()
    {
        foreach (var line in _all)
            line.IsBookmarked = false;
        BookmarkMarks.Clear();
    }

    private void AddToView(LogLine line)
    {
        int index = _view.Count;
        _view.Add(line);
        if (line.Level >= LogLevel.Error)
            ErrorMarks.Add(index);
        else if (line.Level == LogLevel.Warning)
            WarningMarks.Add(index);
        if (line.IsBookmarked)
            BookmarkMarks.Add(index);
    }

    private void RebuildMarks()
    {
        var errors = new List<int>();
        var warnings = new List<int>();
        var bookmarks = new List<int>();
        for (int i = 0; i < _view.Count; i++)
        {
            var line = _view[i];
            if (line.Level >= LogLevel.Error)
                errors.Add(i);
            else if (line.Level == LogLevel.Warning)
                warnings.Add(i);
            if (line.IsBookmarked)
                bookmarks.Add(i);
        }
        ErrorMarks = errors;
        WarningMarks = warnings;
        BookmarkMarks = bookmarks;
    }

    private void Trim()
    {
        int drop = _all.Count - MaxLines;
        var kept = new ChunkedList<LogLine>();
        Array.Clear(_levelCounts);
        for (int i = drop; i < _all.Count; i++)
        {
            var line = _all[i];
            kept.Add(line);
            if (!line.IsSeparator)
                _levelCounts[line.Level.BucketIndex()]++;
        }
        _all = kept;

        long firstNumber = kept.Count > 0 ? kept[0].Number : long.MaxValue;
        int viewDrop = 0;
        while (viewDrop < _view.Count && _view[viewDrop].Number < firstNumber)
            viewDrop++;
        _view.RemoveRange(0, viewDrop);
        RebuildMarks();
    }
}
