using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnakeTailAI.Core;

namespace SnakeTailAI.ViewModels;

public sealed partial class LogFileViewModel(string path, BrowserViewModel owner) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public IRelayCommand OpenCommand { get; } = new RelayCommand(() => owner.Open(path, false));

    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _timeText = "";

    internal void Update(long size, DateTime lastWrite)
    {
        SizeText = Format.Size(size);
        TimeText = lastWrite.ToString("dd/MM HH:mm:ss");
    }
}

/// <summary>A device folder (e.g. <c>ViuPC-172.28.130.31</c>) shown as a card.</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    private readonly BrowserViewModel _owner;

    public DeviceViewModel(string folder, bool isRoot, BrowserViewModel owner)
    {
        _owner = owner;
        FolderPath = folder;
        string name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
        IsRoot = isRoot;
        if (isRoot)
        {
            Title = name;
            Subtitle = "Root folder";
        }
        else if (name.StartsWith("ViuPC-", StringComparison.OrdinalIgnoreCase))
        {
            Title = name["ViuPC-".Length..];
            Subtitle = "ViuPC";
        }
        else
        {
            Title = name;
            Subtitle = "Folder";
        }
    }

    public string FolderPath { get; }
    public bool IsRoot { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public ObservableCollection<LogFileViewModel> Files { get; } = [];

    [ObservableProperty] private string? _activeLog;
    [ObservableProperty] private string _activeLogName = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _lastWriteText = "";
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private int _archiveCount;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _hasLog;
    [ObservableProperty] private bool _isOpen;

    /// <summary>The device of the tab being viewed.</summary>
    [ObservableProperty] private bool _isCurrent;

    public DateTime LastWrite { get; private set; }

    /// <summary>Position in the folder listing, used to order folders that were never used.</summary>
    public int ScanOrder { get; set; }

    internal void Update(BrowserViewModel.FolderScan scan)
    {
        ActiveLog = scan.ActiveLog;
        ActiveLogName = scan.ActiveLog != null ? System.IO.Path.GetFileName(scan.ActiveLog) : "No log files";
        HasLog = scan.ActiveLog != null;
        LastWrite = scan.LastWrite;
        SizeText = scan.ActiveLog != null ? Format.Size(scan.ActiveSize) : "";
        ArchiveCount = scan.ArchiveCount;
        UpdateAge();

        // Rebuild the file list only when the set of files changed, so an open expander does not flicker.
        if (!Files.Select(f => f.Path).SequenceEqual(scan.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase))
        {
            Files.Clear();
            foreach (var f in scan.Files)
                Files.Add(new LogFileViewModel(f.Path, _owner));
        }
        for (int i = 0; i < Files.Count; i++)
            Files[i].Update(scan.Files[i].Size, scan.Files[i].LastWrite);
    }

    internal void UpdateAge()
    {
        if (!HasLog)
        {
            LastWriteText = "";
            IsLive = false;
            return;
        }
        var age = DateTime.Now - LastWrite;
        IsLive = age < Liveness.Window;
        LastWriteText = Format.Age(age);
    }

    /// <summary>Click on the card: shows the tab of this device if one is open.</summary>
    [RelayCommand]
    private void Activate() => _owner.Activate(FolderPath);

    [RelayCommand]
    private void OpenLive()
    {
        if (ActiveLog != null)
            _owner.Open(ActiveLog, false);
    }

    [RelayCommand]
    private void OpenSeries()
    {
        if (ActiveLog != null)
            _owner.Open(ActiveLog, true);
    }

    [RelayCommand]
    private void OpenFolder() => _owner.ShowInExplorer(FolderPath);

    /// <summary>The device can be reached by SSH (a ViuPC-&lt;address&gt; folder).</summary>
    public bool CanSsh => Services.SshLauncher.HostOf(FolderPath) != null;

    [RelayCommand]
    private void OpenSsh() => _owner.OpenSsh(FolderPath);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>The device panel: one card per sub-folder of the log root, refreshed every few seconds.</summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    private readonly Action<string, bool> _open;
    private readonly DispatcherTimer _timer;
    private bool _scanning;

    public BrowserViewModel(string root, Action<string, bool> open)
    {
        _root = root;
        _open = open;
        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.Filter = o => o is DeviceViewModel d && (string.IsNullOrWhiteSpace(Search) ||
            d.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) || d.FolderPath.Contains(Search, StringComparison.OrdinalIgnoreCase));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ICollectionView DevicesView { get; }

    [ObservableProperty] private string _root;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _rootMissing;

    /// <summary>Paths of the files open in tabs, to mark their cards.</summary>
    public Func<IEnumerable<string>>? OpenPaths { get; set; }

    /// <summary>When a folder was last used; used folders come first, most recent on top.</summary>
    public Func<string, DateTime?>? LastUsed { get; set; }

    /// <summary>Opens an SSH terminal on the device of a folder.</summary>
    public Action<string>? OpenSshFolder { get; set; }

    internal void OpenSsh(string folder) => OpenSshFolder?.Invoke(folder);

    /// <summary>Shows the tab of a device folder; returns false when none is open.</summary>
    public Func<string, bool>? ActivateFolder { get; set; }

    private string? _currentFolder;

    internal void Activate(string folder) => ActivateFolder?.Invoke(folder);

    /// <summary>Highlights the card of the device whose tab is being viewed.</summary>
    public void SetCurrent(string? folder)
    {
        _currentFolder = folder?.TrimEnd('\\', '/');
        foreach (var device in Devices)
            device.IsCurrent = _currentFolder != null &&
                string.Equals(device.FolderPath.TrimEnd('\\', '/'), _currentFolder, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchChanged(string value) => DevicesView.Refresh();

    partial void OnRootChanged(string value)
    {
        Devices.Clear();
        _ = RefreshAsync();
    }

    public void Start()
    {
        _ = RefreshAsync();
        _timer.Start();
    }

    internal void Open(string path, bool series) => _open(path, series);

    internal void ShowInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    internal sealed record FileEntry(string Path, long Size, DateTime LastWrite);

    internal sealed record FolderScan(string Folder, bool IsRoot, string? ActiveLog, long ActiveSize, DateTime LastWrite, int ArchiveCount, List<FileEntry> Files);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_scanning)
            return;
        _scanning = true;
        string root = Root;
        try
        {
            var scans = await Task.Run(() => Scan(root));
            if (root != Root)
                return;
            RootMissing = scans == null;
            if (scans == null)
            {
                Status = $"Folder not found: {root}";
                Devices.Clear();
                return;
            }
            Apply(scans);
            int live = Devices.Count(d => d.IsLive);
            Status = $"{Devices.Count} folders · {live} live";
        }
        finally
        {
            _scanning = false;
        }
    }

    private void Apply(List<FolderScan> scans)
    {
        var byPath = Devices.ToDictionary(d => d.FolderPath, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(scans.Select(s => s.Folder), StringComparer.OrdinalIgnoreCase);

        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Devices[i].FolderPath))
                Devices.RemoveAt(i);
        }

        int index = 0;
        foreach (var scan in scans)
        {
            if (!byPath.TryGetValue(scan.Folder, out var device))
            {
                device = new DeviceViewModel(scan.Folder, scan.IsRoot, this) { ScanOrder = index };
                Devices.Add(device);
            }
            device.ScanOrder = index;
            device.Update(scan);
            index++;
        }
        UpdateOpenState();
    }

    /// <summary>Marks the devices that have a log open in a tab, and re-sorts.</summary>
    public void UpdateOpenState()
    {
        var openFolders = new HashSet<string>(
            (OpenPaths?.Invoke() ?? []).Select(p => System.IO.Path.GetDirectoryName(p) ?? ""),
            StringComparer.OrdinalIgnoreCase);
        foreach (var device in Devices)
            device.IsOpen = openFolders.Contains(device.FolderPath.TrimEnd('\\', '/'));
        SetCurrent(_currentFolder);
        ApplyOrder();
    }

    private bool _isPointerOver;
    private bool _orderPending;

    /// <summary>
    /// The mouse is over the panel. Re-sorting then waits until it leaves, so a card never jumps away
    /// from under the pointer while the user is clicking.
    /// </summary>
    public bool IsPointerOver
    {
        get => _isPointerOver;
        set
        {
            _isPointerOver = value;
            if (!value && _orderPending)
                ApplyOrder();
        }
    }

    /// <summary>
    /// Sorts the cards: devices open in a tab first, then by last use (most recent on top),
    /// then the never-used ones in folder order.
    /// </summary>
    public void ApplyOrder()
    {
        if (_isPointerOver)
        {
            _orderPending = true;
            return;
        }
        _orderPending = false;
        var ordered = Devices
            .OrderByDescending(d => d.IsOpen)
            .ThenByDescending(d => LastUsed?.Invoke(d.FolderPath) ?? DateTime.MinValue)
            .ThenBy(d => d.ScanOrder)
            .ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int current = Devices.IndexOf(ordered[i]);
            if (current != i)
                Devices.Move(current, i);
        }
    }

    private static List<FolderScan>? Scan(string root)
    {
        if (!Directory.Exists(root))
            return null;
        var result = new List<FolderScan>();
        try
        {
            var rootScan = ScanFolder(root, isRoot: true);
            foreach (string dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var scan = ScanFolder(dir, isRoot: false);
                if (scan.Files.Count > 0)
                    result.Add(scan);
            }
            if (rootScan.Files.Count > 0)
                result.Add(rootScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return result;
    }

    private static FolderScan ScanFolder(string folder, bool isRoot)
    {
        var files = new List<FileEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFiles())
            {
                bool archive = LogSeries.IsArchiveName(info.Name, out string baseName, out _);
                if (LogSeries.IsLogLike(archive ? baseName : info.Name))
                    files.Add(new FileEntry(info.FullName, info.Length, info.LastWriteTime));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        files.Sort((a, b) => b.LastWrite.CompareTo(a.LastWrite));
        var active = files.FirstOrDefault(f => !LogSeries.IsArchiveName(System.IO.Path.GetFileName(f.Path), out _, out _));
        int archives = active == null ? 0 : LogSeries.GetSeries(active.Path).Count - 1;
        return new FolderScan(folder, isRoot, active?.Path, active?.Size ?? 0, active?.LastWrite ?? DateTime.MinValue, archives, files);
    }
}

/// <summary>When a log counts as live (green dot): written within this time.</summary>
public static class Liveness
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
}

public static class Format
{
    /// <summary>The short name of a device from a log or folder path: <c>…\ViuPC-172.28.130.31\x.log</c> → <c>172.28.130.31</c>.</summary>
    public static string DeviceName(string path)
    {
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return name.StartsWith("ViuPC-", StringComparison.OrdinalIgnoreCase) ? name["ViuPC-".Length..] : name;
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string Age(TimeSpan age) => age.TotalSeconds switch
    {
        < 0 => "just now",
        < 5 => "just now",
        < 60 => $"{(int)age.TotalSeconds} s ago",
        < 3600 => $"{(int)age.TotalMinutes} min ago",
        < 86400 => $"{(int)age.TotalHours} h ago",
        _ => $"{(int)age.TotalDays} d ago",
    };
}
