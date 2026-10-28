using System.Text;
using System.Text.RegularExpressions;

namespace SnakeTailAI.Core;

/// <summary>Lines whose messages differ only in numbers, e.g. "Not enough matches. NMatches = #".</summary>
public sealed record MessageGroup(string Template, LogLevel Level, string Source, int Count, LogLine First, LogLine Last);

/// <summary>Groups similar messages: numbers (and hex values) are replaced by <c>#</c>, then equal messages are counted.</summary>
public static class MessageGrouper
{
    /// <summary>Stands for any number in a template.</summary>
    public const char Placeholder = '#';

    public static string Normalize(ReadOnlySpan<char> text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            bool negative = c == '-' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]) &&
                            (i == 0 || !char.IsAsciiLetterOrDigit(text[i - 1]));
            if (char.IsAsciiDigit(c) || negative)
            {
                int j = negative ? i + 1 : i;
                if (text[j] == '0' && j + 1 < text.Length && (text[j + 1] == 'x' || text[j + 1] == 'X'))
                {
                    j += 2;
                    while (j < text.Length && char.IsAsciiHexDigit(text[j]))
                        j++;
                }
                else
                {
                    while (j < text.Length && (char.IsAsciiDigit(text[j]) ||
                           ((text[j] == '.' || text[j] == ',') && j + 1 < text.Length && char.IsAsciiDigit(text[j + 1]))))
                        j++;
                }
                if (sb.Length == 0 || sb[^1] != Placeholder)
                    sb.Append(Placeholder);
                i = j;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Groups the first <paramref name="count"/> lines; the largest groups first.</summary>
    public static List<MessageGroup> Group(IReadOnlyList<LogLine> lines, int count, CancellationToken ct = default, int max = 1000)
    {
        var groups = new Dictionary<string, (string Template, LogLevel Level, string Source, int Count, LogLine First, LogLine Last)>();
        for (int i = 0; i < count; i++)
        {
            if ((i & 0xFFF) == 0)
                ct.ThrowIfCancellationRequested();
            var line = lines[i];
            if (line.IsSeparator)
                continue;
            bool hasHeader = line.Kind == LineKind.Normal && line.TimeLength > 0;
            string template = Normalize(hasHeader ? line.Raw.AsSpan(line.MessageStart) : line.Raw.AsSpan());
            if (template.Length == 0)
                continue;
            string source = line.SourceLength > 0
                ? line.FunctionLength > 0 ? $"{line.SourceText}::{line.FunctionText}" : line.SourceText
                : "";
            string key = $"{(int)line.Level}\u0001{source}\u0001{template}";
            if (groups.TryGetValue(key, out var g))
                groups[key] = (g.Template, g.Level, g.Source, g.Count + 1, g.First, line);
            else
                groups[key] = (template, line.Level, source, 1, line, line);
        }
        return groups.Values
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.Level)
            .Take(max)
            .Select(g => new MessageGroup(g.Template, g.Level, g.Source, g.Count, g.First, g.Last))
            .ToList();
    }

    /// <summary>A regex that matches the messages of a template (each <c>#</c> becomes "any number").</summary>
    public static string ToRegex(string template)
    {
        string escaped = Regex.Escape(template);
        return escaped.Replace(@"\#", @"-?(?:0[xX][0-9A-Fa-f]+|\d+(?:[.,]\d+)*)").Replace(@"\ ", " ");
    }
}
