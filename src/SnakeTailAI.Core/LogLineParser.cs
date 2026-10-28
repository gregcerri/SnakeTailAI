namespace SnakeTailAI.Core;

/// <summary>
/// Parses log lines. It understands the VIU format
/// <c>2026-10-26 09:59:59.762166 DEBUG [10-31584] Line 92 - File.cpp::run(): message</c>,
/// the log4net format <c>2025-11-12 14:33:41,697 [6] INFO  Logger.Method - message</c>,
/// and falls back to finding a level word in any line.
/// It is stateful: a line without a header inherits level and timestamp from the previous line.
/// One instance per stream; not thread-safe.
/// </summary>
public sealed class LogLineParser
{
    private LogLevel _prevLevel;
    private long _prevTicks;

    public void Reset()
    {
        _prevLevel = LogLevel.None;
        _prevTicks = 0;
    }

    public LogLine Parse(string raw)
    {
        if (raw.Contains('\t'))
            raw = raw.Replace("\t", "    ");

        int p = 0;
        bool hasTime = TryParseTimestamp(raw, out long ticks, out int timeLength);
        if (hasTime)
            p = SkipSpaces(raw, timeLength);

        LogLevel level = LogLevel.None;
        int levelStart = 0, levelLength = 0, threadStart = 0, threadLength = 0;

        if (hasTime)
        {
            // Level and [thread] can come in either order.
            for (int token = 0; token < 2 && p < raw.Length; token++)
            {
                if (raw[p] == '[' && threadLength == 0)
                {
                    int close = raw.IndexOf(']', p + 1);
                    if (close < 0 || close - p > 64)
                        break;
                    threadStart = p + 1;
                    threadLength = close - p - 1;
                    p = SkipSpaces(raw, close + 1);
                }
                else if (level == LogLevel.None)
                {
                    int end = WordEnd(raw, p);
                    if (!LogLevelExtensions.TryParseToken(raw.AsSpan(p, end - p), out level))
                        break;
                    levelStart = p;
                    levelLength = end - p;
                    p = SkipSpaces(raw, end);
                }
            }
        }
        else
        {
            if (!FindLevelWord(raw, out level, out levelStart, out levelLength))
            {
                var cont = new LogLine(raw, LineKind.Continuation, _prevLevel, _prevTicks);
                return cont;
            }
        }

        var line = new LogLine(raw, LineKind.Normal, level, hasTime ? ticks : _prevTicks)
        {
            TimeLength = hasTime ? timeLength : 0,
            LevelStart = levelStart,
            LevelLength = levelLength,
            ThreadStart = threadStart,
            ThreadLength = threadLength,
            MessageStart = hasTime ? p : 0,
        };

        if (hasTime)
            ParseSourceAndMessage(raw, p, line);

        _prevLevel = level;
        if (hasTime)
            _prevTicks = ticks;
        return line;
    }

    private static void ParseSourceAndMessage(string s, int p, LogLine line)
    {
        // VIU: "Line 92 - File.cpp::function(): message"
        if (string.CompareOrdinal(s, p, "Line ", 0, 5) == 0)
        {
            int d = p + 5;
            int digitsEnd = d;
            while (digitsEnd < s.Length && char.IsAsciiDigit(s[digitsEnd]))
                digitsEnd++;
            if (digitsEnd > d && string.CompareOrdinal(s, digitsEnd, " - ", 0, 3) == 0)
            {
                line.SourceLineStart = d;
                line.SourceLineLength = digitsEnd - d;
                int q = digitsEnd + 3;
                line.MessageStart = q;

                int sep = s.IndexOf("::", q, Math.Min(200, s.Length - q), StringComparison.Ordinal);
                if (sep > q && s.AsSpan(q, sep - q).IndexOf(' ') < 0)
                {
                    line.SourceStart = q;
                    line.SourceLength = sep - q;
                    int fnStart = sep + 2;
                    int paren = s.IndexOf('(', fnStart);
                    int msg = s.IndexOf("): ", fnStart, StringComparison.Ordinal);
                    if (paren > fnStart && msg >= paren)
                    {
                        line.FunctionStart = fnStart;
                        line.FunctionLength = paren - fnStart;
                        line.MessageStart = msg + 3;
                    }
                    else if (paren > fnStart)
                    {
                        // "File.cpp::function():" with an empty message
                        int close = s.IndexOf(')', paren);
                        int m = close < 0 ? s.Length : close + 1;
                        if (m < s.Length && s[m] == ':')
                            m++;
                        line.FunctionStart = fnStart;
                        line.FunctionLength = paren - fnStart;
                        line.MessageStart = SkipSpaces(s, m);
                    }
                }
                return;
            }
        }

        // log4net: "Logger.Method - message"
        int tokenEnd = s.IndexOf(' ', p);
        if (tokenEnd > p && string.CompareOrdinal(s, tokenEnd, " - ", 0, 3) == 0)
        {
            int dot = s.LastIndexOf('.', tokenEnd - 1, tokenEnd - p);
            if (dot > p && dot < tokenEnd - 1)
            {
                line.SourceStart = p;
                line.SourceLength = dot - p;
                line.FunctionStart = dot + 1;
                line.FunctionLength = tokenEnd - dot - 1;
            }
            else
            {
                line.SourceStart = p;
                line.SourceLength = tokenEnd - p;
            }
            line.MessageStart = tokenEnd + 3;
        }
    }

    /// <summary>Parses <c>yyyy-MM-dd[ T]HH:mm:ss[.,]fffffff</c> at the start of the line.</summary>
    public static bool TryParseTimestamp(string s, out long ticks, out int length)
    {
        ticks = 0;
        length = 0;
        if (s.Length < 19)
            return false;
        if (s[4] != '-' || s[7] != '-' || (s[10] != ' ' && s[10] != 'T') || s[13] != ':' || s[16] != ':')
            return false;
        if (!Num(s, 0, 4, out int year) || !Num(s, 5, 2, out int month) || !Num(s, 8, 2, out int day) ||
            !Num(s, 11, 2, out int hour) || !Num(s, 14, 2, out int minute) || !Num(s, 17, 2, out int second))
            return false;
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59 || second > 59)
            return false;

        long frac = 0;
        int i = 19;
        if (i < s.Length && (s[i] == '.' || s[i] == ','))
        {
            int start = ++i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                if (i - start < 7)
                    frac = frac * 10 + (s[i] - '0');
                i++;
            }
            int digits = Math.Min(7, i - start);
            for (int k = digits; k < 7; k++)
                frac *= 10;
            if (i == start)
                i--; // a lone '.' is not part of the timestamp
        }

        ticks = new DateTime(year, month, day, hour, minute, second).Ticks + frac;
        length = i;
        return true;
    }

    private static bool Num(string s, int start, int count, out int value)
    {
        value = 0;
        for (int i = start; i < start + count; i++)
        {
            char c = s[i];
            if (!char.IsAsciiDigit(c))
                return false;
            value = value * 10 + (c - '0');
        }
        return true;
    }

    private static int SkipSpaces(string s, int i)
    {
        while (i < s.Length && s[i] == ' ')
            i++;
        return i;
    }

    private static int WordEnd(string s, int i)
    {
        while (i < s.Length && char.IsAsciiLetter(s[i]))
            i++;
        return i;
    }

    /// <summary>Looks for a level word (e.g. <c>ERROR</c>, <c>[WARN]</c>, <c>INFO:</c>) in the first 80 characters.</summary>
    private static bool FindLevelWord(string s, out LogLevel level, out int start, out int length)
    {
        level = LogLevel.None;
        start = length = 0;
        int limit = Math.Min(s.Length, 80);
        int i = 0;
        while (i < limit)
        {
            if (!char.IsAsciiLetterUpper(s[i]) || (i > 0 && char.IsAsciiLetterOrDigit(s[i - 1])))
            {
                i++;
                continue;
            }
            int end = i;
            while (end < s.Length && char.IsAsciiLetterUpper(s[end]))
                end++;
            bool boundary = end == s.Length || !char.IsAsciiLetterOrDigit(s[end]);
            if (boundary && LogLevelExtensions.TryParseToken(s.AsSpan(i, end - i), out level))
            {
                start = i;
                length = end - i;
                return true;
            }
            i = end + 1;
        }
        level = LogLevel.None;
        return false;
    }
}
