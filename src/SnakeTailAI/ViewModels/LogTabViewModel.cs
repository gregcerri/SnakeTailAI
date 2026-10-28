using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SnakeTailAI.Controls;
using SnakeTailAI.Core;

namespace SnakeTailAI.ViewModels;

/// <summary>One open log: the tailer that reads it, the lines held in memory, and the filter / find / view state of its tab.</summary>
public sealed partial class LogTabViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> FilterProperties =
    [
        nameof(Include), nameof(Exclude), nameof(IsRegex), nameof(MatchCase), nameof(WholeWord), nameof(Thread), nameof(Source),
        nameof(TimeFrom), nameof(TimeTo), nameof(ShowDebug), nameof(ShowInfo), nameof(ShowWarning), nameof(ShowError), nameof(ShowOther),
        nameof(Device),
    ];

    private readonly AppSettings _settings;
    private readonly ConcurrentQueue<List<LogLine>> _incoming = new();
    private readonly DispatcherTimer _drainTimer;
    private readonly DispatcherTimer _secondTimer;
    private readonly DispatcherTimer _filterTimer;
    private readonly DispatcherTimer _findTimer;
    private readonly DispatcherTimer _groupsTimer;
    private readonly double[] _rate = new double[60];
    private readonly List<TailSource> _sources = [];
    private readonly List<LogLine> _holdBack = [];
    private bool _holdingBack;
    private bool _flushing;
    private long _startTicks;
    private int _viewVersion;
    private int _groupsVersion = -1;
    private CancellationTokenSource? _groupsCts;
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _findCts;
    private List<int> _findMarks = [];
    private int _linesThisSecond;
    private long _lastLineTicks;
    private bool _suppressFilter;
    private bool _cleared;
    private bool _disposed;

    public LogTabViewModel(string path, bool isSeries, AppSettings settings, Highlighter highlighter, FilterSpec? filter = null)
        : this([path], isSeries, settings, highlighter, filter)
    {
    }

    /// <summary>With several paths the tab is a timeline: their lines merged in time order, each marked with its device.</summary>
    public LogTabViewModel(IReadOnlyList<string> paths, bool isSeries, AppSettings settings, Highlighter highlighter, FilterSpec? filter = null)
    {
        Paths = paths;
        Path = paths[0];
        IsSeries = isSeries && paths.Count == 1;
        _settings = settings;
        _highlighter = highlighter;
        _columnsMode = settings.ColumnsMode;
        _wordWrap = settings.WordWrap;
        _fontSize = settings.FontSize;
        Document.MaxLines = settings.MaxLines;
        Document.Appended += OnDocumentAppended;
        Document.ViewReset += OnDocumentReset;
        if (IsMerged)
            Document.OriginWidth = paths.Max(p => Format.DeviceName(p).Length);

        _drainTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _drainTimer.Tick += (_, _) => Drain();
        _secondTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _secondTimer.Tick += (_, _) => EverySecond();
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _filterTimer.Tick += async (_, _) =>
        {
            _filterTimer.Stop();
            await ApplyFilterNowAsync();
        };
        _findTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _findTimer.Tick += async (_, _) =>
        {
            _findTimer.Stop();
            await RunFindAsync(jump: true);
        };
        _groupsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _groupsTimer.Tick += async (_, _) =>
        {
            if (_groupsVersion != _viewVersion)
                await RefreshGroupsAsync();
        };

        if (filter != null)
            SetFilterFields(filter);
    }

    /// <summary>The (first) log of the tab.</summary>
    public string Path { get; }

    /// <summary>All the logs of the tab: one, or several for a timeline.</summary>
    public IReadOnlyList<string> Paths { get; }

    public bool IsSeries { get; }
    public bool IsMerged => Paths.Count > 1;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Title => IsMerged ? $"Timeline · {Paths.Count} devices" : IsSeries ? FileName + " · series" : FileName;
    public string FolderName => IsMerged
        ? string.Join(", ", Paths.Select(Format.DeviceName))
        : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path) ?? "");
    public string ToolTip => string.Join(Environment.NewLine, Paths);

    /// <summary>The device next to the tab title, when the file name does not already say it.</summary>
    public string HeaderNote => IsMerged || FileName.Contains(Format.DeviceName(Path), StringComparison.OrdinalIgnoreCase)
        ? ""
        : Format.DeviceName(Path);
    public ObservableCollection<MessageGroup> Groups { get; } = [];
    public LogDocument Document { get; } = new();
    public IReadOnlyList<double> RateHistory => _rate;
    public IReadOnlyList<int> FindMarks => _findMarks;

    /// <summary>Set by the view, so commands can move the selection and scroll.</summary>
    public ILogViewport? Viewport { get; set; }

    /// <summary>A short message for a toast (file rotated, export done, …).</summary>
    public event EventHandler<string>? Notify;

    /// <summary>The view should focus a box: "find" or "filter".</summary>
    public event EventHandler<string>? FocusRequested;

    /// <summary>Lines that just arrived from a live tail (not the ones loaded at open). Used by alerts.</summary>
    public event EventHandler<IReadOnlyList<LogLine>>? LiveLinesArrived;

    // ---------------------------------------------------------------- state

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private TailState _state = TailState.Starting;
    [ObservableProperty] private string _stateText = "Starting…";
    [ObservableProperty] private string _statusKind = "idle";
    [ObservableProperty] private string _emptyText = "Opening…";
    [ObservableProperty] private double _loadProgress;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private long _fileSize;
    [ObservableProperty] private int _rotations;
    [ObservableProperty] private int _totalLines;
    [ObservableProperty] private int _shownLines;
    [ObservableProperty] private double _linesPerSecond;
    /// <summary>Lines arrived in the last moments (the status dot pulses).</summary>
    [ObservableProperty] private bool _isReceiving;

    /// <summary>The log was written recently; same rule as the green dot on the device cards.</summary>
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private int _unseenErrors;
    [ObservableProperty] private int _rateVersion;
    [ObservableProperty] private int _marksVersion;

    [ObservableProperty] private bool _isFollowing = true;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private int _pendingWhilePaused;

    [ObservableProperty] private bool _columnsMode;
    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private Highlighter _highlighter;
    [ObservableProperty] private LogLine? _selectedLine;
    [ObservableProperty] private LogLine? _contextLine;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private bool _showGroups;
    [ObservableProperty] private bool _groupsProblemsOnly;
    [ObservableProperty] private string _groupsStatus = "";
    [ObservableProperty] private bool _isGrouping;
    [ObservableProperty] private MessageGroup? _selectedGroup;

    // ---------------------------------------------------------------- filter fields

    [ObservableProperty] private string _include = "";
    [ObservableProperty] private string _exclude = "";
    [ObservableProperty] private bool _isRegex;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private string _thread = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _timeFrom = "";
    [ObservableProperty] private string _timeTo = "";
    [ObservableProperty] private string _device = "";
    [ObservableProperty] private bool _showDebug = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showWarning = true;
    [ObservableProperty] private bool _showError = true;
    [ObservableProperty] private bool _showOther = true;
    [ObservableProperty] private string? _filterError;
    [ObservableProperty] private bool _isFilterActive;
    [ObservableProperty] private bool _isFiltering;
    [ObservableProperty] private bool _showAdvancedFilter;

    [ObservableProperty] private int _debugCount;
    [ObservableProperty] private int _infoCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _otherCount;

    // ---------------------------------------------------------------- find fields

    [ObservableProperty] private bool _isFindOpen;
    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private bool _findRegex;
    [ObservableProperty] private bool _findMatchCase;
    [ObservableProperty] private string _findStatus = "";
    [ObservableProperty] private TextMatcher? _findMatcher;

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
            UnseenErrors = 0;
    }

    partial void OnIsPausedChanged(bool value)
    {
        PendingWhilePaused = 0;
        UpdateStatus();
    }

    partial void OnFontSizeChanged(double value) => _settings.FontSize = value;
    partial void OnColumnsModeChanged(bool value) => _settings.ColumnsMode = value;
    partial void OnWordWrapChanged(bool value) => _settings.WordWrap = value;
    partial void OnFindTextChanged(string value) => RestartTimer(_findTimer);
    partial void OnFindRegexChanged(bool value) => RestartTimer(_findTimer);
    partial void OnFindMatchCaseChanged(bool value) => RestartTimer(_findTimer);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_suppressFilter && e.PropertyName != null && FilterProperties.Contains(e.PropertyName))
            RestartTimer(_filterTimer);
    }

    private static void RestartTimer(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    // ---------------------------------------------------------------- tailing

    /// <summary>One followed file of the tab.</summary>
    private sealed class TailSource(string path, string? origin, LogTailer tailer)
    {
        public string Path { get; } = path;
        public string? Origin { get; } = origin;
        public LogTailer Tailer { get; } = tailer;
        public FileSystemWatcher? Watcher { get; set; }
        public TailState State { get; set; } = TailState.Starting;
        public string? Message { get; set; }
        public double Progress;

        public bool IsReady => State is not (TailState.Starting or TailState.Loading);

        public void Dispose()
        {
            Watcher?.Dispose();
            Tailer.Dispose();
        }
    }

    public void Start()
    {
        _startTicks = Environment.TickCount64;
        _holdingBack = IsMerged;
        foreach (string path in Paths)
        {
            var series = IsSeries ? LogSeries.GetSeries(path) : [path];
            bool isArchive = LogSeries.IsArchiveName(System.IO.Path.GetFileName(path), out _, out _);
            var tailer = new LogTailer(path, new TailerOptions
            {
                InitialBytes = _settings.InitialLoadMB <= 0 ? 0 : _settings.InitialLoadMB * 1024L * 1024L,
                PollIntervalMs = Math.Clamp(_settings.PollIntervalMs, 50, 5000),
                PrefixFiles = series.Take(series.Count - 1).ToList(),
                FollowRotation = !isArchive,
            });
            var source = new TailSource(path, IsMerged ? Format.DeviceName(path) : null, tailer);
            tailer.LinesRead += batch =>
            {
                if (source.Origin != null)
                {
                    foreach (var line in batch)
                        line.Origin = source.Origin;
                }
                _incoming.Enqueue(batch);
            };
            tailer.LoadProgress += progress => Volatile.Write(ref source.Progress, progress);
            tailer.StateChanged += (state, message) => Application.Current?.Dispatcher.BeginInvoke(() => OnTailState(source, state, message));
            string name = source.Origin ?? System.IO.Path.GetFileName(path);
            tailer.Rotated += () => Application.Current?.Dispatcher.BeginInvoke(() => Notify?.Invoke(this, $"{name} rotated — following the new file"));
            source.Watcher = StartWatcher(path, tailer);
            _sources.Add(source);
            tailer.Start();
        }
        _drainTimer.Start();
        _secondTimer.Start();
    }

    private static FileSystemWatcher? StartWatcher(string path, LogTailer tailer)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (dir == null || !Directory.Exists(dir))
                return null;
            var watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            watcher.Changed += (_, _) => tailer.Poke();
            watcher.Created += (_, _) => tailer.Poke();
            watcher.Renamed += (_, _) => tailer.Poke();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Polling still works without the watcher.
            return null;
        }
    }

    private void StopTailer()
    {
        foreach (var source in _sources)
            source.Dispose();
        _sources.Clear();
        _holdBack.Clear();
        while (_incoming.TryDequeue(out _))
        {
        }
    }

    private void OnTailState(TailSource source, TailState state, string? message)
    {
        if (_disposed)
            return;
        source.State = state;
        source.Message = message;
        if (state == TailState.WaitingForFile && message == "File not found")
            Notify?.Invoke(this, $"{source.Origin ?? System.IO.Path.GetFileName(source.Path)}: file not found — waiting for it to appear");
        State = CombinedState(out string? combinedMessage);
        UpdateStatus(combinedMessage);
    }

    /// <summary>The state of the tab: loading while any file loads; missing or failing only when all are.</summary>
    private TailState CombinedState(out string? message)
    {
        message = _sources.Select(s => s.Message).FirstOrDefault(m => m != null);
        if (_sources.Count == 0)
            return TailState.Stopped;
        if (_sources.Any(s => s.State == TailState.Loading))
            return TailState.Loading;
        if (_sources.All(s => s.State == TailState.Starting))
            return TailState.Starting;
        if (_sources.All(s => s.State == TailState.WaitingForFile))
            return TailState.WaitingForFile;
        if (_sources.All(s => s.State == TailState.Error))
            return TailState.Error;
        int problems = _sources.Count(s => s.State is TailState.WaitingForFile or TailState.Error);
        message = problems > 0 ? $"{problems} of {_sources.Count} files not readable" : null;
        return TailState.Following;
    }

    private void UpdateStatus(string? message = null)
    {
        IsLoading = State == TailState.Loading;
        (StatusKind, StateText) = State switch
        {
            TailState.Loading => ("loading", $"Loading {LoadProgress:P0}"),
            TailState.WaitingForFile => ("missing", message ?? "Waiting for file…"),
            TailState.Error => ("error", "Error: " + (message ?? "")),
            TailState.Stopped => ("idle", "Stopped"),
            _ when IsPaused => ("paused", "Paused"),
            _ when IsLive => ("live", (IsFollowing ? "Following · live" : "Live · scrolled up") + Suffix(message)),
            _ => ("idle", (IsFollowing ? "Following · no recent writes" : "Scrolled up · no recent writes") + Suffix(message)),
        };
        UpdateEmptyText();
    }

    private static string Suffix(string? message) => string.IsNullOrEmpty(message) ? "" : " · " + message;

    private void UpdateEmptyText()
    {
        EmptyText = State switch
        {
            TailState.Starting or TailState.Loading => "Loading…",
            TailState.WaitingForFile => "Waiting for the file to appear…",
            TailState.Error => "Cannot read the file — retrying",
            _ when IsFilterActive && TotalLines > 0 => "No lines match the filter",
            _ when TotalLines == 0 && Document.All.Count == 0 && _cleared => "Screen cleared — new lines will appear here",
            _ => "Waiting for new lines…",
        };
    }

    partial void OnIsFollowingChanged(bool value) => UpdateStatus();

    private void Drain()
    {
        if (_holdingBack && !_flushing && _incoming.IsEmpty &&
            (_sources.All(s => s.IsReady) || Environment.TickCount64 - _startTicks > 30_000))
        {
            _ = FlushHoldBackAsync();
            return;
        }
        if (_incoming.IsEmpty)
            return;

        var merged = new List<LogLine>();
        while (merged.Count < 200_000 && _incoming.TryDequeue(out var batch))
            merged.AddRange(batch);

        if (IsMerged)
        {
            if (_holdingBack)
            {
                // Collect everything loaded at open, then show it once, sorted by time across the devices.
                _holdBack.AddRange(merged);
                bool ready = _sources.All(s => s.IsReady) || Environment.TickCount64 - _startTicks > 30_000;
                if (ready && !_flushing)
                    _ = FlushHoldBackAsync();
                return;
            }
            merged = SortByTime(merged);
        }
        AppendLines(merged);
    }

    private async Task FlushHoldBackAsync()
    {
        _flushing = true;
        var lines = _holdBack.ToList();
        _holdBack.Clear();
        var sorted = await Task.Run(() => SortByTime(lines));
        _holdingBack = false;
        _flushing = false;
        if (!_disposed)
            AppendLines(sorted);
    }

    /// <summary>
    /// Stable sort by timestamp. Lines without one (continuations, markers) keep the time of the line before them
    /// from the same device, so they stay attached to it.
    /// </summary>
    private static List<LogLine> SortByTime(List<LogLine> lines)
    {
        var keys = new long[lines.Count];
        var lastByOrigin = new Dictionary<string, long>();
        for (int i = 0; i < lines.Count; i++)
        {
            string origin = lines[i].Origin ?? "";
            long ticks = lines[i].IsSeparator ? 0 : lines[i].TimestampTicks;
            if (ticks == 0)
                ticks = lastByOrigin.GetValueOrDefault(origin);
            else
                lastByOrigin[origin] = ticks;
            keys[i] = ticks;
        }
        return Enumerable.Range(0, lines.Count).OrderBy(i => keys[i]).Select(i => lines[i]).ToList();
    }

    private void AppendLines(List<LogLine> merged)
    {
        if (merged.Count == 0)
            return;
        int live = 0, liveErrors = 0;
        foreach (var line in merged)
        {
            if (line.ArrivedTicks == 0)
                continue;
            live++;
            if (line.Level >= LogLevel.Error)
                liveErrors++;
        }

        var result = Document.Append(merged);
        if (live > 0)
        {
            _linesThisSecond += live;
            _lastLineTicks = Environment.TickCount64;
            if (!IsReceiving || !IsLive)
            {
                IsReceiving = true;
                IsLive = true;
                UpdateStatus();
            }
        }
        if (!IsActive)
            UnseenErrors += liveErrors;
        if (IsPaused)
            PendingWhilePaused += result.ViewAdded;
        UpdateCounts();
        if (live > 0 && LiveLinesArrived != null)
            LiveLinesArrived.Invoke(this, merged.Where(l => l.ArrivedTicks > 0).ToList());
    }

    private void EverySecond()
    {
        Array.Copy(_rate, 1, _rate, 0, _rate.Length - 1);
        _rate[^1] = _linesThisSecond;
        LinesPerSecond = _linesThisSecond;
        _linesThisSecond = 0;
        RateVersion++;

        long sinceLastLine = Environment.TickCount64 - _lastLineTicks;
        IsReceiving = sinceLastLine < 1500;
        IsLive = sinceLastLine < Liveness.Window.TotalMilliseconds || WrittenRecently();
        FileSize = _sources.Sum(s => s.Tailer.FileLength);
        Rotations = _sources.Sum(s => s.Tailer.RotationCount);
        if (State == TailState.Loading)
            LoadProgress = CombinedProgress();
        State = CombinedState(out string? message);
        UpdateStatus(message);
    }

    private double CombinedProgress() =>
        _sources.Count == 0 ? 0 : _sources.Average(s => s.IsReady ? 1.0 : Volatile.Read(ref s.Progress));

    private bool WrittenRecently()
    {
        foreach (string path in Paths)
        {
            try
            {
                if (DateTime.Now - File.GetLastWriteTime(path) < Liveness.Window)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return false;
    }

    private void UpdateCounts()
    {
        TotalLines = Document.All.Count;
        ShownLines = Document.View.Count;
        var c = Document.LevelCounts;
        DebugCount = c[0];
        InfoCount = c[1];
        WarningCount = c[2];
        ErrorCount = c[3];
        OtherCount = c[4];
        UpdateEmptyText();
        if (State == TailState.Loading)
        {
            LoadProgress = CombinedProgress();
            UpdateStatus();
        }
    }

    private void OnDocumentAppended(object? sender, AppendResult e)
    {
        _viewVersion++;
        if (FindMatcher == null || e.ViewAdded == 0)
            return;
        var view = Document.View;
        bool added = false;
        for (int i = view.Count - e.ViewAdded; i < view.Count; i++)
        {
            if (!view[i].IsSeparator && FindMatcher.IsMatch(view[i].Raw))
            {
                _findMarks.Add(i);
                added = true;
            }
        }
        if (added)
            MarksVersion++;
    }

    private async void OnDocumentReset(object? sender, EventArgs e)
    {
        _viewVersion++;
        UpdateCounts();
        MarksVersion++;
        if (FindMatcher != null)
            await RunFindAsync(jump: false);
    }

    // ---------------------------------------------------------------- commands: view

    /// <summary>Empties the screen only. The log file on disk is never modified; Reload brings the lines back.</summary>
    [RelayCommand]
    public void Clear()
    {
        _cleared = true;
        Document.Clear();
        _findMarks = [];
        PendingWhilePaused = 0;
        UnseenErrors = 0;
        SelectedLine = null;
        MarksVersion++;
        OnPropertyChanged(nameof(FindMarks));
        UpdateCounts();
    }

    /// <summary>Reads the file again from the start (or the last N MB), as when it was opened.</summary>
    [RelayCommand]
    public void Reload()
    {
        StopTailer();
        Clear();
        _cleared = false;
        Start();
    }

    [RelayCommand]
    public void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    public void FollowTail()
    {
        IsPaused = false;
        IsFollowing = true;
        Viewport?.ScrollToEnd();
    }

    [RelayCommand]
    public void ToggleDetails() => ShowDetails = !ShowDetails;

    // ---------------------------------------------------------------- commands: filter

    public FilterSpec CurrentSpec() => new()
    {
        Include = Include,
        Exclude = Exclude,
        IsRegex = IsRegex,
        MatchCase = MatchCase,
        WholeWord = WholeWord,
        Thread = Thread,
        Source = Source,
        TimeFrom = TimeFrom,
        TimeTo = TimeTo,
        Device = Device,
        Levels = (ShowDebug ? LevelMask.Debug : 0) | (ShowInfo ? LevelMask.Info : 0) | (ShowWarning ? LevelMask.Warning : 0) |
                 (ShowError ? LevelMask.Error : 0) | (ShowOther ? LevelMask.Other : 0),
    };

    private void SetFilterFields(FilterSpec spec)
    {
        _suppressFilter = true;
        Include = spec.Include;
        Exclude = spec.Exclude;
        IsRegex = spec.IsRegex;
        MatchCase = spec.MatchCase;
        WholeWord = spec.WholeWord;
        Thread = spec.Thread;
        Source = spec.Source;
        TimeFrom = spec.TimeFrom;
        TimeTo = spec.TimeTo;
        Device = spec.Device;
        ShowDebug = spec.Levels.HasFlag(LevelMask.Debug);
        ShowInfo = spec.Levels.HasFlag(LevelMask.Info);
        ShowWarning = spec.Levels.HasFlag(LevelMask.Warning);
        ShowError = spec.Levels.HasFlag(LevelMask.Error);
        ShowOther = spec.Levels.HasFlag(LevelMask.Other);
        ShowAdvancedFilter |= !string.IsNullOrEmpty(spec.Thread) || !string.IsNullOrEmpty(spec.Source) ||
                              !string.IsNullOrEmpty(spec.TimeFrom) || !string.IsNullOrEmpty(spec.TimeTo) ||
                              !string.IsNullOrEmpty(spec.Device);
        _suppressFilter = false;
    }

    public async Task ApplyFilterAsync(FilterSpec spec)
    {
        SetFilterFields(spec);
        await ApplyFilterNowAsync();
    }

    private async Task ApplyFilterNowAsync()
    {
        _filterTimer.Stop();
        var spec = CurrentSpec();
        CompiledFilter compiled;
        try
        {
            compiled = CompiledFilter.Create(spec);
            FilterError = null;
        }
        catch (ArgumentException ex)
        {
            FilterError = ex.Message;
            return;
        }

        IsFilterActive = !spec.IsEmpty;
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        IsFiltering = true;
        try
        {
            await Document.ApplyFilterAsync(compiled, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (cts == _filterCts)
                IsFiltering = false;
        }
    }

    [RelayCommand]
    public async Task ClearFilter() => await ApplyFilterAsync(FilterSpec.Empty);

    [RelayCommand]
    private void ToggleAdvancedFilter() => ShowAdvancedFilter = !ShowAdvancedFilter;

    [RelayCommand]
    private void OnlyThread(LogLine? line)
    {
        if (line is { ThreadLength: > 0 })
        {
            Thread = line.ThreadText;
            ShowAdvancedFilter = true;
        }
    }

    [RelayCommand]
    private void ExcludeThread(LogLine? line)
    {
        if (line is { ThreadLength: > 0 })
            Exclude = AppendTerm(Exclude, "[" + line.ThreadText + "]");
    }

    [RelayCommand]
    private void OnlySource(LogLine? line)
    {
        if (line is { SourceLength: > 0 })
        {
            Source = line.SourceText;
            ShowAdvancedFilter = true;
        }
    }

    [RelayCommand]
    private void ExcludeSource(LogLine? line)
    {
        if (line is { SourceLength: > 0 })
            Exclude = AppendTerm(Exclude, line.SourceText);
    }

    [RelayCommand]
    private void OnlyFunction(LogLine? line)
    {
        if (line is { FunctionLength: > 0 })
        {
            Source = line.FunctionText;
            ShowAdvancedFilter = true;
        }
    }

    [RelayCommand]
    private void OnlyDevice(LogLine? line)
    {
        if (line?.Origin != null)
        {
            Device = line.Origin;
            ShowAdvancedFilter = true;
        }
    }

    [RelayCommand]
    private void HideDevice(LogLine? line)
    {
        if (line?.Origin != null)
        {
            Device = AppendTerm(Device, "-" + line.Origin);
            ShowAdvancedFilter = true;
        }
    }

    [RelayCommand]
    private void OnlyLevel(LogLine? line)
    {
        if (line == null)
            return;
        var mask = line.Level.ToMask();
        _suppressFilter = true;
        ShowDebug = mask == LevelMask.Debug;
        ShowInfo = mask == LevelMask.Info;
        ShowWarning = mask == LevelMask.Warning;
        ShowError = mask == LevelMask.Error;
        _suppressFilter = false;
        ShowOther = mask == LevelMask.Other;
    }

    [RelayCommand]
    private void IncludeMessage(LogLine? line)
    {
        string message = line?.MessageText.Trim() ?? "";
        if (message.Length == 0)
            return;
        if (message.Length > 60)
            message = message[..60];
        IsRegex = false;
        Include = message.Replace(";", "");
    }

    /// <summary>Removes the filter and shows the line among its neighbours.</summary>
    [RelayCommand]
    private async Task ShowInContext(LogLine? line)
    {
        line ??= SelectedLine;
        if (line == null)
            return;
        IsPaused = false;
        IsFollowing = false;
        await ApplyFilterAsync(FilterSpec.Empty);
        int index = Document.FindViewIndex(line.Number);
        if (index >= 0)
            Viewport?.ScrollToIndex(index, select: true);
    }

    private static string AppendTerm(string terms, string term) =>
        string.IsNullOrWhiteSpace(terms) ? term : terms.TrimEnd().TrimEnd(';') + "; " + term;

    // ---------------------------------------------------------------- commands: find

    [RelayCommand]
    public void OpenFind()
    {
        IsFindOpen = true;
        FocusRequested?.Invoke(this, "find");
    }

    [RelayCommand]
    private void CloseFind()
    {
        IsFindOpen = false;
        FindText = "";
        Viewport?.FocusView();
    }

    public void FocusFilter() => FocusRequested?.Invoke(this, "filter");

    private async Task RunFindAsync(bool jump)
    {
        _findCts?.Cancel();
        TextMatcher? matcher;
        try
        {
            matcher = TextMatcher.Create(FindText, FindRegex, FindMatchCase, false, splitTerms: false);
        }
        catch (ArgumentException)
        {
            FindStatus = "Invalid pattern";
            FindMatcher = null;
            _findMarks = [];
            OnPropertyChanged(nameof(FindMarks));
            return;
        }

        if (matcher == null)
        {
            FindMatcher = null;
            FindStatus = "";
            _findMarks = [];
            OnPropertyChanged(nameof(FindMarks));
            return;
        }

        var cts = _findCts = new CancellationTokenSource();
        var view = Document.View.ToArray();
        List<int> marks;
        try
        {
            marks = await Task.Run(() =>
            {
                var result = new List<int>();
                for (int i = 0; i < view.Length; i++)
                {
                    if ((i & 0xFFF) == 0)
                        cts.Token.ThrowIfCancellationRequested();
                    if (!view[i].IsSeparator && matcher.IsMatch(view[i].Raw))
                        result.Add(i);
                }
                return result;
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts != _findCts)
            return;

        _findMarks = marks;
        FindMatcher = matcher;
        OnPropertyChanged(nameof(FindMarks));
        MarksVersion++;
        FindStatus = marks.Count == 0 ? "No matches" : $"{marks.Count:N0} matches";
        if (jump && marks.Count > 0)
            FindNext();
    }

    [RelayCommand]
    public void FindNext() => JumpToMark(_findMarks, forward: true, isFind: true);

    [RelayCommand]
    public void FindPrevious() => JumpToMark(_findMarks, forward: false, isFind: true);

    private void JumpToMark(IReadOnlyList<int> marks, bool forward, bool isFind)
    {
        if (marks.Count == 0 || Viewport == null)
            return;
        int caret = Viewport.CaretIndex;
        int k;
        if (forward)
        {
            k = LowerBound(marks, caret + 1);
            if (k >= marks.Count)
                k = 0;
        }
        else
        {
            k = LowerBound(marks, caret) - 1;
            if (k < 0)
                k = marks.Count - 1;
        }
        IsPaused = false;
        Viewport.ScrollToIndex(marks[k], select: true);
        if (isFind)
            FindStatus = $"{k + 1:N0} of {marks.Count:N0}";
    }

    private static int LowerBound(IReadOnlyList<int> list, int value)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (list[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // ---------------------------------------------------------------- commands: bookmarks

    [RelayCommand]
    public void ToggleBookmark(LogLine? line)
    {
        line ??= SelectedLine;
        if (line == null || line.IsSeparator)
            return;
        Document.ToggleBookmark(line);
        MarksVersion++;
    }

    [RelayCommand]
    public void NextBookmark() => JumpToMark(Document.BookmarkMarks, forward: true, isFind: false);

    [RelayCommand]
    public void PreviousBookmark() => JumpToMark(Document.BookmarkMarks, forward: false, isFind: false);

    [RelayCommand]
    public void ClearBookmarks()
    {
        Document.ClearBookmarks();
        MarksVersion++;
    }

    public IReadOnlyList<LogLine> BookmarkedLines() => Document.BookmarkMarks.Select(i => Document.View[i]).ToList();

    public void GoToLine(LogLine line)
    {
        int index = Document.FindViewIndex(line.Number);
        if (index >= 0)
        {
            IsPaused = false;
            Viewport?.ScrollToIndex(index, select: true);
        }
    }

    // ---------------------------------------------------------------- commands: copy / export

    [RelayCommand]
    private void CopyLines()
    {
        var lines = Viewport?.GetSelectedLines() ?? [];
        if (lines.Count == 0 && ContextLine != null)
            lines = [ContextLine];
        CopyText(string.Join(Environment.NewLine, lines.Select(TextOf)));
    }

    [RelayCommand]
    private void CopyMessage(LogLine? line)
    {
        line ??= SelectedLine;
        if (line != null)
            CopyText(line.MessageText);
    }

    private static void CopyText(string text)
    {
        if (text.Length == 0)
            return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    [RelayCommand]
    public async Task ExportFiltered() => await ExportAsync(Document.View.Where(l => !l.IsSeparator).ToArray(), "filtered");

    [RelayCommand]
    public async Task ExportSelection()
    {
        var lines = Viewport?.GetSelectedLines() ?? [];
        await ExportAsync(lines.Where(l => !l.IsSeparator).ToArray(), "selection");
    }

    private async Task ExportAsync(LogLine[] lines, string suffix)
    {
        if (lines.Length == 0)
        {
            Notify?.Invoke(this, "Nothing to export");
            return;
        }
        var dialog = new SaveFileDialog
        {
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(FileName)}-{suffix}-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Filter = "Log file (*.log)|*.log|Text file (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            string target = dialog.FileName;
            await Task.Run(() => File.WriteAllLines(target, lines.Select(TextOf)));
            Notify?.Invoke(this, $"Exported {lines.Length:N0} lines to {System.IO.Path.GetFileName(target)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify?.Invoke(this, "Export failed: " + ex.Message);
        }
    }

    /// <summary>A line as text; in a timeline it starts with its device.</summary>
    public static string TextOf(LogLine line) => line.Origin != null ? $"[{line.Origin}] {line.Raw}" : line.Raw;

    public OpenTabState ToState() => new()
    {
        Path = Path,
        IsSeries = IsSeries,
        Paths = IsMerged ? Paths.ToList() : null,
        Filter = CurrentSpec(),
    };

    // ---------------------------------------------------------------- go to time

    /// <summary>Jumps to the first shown line at or after the time typed; returns a message when it cannot.</summary>
    public string? GoToTime(string text)
    {
        var view = Document.View;
        DateTime? first = null, last = null;
        for (int i = 0; i < view.Count && first == null; i++)
            first = view[i].Timestamp;
        for (int i = view.Count - 1; i >= 0 && last == null; i--)
            last = view[i].Timestamp;
        if (!TimeText.TryResolve(text, first, last, out var target))
            return $"\"{text}\" is not a time. Use HH:mm, HH:mm:ss or yyyy-MM-dd HH:mm:ss";
        if (first == null)
            return "No lines with a time to jump to";

        IsPaused = false;
        int index = TimeText.FindFirstAtOrAfter(view, target);
        if (index < 0)
        {
            Viewport?.ScrollToIndex(view.Count - 1, select: true);
            return $"No lines after {target:HH:mm:ss} — showing the last line";
        }
        Viewport?.ScrollToIndex(index, select: true);
        return null;
    }

    // ---------------------------------------------------------------- message groups

    partial void OnShowGroupsChanged(bool value)
    {
        if (value)
        {
            _groupsTimer.Start();
            _ = RefreshGroupsAsync();
        }
        else
        {
            _groupsTimer.Stop();
            _groupsCts?.Cancel();
        }
    }

    partial void OnGroupsProblemsOnlyChanged(bool value) => _ = RefreshGroupsAsync();

    [RelayCommand]
    private void ToggleGroups() => ShowGroups = !ShowGroups;

    /// <summary>Counts similar messages among the shown lines, in the background.</summary>
    [RelayCommand]
    public async Task RefreshGroupsAsync()
    {
        _groupsCts?.Cancel();
        var cts = _groupsCts = new CancellationTokenSource();
        int version = _viewVersion;
        var view = Document.View.ToArray();
        bool problemsOnly = GroupsProblemsOnly;
        IsGrouping = true;
        try
        {
            var groups = await Task.Run(() =>
            {
                IReadOnlyList<LogLine> lines = problemsOnly ? view.Where(l => l.Level >= LogLevel.Warning).ToArray() : view;
                return MessageGrouper.Group(lines, lines.Count, cts.Token, max: 500);
            }, cts.Token);
            if (cts != _groupsCts)
                return;
            _groupsVersion = version;
            Groups.Clear();
            foreach (var group in groups)
                Groups.Add(group);
            GroupsStatus = $"{groups.Count:N0} groups · {view.Length:N0} lines";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (cts == _groupsCts)
                IsGrouping = false;
        }
    }

    /// <summary>Highlights the lines of a group and jumps to the next one.</summary>
    [RelayCommand]
    private void FindGroup(MessageGroup? group)
    {
        group ??= SelectedGroup;
        if (group == null)
            return;
        FindRegex = true;
        FindMatchCase = true;
        FindText = MessageGrouper.ToRegex(group.Template);
        IsFindOpen = true;
    }

    [RelayCommand]
    private void OnlyGroup(MessageGroup? group)
    {
        group ??= SelectedGroup;
        if (group == null)
            return;
        UseRegexFilters();
        Include = MessageGrouper.ToRegex(group.Template);
    }

    [RelayCommand]
    private void HideGroup(MessageGroup? group)
    {
        group ??= SelectedGroup;
        if (group == null)
            return;
        UseRegexFilters();
        string pattern = MessageGrouper.ToRegex(group.Template);
        Exclude = string.IsNullOrWhiteSpace(Exclude) ? pattern : $"{Exclude}|{pattern}";
    }

    [RelayCommand]
    private void CopyGroup(MessageGroup? group)
    {
        group ??= SelectedGroup;
        if (group != null)
            CopyText(group.Template);
    }

    /// <summary>Switches the filter boxes to regex mode, turning plain "a; b" terms into the same "a|b" regex.</summary>
    private void UseRegexFilters()
    {
        if (IsRegex)
            return;
        static string ToRegex(string terms) => string.Join("|",
            terms.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(System.Text.RegularExpressions.Regex.Escape));
        _suppressFilter = true;
        Include = ToRegex(Include);
        Exclude = ToRegex(Exclude);
        _suppressFilter = false;
        IsRegex = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _drainTimer.Stop();
        _secondTimer.Stop();
        _filterTimer.Stop();
        _findTimer.Stop();
        _groupsTimer.Stop();
        _groupsCts?.Cancel();
        _filterCts?.Cancel();
        _findCts?.Cancel();
        StopTailer();
    }
}
