namespace SnakeTailAI.Core;

public enum LineKind : byte
{
    Normal,
    /// <summary>A line without its own header (e.g. a stack trace); it inherits level and time from the previous line.</summary>
    Continuation,
    /// <summary>A marker inserted by the viewer (rotation, file boundary). It is not part of the file.</summary>
    Separator,
}

/// <summary>
/// One line of a log. Parsed fields are kept as offsets into <see cref="Raw"/>, so no extra strings are allocated per line.
/// </summary>
public sealed class LogLine
{
    internal LogLine(string raw, LineKind kind, LogLevel level, long timestampTicks)
    {
        Raw = raw;
        Kind = kind;
        Level = level;
        TimestampTicks = timestampTicks;
    }

    public static LogLine Separator(string text) => new(text, LineKind.Separator, LogLevel.None, 0);

    /// <summary>Sequence number inside the document (1-based, increasing). Assigned by <see cref="LogDocument"/>.</summary>
    public long Number { get; internal set; }

    public string Raw { get; }
    public LineKind Kind { get; }
    public LogLevel Level { get; }

    /// <summary>Timestamp ticks, or 0 when the line has none.</summary>
    public long TimestampTicks { get; }

    public int TimeLength { get; internal set; }
    public int LevelStart { get; internal set; }
    public int LevelLength { get; internal set; }
    public int ThreadStart { get; internal set; }
    public int ThreadLength { get; internal set; }
    public int SourceStart { get; internal set; }
    public int SourceLength { get; internal set; }
    public int SourceLineStart { get; internal set; }
    public int SourceLineLength { get; internal set; }
    public int FunctionStart { get; internal set; }
    public int FunctionLength { get; internal set; }
    public int MessageStart { get; internal set; }

    public bool IsBookmarked { get; set; }

    /// <summary>The device the line comes from, in a timeline that merges several logs; null otherwise.</summary>
    public string? Origin { get; set; }

    /// <summary><see cref="Environment.TickCount64"/> when the line arrived from a live tail; 0 for lines loaded at open.</summary>
    public long ArrivedTicks { get; set; }

    public bool IsSeparator => Kind == LineKind.Separator;
    public DateTime? Timestamp => TimestampTicks == 0 ? null : new DateTime(TimestampTicks);

    public string TimeText => TimeLength > 0 ? Raw[..TimeLength] : "";
    public string ThreadText => ThreadLength > 0 ? Raw.Substring(ThreadStart, ThreadLength) : "";
    public string SourceText => SourceLength > 0 ? Raw.Substring(SourceStart, SourceLength) : "";
    public string SourceLineText => SourceLineLength > 0 ? Raw.Substring(SourceLineStart, SourceLineLength) : "";
    public string FunctionText => FunctionLength > 0 ? Raw.Substring(FunctionStart, FunctionLength) : "";
    public string MessageText => MessageStart < Raw.Length ? Raw[MessageStart..] : "";

    public ReadOnlySpan<char> ThreadSpan => Raw.AsSpan(ThreadStart, ThreadLength);
    public ReadOnlySpan<char> SourceSpan => Raw.AsSpan(SourceStart, SourceLength);
    public ReadOnlySpan<char> FunctionSpan => Raw.AsSpan(FunctionStart, FunctionLength);

    public override string ToString() => Raw;
}
