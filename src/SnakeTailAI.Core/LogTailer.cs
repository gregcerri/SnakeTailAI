using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;

namespace SnakeTailAI.Core;

public enum TailState
{
    Starting,
    Loading,
    Following,
    WaitingForFile,
    Error,
    Stopped,
}

public sealed class TailerOptions
{
    /// <summary>How much of the end of the active file to read at open. 0 or less reads the whole file.</summary>
    public long InitialBytes { get; init; } = 20L * 1024 * 1024;

    public int PollIntervalMs { get; init; } = 250;

    /// <summary>
    /// When the path starts naming another file, switch to it (active logs). Off for archives such as <c>x.log.3</c>:
    /// their name moves on the next rotation, and the file already opened is the one the user asked for.
    /// </summary>
    public bool FollowRotation { get; init; } = true;

    /// <summary>Files read completely before the active one (e.g. the archives of a series, oldest first).</summary>
    public IReadOnlyList<string> PrefixFiles { get; init; } = [];
}

/// <summary>
/// Follows a log file on a background thread and reports the new lines.
/// The file is opened read-only and shared for read, write and delete, so the program that writes the log
/// can keep writing, rename and rotate it. When the path starts pointing at a different file (rotation),
/// the rest of the old file is read and the new one is followed from its start.
/// </summary>
public sealed class LogTailer : IDisposable
{
    private const int BatchSize = 5000;

    private readonly TailerOptions _options;
    private readonly LogLineParser _parser = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly List<byte> _pending = [];
    private readonly byte[] _buffer = new byte[256 * 1024];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private FileStream? _stream;
    private (uint Volume, ulong Index)? _fileId;
    private long _position;
    private bool _skipPartialLine;
    private bool _firstOpen = true;
    private bool _atFileStart;
    private List<LogLine> _batch = [];
    private long _loadTotal;
    private long _loadDone;

    public LogTailer(string path, TailerOptions? options = null)
    {
        Path = path;
        _options = options ?? new TailerOptions();
    }

    public string Path { get; }
    public TailState State { get; private set; } = TailState.Starting;
    public string? StateMessage { get; private set; }
    public long FileLength { get; private set; }
    public int RotationCount { get; private set; }

    /// <summary>New lines. Raised on the background thread.</summary>
    public event Action<List<LogLine>>? LinesRead;

    public event Action<TailState, string?>? StateChanged;

    /// <summary>Progress of the initial load (0..1). Raised on the background thread.</summary>
    public event Action<double>? LoadProgress;

    public event Action? Rotated;

    public void Start()
    {
        if (_loop != null)
            return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Checks the file now instead of waiting for the next poll.</summary>
    public void Poke() => _wake.Release();

    public void Dispose()
    {
        _cts?.Cancel();
        _wake.Release();
        try
        {
            _loop?.Wait(2000);
        }
        catch (AggregateException)
        {
        }
        CloseStream();
        _cts?.Dispose();
        _wake.Dispose();
    }

    private void Run(CancellationToken ct)
    {
        try
        {
            ReadPrefixFiles(ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Step();
                }
                catch (IOException ex)
                {
                    CloseStream();
                    SetState(TailState.Error, ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    CloseStream();
                    SetState(TailState.Error, ex.Message);
                }
                _wake.Wait(_options.PollIntervalMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        SetState(TailState.Stopped, null);
    }

    private void ReadPrefixFiles(CancellationToken ct)
    {
        if (_options.PrefixFiles.Count == 0)
            return;

        _loadTotal = _options.PrefixFiles.Sum(f => SafeLength(f)) + SafeLength(Path);
        SetState(TailState.Loading, null);
        foreach (string file in _options.PrefixFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var fs = OpenShared(file);
                _batch.Add(LogLine.Separator(System.IO.Path.GetFileName(file)));
                _pending.Clear();
                _parser.Reset();
                _atFileStart = true;
                ReadToEnd(fs, ct, trackPosition: false);
                FlushPending();
                Emit();
            }
            catch (IOException)
            {
                // An archive can disappear while rotating; skip it.
            }
        }
        _batch.Add(LogLine.Separator(System.IO.Path.GetFileName(Path)));
        Emit();
    }

    private void Step()
    {
        if (_stream == null && !Open())
            return;

        ReadToEnd(_stream!, CancellationToken.None, trackPosition: true);

        if (!_options.FollowRotation)
        {
            Emit();
            if (State != TailState.Following)
                SetState(TailState.Following, null);
            return;
        }

        var idAtPath = GetFileId(Path);
        if (idAtPath == null)
        {
            // Renamed away and not created again yet: keep the old handle, it may still get the last writes.
            Emit();
            SetState(TailState.WaitingForFile, "File is being rotated or was removed");
            return;
        }

        if (_fileId != null && idAtPath != _fileId)
        {
            ReadToEnd(_stream!, CancellationToken.None, trackPosition: true);
            FlushPending();
            CloseStream();
            RotationCount++;
            _batch.Add(LogLine.Separator($"log rotated · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
            Emit();
            Rotated?.Invoke();
            if (Open())
                ReadToEnd(_stream!, CancellationToken.None, trackPosition: true);
        }
        else if (_stream!.Length < _position)
        {
            _pending.Clear();
            _parser.Reset();
            _position = 0;
            _stream.Position = 0;
            _atFileStart = true;
            _batch.Add(LogLine.Separator($"file truncated · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
            ReadToEnd(_stream, CancellationToken.None, trackPosition: true);
        }

        Emit();
        if (State != TailState.Following)
            SetState(TailState.Following, null);
    }

    private bool Open()
    {
        try
        {
            _stream = OpenShared(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetState(TailState.WaitingForFile, ex is FileNotFoundException or DirectoryNotFoundException ? "File not found" : ex.Message);
            return false;
        }

        _fileId = GetFileId(_stream.SafeFileHandle);
        long length = _stream.Length;
        long start = 0;
        if (_firstOpen && _options.PrefixFiles.Count == 0 && _options.InitialBytes > 0 && length > _options.InitialBytes)
            start = length - _options.InitialBytes;
        _skipPartialLine = start > 0;
        _atFileStart = start == 0;
        if (_firstOpen && _options.PrefixFiles.Count == 0)
        {
            _loadTotal = length - start;
            if (_loadTotal > 1024 * 1024)
                SetState(TailState.Loading, null);
        }
        _firstOpen = false;
        _position = start;
        _stream.Position = start;
        _pending.Clear();
        _parser.Reset();
        return true;
    }

    private void ReadToEnd(FileStream fs, CancellationToken ct, bool trackPosition)
    {
        int n;
        while ((n = fs.Read(_buffer, 0, _buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (trackPosition)
                _position += n;
            SplitLines(_buffer.AsSpan(0, n));
            if (State == TailState.Loading && _loadTotal > 0)
            {
                _loadDone += n;
                LoadProgress?.Invoke(Math.Min(1.0, (double)_loadDone / _loadTotal));
            }
        }
        if (trackPosition)
            FileLength = fs.Length;
    }

    private void SplitLines(ReadOnlySpan<byte> data)
    {
        if (_atFileStart)
        {
            _atFileStart = false;
            if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                data = data[3..];
        }

        while (true)
        {
            int nl = data.IndexOf((byte)'\n');
            if (nl < 0)
            {
                foreach (byte b in data)
                    _pending.Add(b);
                return;
            }

            ReadOnlySpan<byte> lineBytes;
            byte[]? joined = null;
            if (_pending.Count > 0)
            {
                foreach (byte b in data[..nl])
                    _pending.Add(b);
                joined = _pending.ToArray();
                _pending.Clear();
                lineBytes = joined;
            }
            else
            {
                lineBytes = data[..nl];
            }

            if (_skipPartialLine)
                _skipPartialLine = false;
            else
                AddLine(lineBytes);

            data = data[(nl + 1)..];
        }
    }

    private void AddLine(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 0 && bytes[^1] == '\r')
            bytes = bytes[..^1];
        string text = Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
        var line = _parser.Parse(text);
        if (State == TailState.Following)
            line.ArrivedTicks = Environment.TickCount64;
        _batch.Add(line);
        if (_batch.Count >= BatchSize)
            Emit();
    }

    private void FlushPending()
    {
        if (_pending.Count == 0)
            return;
        var bytes = _pending.ToArray();
        _pending.Clear();
        if (!_skipPartialLine)
            AddLine(bytes);
        _skipPartialLine = false;
    }

    private void Emit()
    {
        if (_batch.Count == 0)
            return;
        var batch = _batch;
        _batch = [];
        LinesRead?.Invoke(batch);
    }

    private void SetState(TailState state, string? message)
    {
        if (State == state && StateMessage == message)
            return;
        State = state;
        StateMessage = message;
        StateChanged?.Invoke(state, message);
    }

    private void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
        _fileId = null;
    }

    private static FileStream OpenShared(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 0,
        });

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // --- File identity (Windows file index), used to notice that the path now names another file. ---

    private static (uint, ulong)? GetFileId(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileId(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (uint, ulong)? GetFileId(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return (0, 0);
        return GetFileInformationByHandle(handle, out var info)
            ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
            : null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);
}
