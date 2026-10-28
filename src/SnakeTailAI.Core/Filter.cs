using System.Globalization;

namespace SnakeTailAI.Core;

/// <summary>What the user typed in the filter bar. Serializable, so it can be saved as a preset.</summary>
public sealed record FilterSpec
{
    public string Include { get; init; } = "";
    public string Exclude { get; init; } = "";
    public bool IsRegex { get; init; }
    public bool MatchCase { get; init; }
    public bool WholeWord { get; init; }
    public LevelMask Levels { get; init; } = LevelMask.All;
    public string Thread { get; init; } = "";
    public string Source { get; init; } = "";
    public string TimeFrom { get; init; } = "";
    public string TimeTo { get; init; } = "";

    /// <summary>Devices to show in a timeline (<c>a; b</c>); a term starting with <c>-</c> hides that device.</summary>
    public string Device { get; init; } = "";

    public static FilterSpec Empty { get; } = new();

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Include) && string.IsNullOrWhiteSpace(Exclude) && Levels == LevelMask.All &&
        string.IsNullOrWhiteSpace(Thread) && string.IsNullOrWhiteSpace(Source) &&
        string.IsNullOrWhiteSpace(TimeFrom) && string.IsNullOrWhiteSpace(TimeTo) && string.IsNullOrWhiteSpace(Device);
}

/// <summary>A <see cref="FilterSpec"/> turned into a fast predicate. Immutable and thread-safe.</summary>
public sealed class CompiledFilter
{
    private readonly TextMatcher? _include;
    private readonly TextMatcher? _exclude;
    private readonly TextMatcher? _thread;
    private readonly TextMatcher? _source;
    private readonly string[] _devices;
    private readonly string[] _hiddenDevices;
    private readonly LevelMask _levels;
    private readonly TimeBound _from;
    private readonly TimeBound _to;

    public static CompiledFilter PassAll { get; } = new(FilterSpec.Empty);

    public FilterSpec Spec { get; }
    public bool IsPassAll { get; }

    private CompiledFilter(FilterSpec spec)
    {
        Spec = spec;
        _include = TextMatcher.Create(spec.Include, spec.IsRegex, spec.MatchCase, spec.WholeWord);
        _exclude = TextMatcher.Create(spec.Exclude, spec.IsRegex, spec.MatchCase, spec.WholeWord);
        _thread = TextMatcher.Create(spec.Thread, false, false, false);
        _source = TextMatcher.Create(spec.Source, false, false, false);
        var deviceTerms = (spec.Device ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _devices = deviceTerms.Where(t => !t.StartsWith('-')).ToArray();
        _hiddenDevices = deviceTerms.Where(t => t.StartsWith('-') && t.Length > 1).Select(t => t[1..].Trim()).ToArray();
        _levels = spec.Levels;
        _from = TimeBound.Parse(spec.TimeFrom, isUpper: false);
        _to = TimeBound.Parse(spec.TimeTo, isUpper: true);
        IsPassAll = spec.IsEmpty;
    }

    /// <summary>Throws <see cref="ArgumentException"/> (with a readable message) when a pattern or time is invalid.</summary>
    public static CompiledFilter Create(FilterSpec spec)
    {
        if (spec.IsEmpty)
            return PassAll;
        var compiled = new CompiledFilter(spec);
        if (!compiled._from.IsValid)
            throw new ArgumentException($"Invalid 'from' time: {spec.TimeFrom}");
        if (!compiled._to.IsValid)
            throw new ArgumentException($"Invalid 'to' time: {spec.TimeTo}");
        return compiled;
    }

    public bool IsMatch(LogLine line)
    {
        if (IsPassAll)
            return true;
        if (!DeviceAllowed(line.Origin))
            return false;
        if (line.IsSeparator)
            return true;
        if ((line.Level.ToMask() & _levels) == 0)
            return false;
        if (!_from.Allows(line.TimestampTicks) || !_to.Allows(line.TimestampTicks))
            return false;
        if (_thread != null && !_thread.IsMatch(line.ThreadSpan))
            return false;
        if (_source != null && !_source.IsMatch(line.SourceSpan) && !_source.IsMatch(line.FunctionSpan))
            return false;
        if (_exclude != null && _exclude.IsMatch(line.Raw))
            return false;
        if (_include != null && !_include.IsMatch(line.Raw))
            return false;
        return true;
    }

    private bool DeviceAllowed(string? origin)
    {
        if (_devices.Length == 0 && _hiddenDevices.Length == 0)
            return true;
        if (origin == null)
            return _devices.Length == 0;
        foreach (string hidden in _hiddenDevices)
        {
            if (origin.Contains(hidden, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (_devices.Length == 0)
            return true;
        foreach (string device in _devices)
        {
            if (origin.Contains(device, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>A time limit typed by the user; see <see cref="TimeText"/> for the accepted forms.</summary>
    private readonly struct TimeBound
    {
        private readonly bool _active;
        private readonly bool _timeOfDayOnly;
        private readonly bool _isUpper;
        private readonly long _ticks;

        public bool IsValid { get; }

        private TimeBound(bool active, bool timeOfDayOnly, bool isUpper, long ticks, bool valid)
        {
            _active = active;
            _timeOfDayOnly = timeOfDayOnly;
            _isUpper = isUpper;
            _ticks = ticks;
            IsValid = valid;
        }

        public static TimeBound Parse(string text, bool isUpper)
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0)
                return new TimeBound(false, false, isUpper, 0, true);
            if (!TimeText.TryParse(text, out long ticks, out bool timeOfDayOnly, out long precision))
                return new TimeBound(false, false, isUpper, 0, false);
            // "10:15" as an upper bound includes the whole minute, "2026-10-26" the whole day.
            if (isUpper)
                ticks += precision - 1;
            return new TimeBound(true, timeOfDayOnly, isUpper, ticks, true);
        }

        public bool Allows(long lineTicks)
        {
            if (!_active)
                return true;
            if (lineTicks == 0)
                return false;
            long value = _timeOfDayOnly ? lineTicks % TimeSpan.TicksPerDay : lineTicks;
            return _isUpper ? value <= _ticks : value >= _ticks;
        }
    }
}

/// <summary>
/// Times typed by the user: <c>HH:mm[:ss[.fff]]</c> (a time of day) or <c>yyyy-MM-dd [HH:mm[:ss[.fff]]]</c> (a moment).
/// </summary>
public static class TimeText
{
    private static readonly string[] TimeFormats = ["H:mm", "H:mm:ss", "H:mm:ss.FFFFFFF", "H:mm:ss,FFFFFFF"];
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "yyyy-MM-dd H:mm", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd H:mm:ss.FFFFFFF", "yyyy-MM-ddTH:mm:ss"];

    /// <param name="ticks">Time-of-day ticks, or absolute ticks when a date was given.</param>
    /// <param name="timeOfDayOnly">True when no date was given.</param>
    /// <param name="precision">The span the text stands for (a minute for "10:15", a day for a bare date).</param>
    public static bool TryParse(string text, out long ticks, out bool timeOfDayOnly, out long precision)
    {
        text = text.Trim();
        if (DateTime.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var t))
        {
            ticks = t.TimeOfDay.Ticks;
            timeOfDayOnly = true;
            precision = Precision(text);
            return true;
        }
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            ticks = d.Ticks;
            timeOfDayOnly = false;
            precision = text.Length == 10 ? TimeSpan.TicksPerDay : Precision(text[11..]);
            return true;
        }
        ticks = precision = 0;
        timeOfDayOnly = false;
        return false;
    }

    private static long Precision(string time) =>
        time.Contains('.') || time.Contains(',') ? 1 : time.Count(c => c == ':') == 1 ? TimeSpan.TicksPerMinute : TimeSpan.TicksPerSecond;

    /// <summary>
    /// The moment to jump to. A bare time of day is placed on the newest day of the log (<paramref name="latest"/>)
    /// that keeps it within the lines held (<paramref name="earliest"/> … <paramref name="latest"/>).
    /// </summary>
    public static bool TryResolve(string text, DateTime? earliest, DateTime? latest, out DateTime target)
    {
        target = default;
        if (!TryParse(text, out long ticks, out bool timeOfDayOnly, out _))
            return false;
        if (!timeOfDayOnly)
        {
            target = new DateTime(ticks);
            return true;
        }
        var last = latest ?? DateTime.Now;
        var first = earliest ?? last;
        for (var day = last.Date; day >= first.Date; day = day.AddDays(-1))
        {
            target = day.AddTicks(ticks);
            if (target <= last)
                return true;
        }
        target = last.Date.AddTicks(ticks);
        return true;
    }

    /// <summary>Index of the first line at or after <paramref name="target"/>, or -1 when every line is earlier.</summary>
    public static int FindFirstAtOrAfter(IReadOnlyList<LogLine> lines, DateTime target)
    {
        long ticks = target.Ticks;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].TimestampTicks >= ticks && lines[i].TimestampTicks != 0)
                return i;
        }
        return -1;
    }
}
