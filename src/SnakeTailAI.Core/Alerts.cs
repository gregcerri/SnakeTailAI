namespace SnakeTailAI.Core;

public enum AlertLevel
{
    Any,
    WarningOrAbove,
    ErrorOrAbove,
}

/// <summary>"When a new line matches, tell me": a pattern and/or a minimum level, with a notification and/or a sound.</summary>
public sealed class AlertRule
{
    public bool Enabled { get; set; } = true;
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public bool MatchCase { get; set; }
    public AlertLevel MinLevel { get; set; } = AlertLevel.Any;
    public bool ShowNotification { get; set; } = true;
    public bool PlaySound { get; set; }

    public AlertRule Clone() => (AlertRule)MemberwiseClone();

    public string Describe()
    {
        string level = MinLevel switch
        {
            AlertLevel.ErrorOrAbove => "ERROR",
            AlertLevel.WarningOrAbove => "WARN+",
            _ => "",
        };
        if (string.IsNullOrWhiteSpace(Pattern))
            return level.Length > 0 ? $"any {level} line" : "any line";
        return level.Length > 0 ? $"{level} with \"{Pattern}\"" : $"\"{Pattern}\"";
    }

    public static List<AlertRule> Defaults() =>
    [
        new() { Pattern = "Exception|FATAL|Crash|crash", IsRegex = true, MatchCase = true },
        new() { Enabled = false, MinLevel = AlertLevel.ErrorOrAbove },
    ];
}

/// <summary>Enabled rules compiled for checking new lines. Immutable and thread-safe.</summary>
public sealed class AlertMatcher
{
    private readonly (AlertRule Rule, TextMatcher? Matcher)[] _rules;

    public static AlertMatcher Empty { get; } = new([]);

    public AlertMatcher(IEnumerable<AlertRule> rules)
    {
        var list = new List<(AlertRule, TextMatcher?)>();
        foreach (var rule in rules)
        {
            // A rule with neither a pattern nor a level would fire on every line.
            if (!rule.Enabled || (string.IsNullOrWhiteSpace(rule.Pattern) && rule.MinLevel == AlertLevel.Any))
                continue;
            if (!rule.ShowNotification && !rule.PlaySound)
                continue;
            try
            {
                list.Add((rule.Clone(), TextMatcher.Create(rule.Pattern, rule.IsRegex, rule.MatchCase, false, splitTerms: !rule.IsRegex)));
            }
            catch (ArgumentException)
            {
                // An invalid regex disables that rule only.
            }
        }
        _rules = [.. list];
    }

    public bool IsEmpty => _rules.Length == 0;

    /// <summary>The first rule the line triggers, or null.</summary>
    public AlertRule? Match(LogLine line)
    {
        if (line.IsSeparator)
            return null;
        foreach (var (rule, matcher) in _rules)
        {
            bool levelOk = rule.MinLevel switch
            {
                AlertLevel.ErrorOrAbove => line.Level >= LogLevel.Error,
                AlertLevel.WarningOrAbove => line.Level >= LogLevel.Warning,
                _ => true,
            };
            if (levelOk && (matcher == null || matcher.IsMatch(line.Raw)))
                return rule;
        }
        return null;
    }
}
