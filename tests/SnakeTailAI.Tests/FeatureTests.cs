using SnakeTailAI.Core;

namespace SnakeTailAI.Tests;

public class FeatureTests
{
    private static LogLine Line(string raw) => new LogLineParser().Parse(raw);

    // ---------------------------------------------------------------- message groups

    [Theory]
    [InlineData("Not enough matches. NMatches = 10", "Not enough matches. NMatches = #")]
    [InlineData("updating check NotchPosition of value: 86.393059", "updating check NotchPosition of value: #")]
    [InlineData("delta -10.000000 , Good: 90.000000", "delta # , Good: #")]
    [InlineData("handle 0x1F3A ok", "handle # ok")]
    [InlineData("[200::APP_Snus-Label-TOP] done", "[#::APP_Snus-Label-TOP] done")]
    [InlineData("APP_Snus-Label-TOP", "APP_Snus-Label-TOP")]
    public void NormalizesNumbers(string message, string expected) => Assert.Equal(expected, MessageGrouper.Normalize(message));

    [Fact]
    public void GroupsSimilarMessages()
    {
        var lines = new[]
        {
            Line("2026-10-26 10:00:00.000001 ERROR [1-2] Line 359 - a.cpp::elab(): Not enough matches. NMatches = 10"),
            Line("2026-10-26 10:00:00.000002 DEBUG [1-2] Line 5 - b.cpp::run(): End run"),
            Line("2026-10-26 10:00:00.000003 ERROR [3-4] Line 359 - a.cpp::elab(): Not enough matches. NMatches = 7"),
            Line("2026-10-26 10:00:00.000004 ERROR [3-4] Line 359 - a.cpp::elab(): Not enough matches. NMatches = 12"),
            LogLine.Separator("log rotated"),
        };
        var groups = MessageGrouper.Group(lines, lines.Length);

        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups[0].Count);
        Assert.Equal("Not enough matches. NMatches = #", groups[0].Template);
        Assert.Equal(LogLevel.Error, groups[0].Level);
        Assert.Equal("a.cpp::elab", groups[0].Source);
        Assert.Same(lines[0], groups[0].First);
        Assert.Same(lines[3], groups[0].Last);
    }

    [Fact]
    public void TemplateRegexMatchesTheMessages()
    {
        var matcher = TextMatcher.Create(MessageGrouper.ToRegex("NotchPosition of value: # (max #)"), isRegex: true, matchCase: true, wholeWord: false)!;
        Assert.True(matcher.IsMatch("x NotchPosition of value: 86.393059 (max -10) y"));
        Assert.False(matcher.IsMatch("NotchPosition of value: abc (max 1)"));
    }

    // ---------------------------------------------------------------- go to time

    [Fact]
    public void TimeOfDayResolvesWithinTheLog()
    {
        var first = new DateTime(2026, 10, 25, 22, 0, 0);
        var last = new DateTime(2026, 10, 26, 10, 0, 0);

        Assert.True(TimeText.TryResolve("09:30", first, last, out var t1));
        Assert.Equal(new DateTime(2026, 10, 26, 9, 30, 0), t1);

        // 23:15 has not come yet today: it is yesterday's.
        Assert.True(TimeText.TryResolve("23:15", first, last, out var t2));
        Assert.Equal(new DateTime(2026, 10, 25, 23, 15, 0), t2);

        Assert.True(TimeText.TryResolve("2026-10-20 08:00", first, last, out var t3));
        Assert.Equal(new DateTime(2026, 10, 20, 8, 0, 0), t3);

        Assert.False(TimeText.TryResolve("soon", first, last, out _));
    }

    [Fact]
    public void FindsFirstLineAtOrAfter()
    {
        var lines = new[]
        {
            Line("2026-10-26 10:00:00.000000 DEBUG [1-2] Line 1 - a.cpp::f(): a"),
            Line("2026-10-26 10:00:05.000000 DEBUG [1-2] Line 1 - a.cpp::f(): b"),
            Line("2026-10-26 10:00:09.000000 DEBUG [1-2] Line 1 - a.cpp::f(): c"),
        };
        Assert.Equal(1, TimeText.FindFirstAtOrAfter(lines, new DateTime(2026, 10, 26, 10, 0, 3)));
        Assert.Equal(-1, TimeText.FindFirstAtOrAfter(lines, new DateTime(2026, 10, 26, 11, 0, 0)));
    }

    // ---------------------------------------------------------------- alerts

    [Fact]
    public void AlertRulesMatchPatternAndLevel()
    {
        var matcher = new AlertMatcher(
        [
            new AlertRule { Pattern = "Exception", MatchCase = true },
            new AlertRule { MinLevel = AlertLevel.ErrorOrAbove, PlaySound = true },
            new AlertRule { Pattern = "", MinLevel = AlertLevel.Any }, // would fire on everything: ignored
        ]);

        var exception = Line("2026-10-26 10:00:00.000000 DEBUG [1-2] Line 1 - a.cpp::f(): NullReferenceException thrown");
        var error = Line("2026-10-26 10:00:00.000000 ERROR [1-2] Line 1 - a.cpp::f(): boom");
        var normal = Line("2026-10-26 10:00:00.000000 DEBUG [1-2] Line 1 - a.cpp::f(): all good");

        Assert.Equal("Exception", matcher.Match(exception)?.Pattern);
        Assert.Equal(AlertLevel.ErrorOrAbove, matcher.Match(error)?.MinLevel);
        Assert.Null(matcher.Match(normal));
        Assert.Null(matcher.Match(LogLine.Separator("Exception")));
    }

    [Fact]
    public void DisabledOrSilentRulesAreIgnored()
    {
        var matcher = new AlertMatcher(
        [
            new AlertRule { Pattern = "x", Enabled = false },
            new AlertRule { Pattern = "x", ShowNotification = false, PlaySound = false },
        ]);
        Assert.True(matcher.IsEmpty);
    }

    // ---------------------------------------------------------------- device filter (timeline)

    [Fact]
    public void DeviceFilterIncludesAndHides()
    {
        var a = Line("2026-10-26 10:00:00.000000 DEBUG [1-2] Line 1 - a.cpp::f(): a");
        a.Origin = "172.28.130.31";
        var b = Line("2026-10-26 10:00:00.000000 DEBUG [1-2] Line 1 - a.cpp::f(): b");
        b.Origin = "172.28.130.29";

        var only31 = CompiledFilter.Create(new FilterSpec { Device = "130.31" });
        Assert.True(only31.IsMatch(a));
        Assert.False(only31.IsMatch(b));

        var hide31 = CompiledFilter.Create(new FilterSpec { Device = "-130.31" });
        Assert.False(hide31.IsMatch(a));
        Assert.True(hide31.IsMatch(b));

        var rotated = LogLine.Separator("log rotated");
        rotated.Origin = "172.28.130.31";
        Assert.False(hide31.IsMatch(rotated));
    }
}
