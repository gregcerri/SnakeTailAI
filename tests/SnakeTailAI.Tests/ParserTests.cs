using SnakeTailAI.Core;

namespace SnakeTailAI.Tests;

public class ParserTests
{
    private const string Viu =
        "2026-10-26 09:59:59.762166 DEBUG [10-31584] Line 92 - ElabFlowSnussOrienterSide.cpp::run(): End ElabFlowSnussOrienterSide::run";

    [Fact]
    public void ParsesViuLine()
    {
        var line = new LogLineParser().Parse(Viu);

        Assert.Equal(LineKind.Normal, line.Kind);
        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.Equal(new DateTime(2026, 10, 26, 9, 59, 59).AddTicks(7621660), line.Timestamp);
        Assert.Equal("2026-10-26 09:59:59.762166", line.TimeText);
        Assert.Equal("10-31584", line.ThreadText);
        Assert.Equal("92", line.SourceLineText);
        Assert.Equal("ElabFlowSnussOrienterSide.cpp", line.SourceText);
        Assert.Equal("run", line.FunctionText);
        Assert.Equal("End ElabFlowSnussOrienterSide::run", line.MessageText);
    }

    [Theory]
    [InlineData("2026-10-19 14:13:38.955827 WARNING [17-8955] Line 263 - snussComponentLaterale.cpp::elab(): Roi TaxStamp non impostata", LogLevel.Warning)]
    [InlineData("2026-10-19 14:06:26.959252 ERROR [16-8948] Line 359 - snussComponentEtichetta.cpp::elab(): Not enough matches. NMatches = 10", LogLevel.Error)]
    [InlineData("2026-10-19 14:13:38.943504 INFO [13-8939] Line 819 - SnussElabEtichetta.cpp::DetectAndCompute(): Num keypoints: 959", LogLevel.Info)]
    public void ParsesViuLevels(string raw, LogLevel expected)
    {
        Assert.Equal(expected, new LogLineParser().Parse(raw).Level);
    }

    [Fact]
    public void ParsesLog4NetLine()
    {
        var line = new LogLineParser().Parse(
            "2025-11-12 14:33:41,697 [6] INFO  EthLogClientService.OnStart - WorkingDir=C:\\SPECIAL_DEVICES\\ViuLog");

        Assert.Equal(LogLevel.Info, line.Level);
        Assert.Equal("6", line.ThreadText);
        Assert.Equal("EthLogClientService", line.SourceText);
        Assert.Equal("OnStart", line.FunctionText);
        Assert.Equal("WorkingDir=C:\\SPECIAL_DEVICES\\ViuLog", line.MessageText);
        Assert.Equal(new DateTime(2025, 11, 12, 14, 33, 41, 697), line.Timestamp);
    }

    [Fact]
    public void ContinuationInheritsLevelAndTime()
    {
        var parser = new LogLineParser();
        var first = parser.Parse("2026-10-19 14:06:26.959252 ERROR [16-8948] Line 359 - a.cpp::elab(): boom");
        var cont = parser.Parse("   at some.stack.frame()");

        Assert.Equal(LineKind.Continuation, cont.Kind);
        Assert.Equal(LogLevel.Error, cont.Level);
        Assert.Equal(first.TimestampTicks, cont.TimestampTicks);
        Assert.Equal("   at some.stack.frame()", cont.MessageText);
    }

    [Fact]
    public void FindsLevelWordWithoutTimestamp()
    {
        var line = new LogLineParser().Parse("[WARN] disk almost full");
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal(LineKind.Normal, line.Kind);
    }

    [Fact]
    public void FunctionWithEmptyMessage()
    {
        var line = new LogLineParser().Parse("2026-10-26 09:59:59.762166 DEBUG [1-2] Line 5 - x.cpp::go():");
        Assert.Equal("go", line.FunctionText);
        Assert.Equal("", line.MessageText);
    }

    [Theory]
    [InlineData("2026-13-01 10:00:00 INFO x")]
    [InlineData("hello world")]
    [InlineData("")]
    public void RejectsBadTimestamps(string raw)
    {
        Assert.False(LogLineParser.TryParseTimestamp(raw, out _, out _));
    }

    [Fact]
    public void ExpandsTabs()
    {
        var line = new LogLineParser().Parse("a\tb");
        Assert.Equal("a    b", line.Raw);
    }
}
