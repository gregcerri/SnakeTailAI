using SnakeTailAI.Core;

namespace SnakeTailAI.Tests;

public class DocumentTests
{
    private static List<LogLine> Make(int count, int start = 0)
    {
        var parser = new LogLineParser();
        return Enumerable.Range(start, count)
            .Select(i => parser.Parse($"2026-10-26 10:00:00.000000 {(i % 10 == 0 ? "ERROR" : "DEBUG")} [1-2] Line {i} - T.cpp::f(): item {i}"))
            .ToList();
    }

    [Fact]
    public void AppendNumbersLinesAndCounts()
    {
        var doc = new LogDocument();
        var result = doc.Append(Make(100));

        Assert.Equal(100, result.Added);
        Assert.Equal(10, result.ErrorsAdded);
        Assert.Equal(100, doc.View.Count);
        Assert.Equal(1, doc.View[0].Number);
        Assert.Equal(10, doc.LevelCounts[3]);
        Assert.Equal(90, doc.LevelCounts[0]);
        Assert.Equal(10, doc.ErrorMarks.Count);
    }

    [Fact]
    public async Task FilterThenAppendKeepsFiltering()
    {
        var doc = new LogDocument();
        doc.Append(Make(100));
        await doc.ApplyFilterAsync(CompiledFilter.Create(new FilterSpec { Levels = LevelMask.Error }));
        Assert.Equal(10, doc.View.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i), doc.ErrorMarks);

        var result = doc.Append(Make(20, 100));
        Assert.Equal(2, result.ViewAdded);
        Assert.Equal(12, doc.View.Count);
    }

    [Fact]
    public void ClearOnlyEmptiesMemoryAndKeepsNumbering()
    {
        var doc = new LogDocument();
        doc.Append(Make(50));
        doc.Clear();
        Assert.Empty(doc.View);
        Assert.Empty(doc.All);

        doc.Append(Make(5));
        Assert.Equal(51, doc.View[0].Number);
    }

    [Fact]
    public void TrimDropsOldestLines()
    {
        var doc = new LogDocument { MaxLines = 100 };
        int resets = 0;
        doc.ViewReset += (_, _) => resets++;
        doc.Append(Make(200));

        Assert.Equal(100, doc.All.Count);
        Assert.Equal(101, doc.All[0].Number);
        Assert.Equal(1, resets);
    }

    [Fact]
    public void FindViewIndexAndBookmarks()
    {
        var doc = new LogDocument();
        doc.Append(Make(30));
        Assert.Equal(4, doc.FindViewIndex(5));

        doc.ToggleBookmark(doc.View[7]);
        doc.ToggleBookmark(doc.View[3]);
        Assert.Equal([3, 7], doc.BookmarkMarks);
        doc.ToggleBookmark(doc.View[7]);
        Assert.Equal([3], doc.BookmarkMarks);
    }

    [Fact]
    public void ChunkedListGrowsPastOneChunk()
    {
        var list = new ChunkedList<int>();
        for (int i = 0; i < 100_000; i++)
            list.Add(i);
        Assert.Equal(100_000, list.Count);
        Assert.Equal(65_537, list[65_537]);
        Assert.Equal(99_999, list.Last());
    }

    [Fact]
    public void HighlighterSplitsLineAndSpanRules()
    {
        var highlighter = new Highlighter(
        [
            new HighlightRule { Pattern = "item", Foreground = "#FF0000" },
            new HighlightRule { Pattern = "ERROR", WholeLine = true, Background = "#330000" },
            new HighlightRule { Pattern = "(", IsRegex = true },
        ]);
        var (lineRule, spans) = highlighter.Evaluate("ERROR item 1 item 2");
        Assert.Equal("ERROR", lineRule?.Pattern);
        Assert.Equal(2, spans?.Count);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), "SnakeTailAI.Tests", Guid.NewGuid() + ".json");
        var settings = new AppSettings { FontSize = 15 };
        settings.FilterPresets.Add(new FilterPreset { Name = "errors", Filter = new FilterSpec { Levels = LevelMask.Error, Include = "x" } });
        settings.MarkDeviceUsed(@"C:\ViuLog\ViuPC-1");
        SettingsStore.Save(settings, path);

        var loaded = SettingsStore.Load(path);
        Assert.Equal(15, loaded.FontSize);
        Assert.Equal(LevelMask.Error, loaded.FilterPresets[0].Filter.Levels);
        Assert.Equal(settings.HighlightRules.Count, loaded.HighlightRules.Count);
        Assert.True(loaded.DeviceLastUsed.ContainsKey(@"c:\viulog\viupc-1"));
        File.Delete(path);
    }
}
