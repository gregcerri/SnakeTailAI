namespace SnakeTailAI.Core;

public enum LogLevel : byte
{
    None,
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
}

/// <summary>Level buckets used by the filter chips.</summary>
[Flags]
public enum LevelMask
{
    None = 0,
    Debug = 1,
    Info = 2,
    Warning = 4,
    Error = 8,
    Other = 16,
    All = Debug | Info | Warning | Error | Other,
}

public static class LogLevelExtensions
{
    public static LevelMask ToMask(this LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => LevelMask.Debug,
        LogLevel.Info => LevelMask.Info,
        LogLevel.Warning => LevelMask.Warning,
        LogLevel.Error or LogLevel.Fatal => LevelMask.Error,
        _ => LevelMask.Other,
    };

    /// <summary>Index of the chip bucket (0..4), matching the bit order of <see cref="LevelMask"/>.</summary>
    public static int BucketIndex(this LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => 0,
        LogLevel.Info => 1,
        LogLevel.Warning => 2,
        LogLevel.Error or LogLevel.Fatal => 3,
        _ => 4,
    };

    public static string ShortName(this LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Fatal => "FATAL",
        _ => "",
    };

    /// <summary>Maps a level token as written in a log file to a <see cref="LogLevel"/>.</summary>
    public static bool TryParseToken(ReadOnlySpan<char> token, out LogLevel level)
    {
        level = LogLevel.None;
        if (token.Length is < 3 or > 8)
            return false;

        Span<char> upper = stackalloc char[token.Length];
        token.ToUpperInvariant(upper);
        level = upper switch
        {
            "TRACE" or "VERBOSE" or "FINEST" => LogLevel.Trace,
            "DEBUG" or "DBG" or "FINE" => LogLevel.Debug,
            "INFO" or "INF" or "NOTICE" => LogLevel.Info,
            "WARN" or "WARNING" or "WRN" => LogLevel.Warning,
            "ERROR" or "ERR" or "SEVERE" => LogLevel.Error,
            "FATAL" or "CRIT" or "CRITICAL" or "FTL" => LogLevel.Fatal,
            _ => LogLevel.None,
        };
        return level != LogLevel.None;
    }
}
