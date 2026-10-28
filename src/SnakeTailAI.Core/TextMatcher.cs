using System.Text.RegularExpressions;

namespace SnakeTailAI.Core;

/// <summary>
/// Matches text against a pattern, either as plain terms (split on <c>;</c>, any term matches) or as one regex.
/// Shared by filters, find and highlight rules. Immutable and thread-safe.
/// </summary>
public sealed class TextMatcher
{
    private readonly string[] _terms = [];
    private readonly Regex? _regex;
    private readonly StringComparison _comparison;

    private TextMatcher(string[] terms, Regex? regex, StringComparison comparison)
    {
        _terms = terms;
        _regex = regex;
        _comparison = comparison;
    }

    /// <summary>Returns null when the pattern is empty. Throws <see cref="ArgumentException"/> for an invalid regex.</summary>
    public static TextMatcher? Create(string? pattern, bool isRegex, bool matchCase, bool wholeWord, bool splitTerms = true)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;

        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (!matchCase)
            options |= RegexOptions.IgnoreCase;

        if (isRegex)
        {
            string p = wholeWord ? $@"\b(?:{pattern})\b" : pattern;
            return new TextMatcher([], new Regex(p, options), StringComparison.Ordinal);
        }

        string[] terms = splitTerms
            ? pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [pattern];
        if (terms.Length == 0)
            return null;

        if (wholeWord)
        {
            string alternation = string.Join("|", terms.Select(Regex.Escape));
            return new TextMatcher([], new Regex($@"\b(?:{alternation})\b", options), StringComparison.Ordinal);
        }

        return new TextMatcher(terms, null, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks a pattern without keeping the matcher; returns the error message or null.</summary>
    public static string? Validate(string? pattern, bool isRegex)
    {
        if (!isRegex || string.IsNullOrWhiteSpace(pattern))
            return null;
        try
        {
            _ = new Regex(pattern);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    public bool IsMatch(ReadOnlySpan<char> text)
    {
        if (_regex != null)
            return _regex.IsMatch(text);
        foreach (string term in _terms)
        {
            if (text.IndexOf(term, _comparison) >= 0)
                return true;
        }
        return false;
    }

    public bool IsMatch(string text) => IsMatch(text.AsSpan());

    /// <summary>All match ranges in <paramref name="text"/>, sorted by start (they may overlap for several terms).</summary>
    public List<(int Start, int Length)> Matches(string text, int maxMatches = 256)
    {
        var result = new List<(int, int)>();
        if (_regex != null)
        {
            foreach (ValueMatch m in _regex.EnumerateMatches(text))
            {
                if (m.Length > 0)
                    result.Add((m.Index, m.Length));
                if (result.Count >= maxMatches)
                    break;
            }
            return result;
        }

        foreach (string term in _terms)
        {
            int i = 0;
            while (result.Count < maxMatches && (i = text.IndexOf(term, i, _comparison)) >= 0)
            {
                result.Add((i, term.Length));
                i += term.Length;
            }
        }
        if (_terms.Length > 1)
            result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }
}
