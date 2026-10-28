using System.Collections.Concurrent;
using System.Text;
using SnakeTailAI.Core;

namespace SnakeTailAI.Tests;

public sealed class TailerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SnakeTailAI.Tests", Guid.NewGuid().ToString("N"));

    public TailerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Line(int i) => $"2026-10-26 10:00:00.{i:D6} DEBUG [1-2] Line {i} - Test.cpp::write(): line {i}";

    /// <summary>Writes like a C runtime logger: shares read/write but not delete, closes before rotating.</summary>
    private static void Write(string path, IEnumerable<int> numbers, int sleepEvery = 50)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        int n = 0;
        foreach (int i in numbers)
        {
            var bytes = Encoding.UTF8.GetBytes(Line(i) + "\r\n");
            fs.Write(bytes);
            fs.Flush();
            if (++n % sleepEvery == 0)
                Thread.Sleep(5);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task FollowsAppendsAndRotationWithoutLosingLines()
    {
        string path = Path.Combine(_dir, "viu.log");
        Write(path, Enumerable.Range(0, 100));

        var lines = new ConcurrentQueue<LogLine>();
        using var tailer = new LogTailer(path, new TailerOptions { PollIntervalMs = 20 });
        tailer.LinesRead += batch => batch.ForEach(lines.Enqueue);
        tailer.Start();

        await WaitUntil(() => lines.Count >= 100);
        Write(path, Enumerable.Range(100, 400));
        await WaitUntil(() => lines.Count >= 500);

        // Rotate like the VIU logger: rename the active file and start a new one.
        File.Move(path, path + ".1");
        Write(path, Enumerable.Range(500, 500));
        await WaitUntil(() => lines.Count(l => !l.IsSeparator) >= 1000);
        await Task.Delay(200);

        var numbers = lines.Where(l => !l.IsSeparator).Select(l => int.Parse(l.SourceLineText)).ToList();
        Assert.Equal(Enumerable.Range(0, 1000), numbers);
        Assert.Contains(lines, l => l.IsSeparator && l.Raw.StartsWith("log rotated"));
        Assert.Equal(1, tailer.RotationCount);
    }

    [Fact]
    public async Task InitialBytesSkipsThePartialFirstLine()
    {
        string path = Path.Combine(_dir, "big.log");
        Write(path, Enumerable.Range(0, 1000), sleepEvery: int.MaxValue);
        long length = new FileInfo(path).Length;

        var lines = new ConcurrentQueue<LogLine>();
        using var tailer = new LogTailer(path, new TailerOptions { PollIntervalMs = 20, InitialBytes = length / 2 });
        tailer.LinesRead += batch => batch.ForEach(lines.Enqueue);
        tailer.Start();
        await WaitUntil(() => tailer.State == TailState.Following);

        var numbers = lines.Select(l => int.Parse(l.SourceLineText)).ToList();
        Assert.Equal(999, numbers[^1]);
        Assert.InRange(numbers.Count, 400, 600);
        Assert.Equal(Enumerable.Range(numbers[0], numbers.Count), numbers);
    }

    [Fact]
    public async Task DoesNotBlockTheWriterFromRotating()
    {
        string path = Path.Combine(_dir, "lock.log");
        Write(path, Enumerable.Range(0, 10));
        using var tailer = new LogTailer(path, new TailerOptions { PollIntervalMs = 20 });
        tailer.Start();
        await WaitUntil(() => tailer.State == TailState.Following);

        // Would throw if the tailer held the file without FileShare.Delete.
        File.Move(path, path + ".1");
        File.Delete(path + ".1");
    }

    [Fact]
    public async Task WaitsForAMissingFile()
    {
        string path = Path.Combine(_dir, "later.log");
        var lines = new ConcurrentQueue<LogLine>();
        using var tailer = new LogTailer(path, new TailerOptions { PollIntervalMs = 20 });
        tailer.LinesRead += batch => batch.ForEach(lines.Enqueue);
        tailer.Start();
        await WaitUntil(() => tailer.State == TailState.WaitingForFile);

        Write(path, Enumerable.Range(0, 5));
        await WaitUntil(() => lines.Count == 5);
    }

    [Fact]
    public async Task LoadsSeriesOldestFirst()
    {
        string path = Path.Combine(_dir, "s.log");
        Write(path + ".2", Enumerable.Range(0, 10));
        Write(path + ".1", Enumerable.Range(10, 10));
        Write(path, Enumerable.Range(20, 10));

        var series = LogSeries.GetSeries(path);
        Assert.Equal([path + ".2", path + ".1", path], series);

        var lines = new ConcurrentQueue<LogLine>();
        using var tailer = new LogTailer(path, new TailerOptions { PollIntervalMs = 20, PrefixFiles = series.Take(series.Count - 1).ToList() });
        tailer.LinesRead += batch => batch.ForEach(lines.Enqueue);
        tailer.Start();
        await WaitUntil(() => tailer.State == TailState.Following);

        Assert.Equal(Enumerable.Range(0, 30), lines.Where(l => !l.IsSeparator).Select(l => int.Parse(l.SourceLineText)));
        Assert.Equal(["s.log.2", "s.log.1", "s.log"], lines.Where(l => l.IsSeparator).Select(l => l.Raw));
    }
}
