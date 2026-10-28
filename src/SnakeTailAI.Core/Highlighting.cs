namespace SnakeTailAI.Core;

/// <summary>A user colouring rule. Colours are <c>#RRGGBB</c> or <c>#AARRGGBB</c>; empty means "keep the default".</summary>
public sealed class HighlightRule
{
    public bool Enabled { get; set; } = true;
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public bool MatchCase { get; set; }
    public string Foreground { get; set; } = "";
    public string Background { get; set; } = "";
    public bool Bold { get; set; }

    /// <summary>Colour the whole line, or only the matched text.</summary>
    public bool WholeLine { get; set; }

    public HighlightRule Clone() => (HighlightRule)MemberwiseClone();

    public static List<HighlightRule> Defaults() =>
    [
        // Mid-tone colours, readable on both the dark and the light theme.
        new() { Pattern = "check PASSED", Foreground = "#22C55E", Bold = true },
        new() { Pattern = @"FAILED|Exception|exception|failed", IsRegex = true, MatchCase = true, Foreground = "#EF4444", Bold = true },
        new() { Pattern = "saveRejects SELECTED", Foreground = "#EC4899", Bold = true },
        new() { Pattern = "Not enough matches", Background = "#33FF5C5C" },
    ];
}

public readonly record struct HighlightSpan(int Start, int Length, HighlightRule Rule);

/// <summary>Rules compiled for rendering. Immutable and thread-safe.</summary>
public sealed class Highlighter
{
    private readonly (HighlightRule Rule, TextMatcher Matcher)[] _rules;

    public static Highlighter Empty { get; } = new([]);

    public Highlighter(IEnumerable<HighlightRule> rules)
    {
        var list = new List<(HighlightRule, TextMatcher)>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled)
                continue;
            try
            {
                var matcher = TextMatcher.Create(rule.Pattern, rule.IsRegex, rule.MatchCase, false, splitTerms: false);
                if (matcher != null)
                    list.Add((rule.Clone(), matcher));
            }
            catch (ArgumentException)
            {
                // An invalid regex disables that rule only.
            }
        }
        _rules = [.. list];
    }

    public bool IsEmpty => _rules.Length == 0;

    /// <summary>The first whole-line rule that matches, and the match-only spans of the other rules.</summary>
    public (HighlightRule? LineRule, List<HighlightSpan>? Spans) Evaluate(string text)
    {
        HighlightRule? lineRule = null;
        List<HighlightSpan>? spans = null;
        foreach (var (rule, matcher) in _rules)
        {
            if (rule.WholeLine)
            {
                if (lineRule == null && matcher.IsMatch(text))
                    lineRule = rule;
                continue;
            }
            foreach (var (start, length) in matcher.Matches(text, 64))
                (spans ??= []).Add(new HighlightSpan(start, length, rule));
        }
        return (lineRule, spans);
    }
}
