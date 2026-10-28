using SnakeTailAI.Core;

namespace SnakeTailAI.Tests;

public class FilterTests
{
    private static readonly string[] Sample =
    [
        "2026-10-26 09:59:59.762166 DEBUG [10-31584] Line 92 - ElabFlowSnussOrienterSide.cpp::run(): End run",
        "2026-10-26 10:00:00.077470 DEBUG [0-31606] Line 35 - Acquisition.cpp::acquisition_OnImageAcquired(): acquisition_OnImageAcquired",
        "2026-10-26 10:00:01.000000 WARNING [17-8955] Line 263 - snussComponentLaterale.cpp::elab(): Roi TaxStamp non impostata",
        "2026-10-26 10:00:02.000000 ERROR [16-8948] Line 359 - snussComponentEtichetta.cpp::elab(): Not enough matches. NMatches = 10",
        "2026-10-26 10:00:03.000000 INFO [13-8939] Line 819 - SnussElabEtichetta.cpp::DetectAndCompute(): Num keypoints: 959",
    ];

    private static List<LogLine> Lines()
    {
        var parser = new LogLineParser();
        return Sample.Select(parser.Parse).ToList();
    }

    private static int[] Matching(FilterSpec spec)
    {
        var filter = CompiledFilter.Create(spec);
        return Lines().Select((l, i) => (l, i)).Where(x => filter.IsMatch(x.l)).Select(x => x.i).ToArray();
    }

    [Fact]
    public void EmptyFilterPassesAll() => Assert.Equal([0, 1, 2, 3, 4], Matching(new FilterSpec()));

    [Fact]
    public void IncludeIsCaseInsensitiveByDefault() => Assert.Equal([1], Matching(new FilterSpec { Include = "ACQUISITION" }));

    [Fact]
    public void IncludeTermsAreOred() => Assert.Equal([1, 3], Matching(new FilterSpec { Include = "acquisition; NMatches" }));

    [Fact]
    public void MatchCase() => Assert.Empty(Matching(new FilterSpec { Include = "ACQUISITION", MatchCase = true }));

    [Fact]
    public void Exclude() => Assert.Equal([0, 2, 4], Matching(new FilterSpec { Exclude = "acquisition;NMatches" }));

    [Fact]
    public void Regex() => Assert.Equal([3, 4], Matching(new FilterSpec { Include = @"(NMatches|keypoints): ?\d+|= \d+", IsRegex = true }));

    [Fact]
    public void WholeWord() => Assert.Equal([0], Matching(new FilterSpec { Include = "run", WholeWord = true, Levels = LevelMask.Debug }));

    [Fact]
    public void Levels() => Assert.Equal([2, 3], Matching(new FilterSpec { Levels = LevelMask.Warning | LevelMask.Error }));

    [Fact]
    public void ThreadAndSource()
    {
        Assert.Equal([3], Matching(new FilterSpec { Thread = "16-8948" }));
        Assert.Equal([2, 3], Matching(new FilterSpec { Source = "snussComponent" }));
        Assert.Equal([4], Matching(new FilterSpec { Source = "DetectAndCompute" }));
    }

    [Fact]
    public void TimeOfDayRange() => Assert.Equal([2, 3], Matching(new FilterSpec { TimeFrom = "10:00:01", TimeTo = "10:00:02" }));

    [Fact]
    public void FullDateRange() => Assert.Equal([1, 2, 3, 4], Matching(new FilterSpec { TimeFrom = "2026-10-26 10:00" }));

    [Fact]
    public void InvalidRegexThrows() => Assert.ThrowsAny<ArgumentException>(() => CompiledFilter.Create(new FilterSpec { Include = "(", IsRegex = true }));

    [Fact]
    public void InvalidTimeThrows() => Assert.Throws<ArgumentException>(() => CompiledFilter.Create(new FilterSpec { TimeFrom = "yesterday" }));

    [Fact]
    public void SeparatorsAlwaysPass()
    {
        var filter = CompiledFilter.Create(new FilterSpec { Include = "nothing-matches-this" });
        Assert.True(filter.IsMatch(LogLine.Separator("log rotated")));
    }

    [Fact]
    public void MatcherReturnsRanges()
    {
        var matcher = TextMatcher.Create("ab;cd", false, false, false)!;
        Assert.Equal([(0, 2), (2, 2), (6, 2)], matcher.Matches("abcd  AB"));
    }
}
