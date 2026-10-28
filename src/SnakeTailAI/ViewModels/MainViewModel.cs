using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SnakeTailAI.Core;
using SnakeTailAI.Services;

namespace SnakeTailAI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        _highlighter = new Highlighter(settings.HighlightRules);
        _showBrowser = settings.ShowBrowser;
        _alwaysOnTop = settings.AlwaysOnTop;
        _theme = settings.Theme;
        _logFontFamily = settings.FontFamily;
        _fadeIn = settings.FadeInNewLines;
        _lineSpacing = settings.RowSpacing;
        _showFilterBar = settings.ShowFilterBar;
        _sshEnabled = settings.SshEnabled;
        Browser = new BrowserViewModel(settings.LogRoot, (path, series) => Open(path, series))
        {
            OpenPaths = () => Tabs.SelectMany(t => t.Paths),
            LastUsed = folder => settings.DeviceLastUsed.TryGetValue(folder, out var used) ? used : null,
            OpenSshFolder = folder => OpenSshCommand.Execute(folder),
        };
        Browser.ActivateFolder = ActivateFolder;

        _alertsMuted = settings.AlertsMuted;
        Alerts = new AlertService { Muted = settings.AlertsMuted };
        Alerts.SetRules(settings.AlertRules);
        Alerts.MutedChanged += muted => AlertsMuted = muted;
        Alerts.AlertOpened += (tab, line) =>
        {
            if (!Tabs.Contains(tab))
                return;
            SelectedTab = tab;
            tab.GoToLine(line);
        };
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(RecentFiles));
            Browser.UpdateOpenState();
        };
    }

    public AppSettings Settings { get; }
    public BrowserViewModel Browser { get; }
    public AlertService Alerts { get; }
    public ObservableCollection<LogTabViewModel> Tabs { get; } = [];
    public bool HasTabs => Tabs.Count > 0;
    public IReadOnlyList<string> RecentFiles => Settings.RecentFiles.Take(6).ToList();

    /// <summary>A message to show as a toast.</summary>
    public event EventHandler<string>? Toast;

    /// <summary>The window should ask for a time to jump to.</summary>
    public event Action? GoToTimeRequested;

    /// <summary>The window should let the user pick the devices of a new timeline.</summary>
    public event Action? TimelineRequested;

    [ObservableProperty] private LogTabViewModel? _selectedTab;
    [ObservableProperty] private Highlighter _highlighter;
    [ObservableProperty] private bool _showBrowser;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private ThemeChoice _theme;
    [ObservableProperty] private string _logFontFamily;
    [ObservableProperty] private bool _fadeIn;
    [ObservableProperty] private bool _alertsMuted;
    [ObservableProperty] private double _lineSpacing;
    [ObservableProperty] private bool _sshEnabled;

    /// <summary>
    /// Opens an SSH terminal on a device: the one of <paramref name="path"/> (a log or device folder), or of the tab
    /// being viewed (in a timeline, the device of the selected line, else the first one).
    /// </summary>
    [RelayCommand]
    private void OpenSsh(string? path)
    {
        if (path == null && SelectedTab is { } tab)
        {
            path = tab.Path;
            if (tab.IsMerged && tab.SelectedLine?.Origin is { } origin)
                path = tab.Paths.FirstOrDefault(p => Format.DeviceName(p) == origin) ?? path;
        }
        string? host = path == null ? null : SshLauncher.HostOf(path);
        if (host == null)
        {
            Toast?.Invoke(this, "SSH works for device folders named ViuPC-<address>");
            return;
        }
        var existing = Terminals.FirstOrDefault(t => string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectedTerminal = existing;
            ShowTerminal = true;
            if (existing.HasEnded)
                existing.ReconnectCommand.Execute(null);
            return;
        }

        var (commandLine, error) = SshLauncher.CommandLine(host, Settings);
        if (commandLine == null)
        {
            Toast?.Invoke(this, error!);
            return;
        }
        var session = new TerminalSessionViewModel(host, commandLine);
        Terminals.Add(session);
        SelectedTerminal = session;
        ShowTerminal = true;
    }

    /// <summary>SSH sessions shown in the terminal panel at the bottom.</summary>
    public ObservableCollection<TerminalSessionViewModel> Terminals { get; } = [];

    [ObservableProperty] private TerminalSessionViewModel? _selectedTerminal;
    [ObservableProperty] private bool _showTerminal;

    partial void OnSelectedTerminalChanged(TerminalSessionViewModel? oldValue, TerminalSessionViewModel? newValue)
    {
        if (oldValue != null)
            oldValue.IsActive = false;
        if (newValue != null)
            newValue.IsActive = true;
    }

    [RelayCommand]
    private void ToggleTerminal()
    {
        if (!ShowTerminal && Terminals.Count == 0)
        {
            OpenSshCommand.Execute(null);
            return;
        }
        ShowTerminal = !ShowTerminal;
    }

    [RelayCommand]
    private void CloseTerminal(TerminalSessionViewModel? session)
    {
        session ??= SelectedTerminal;
        if (session == null)
            return;
        int index = Terminals.IndexOf(session);
        session.Close();
        Terminals.Remove(session);
        if (SelectedTerminal == session || SelectedTerminal == null)
            SelectedTerminal = Terminals.Count == 0 ? null : Terminals[Math.Clamp(index, 0, Terminals.Count - 1)];
        if (Terminals.Count == 0)
            ShowTerminal = false;
    }

    /// <summary>Only the tabs and the log: toolbar, devices panel, filters and status bar are hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterBarVisible))]
    private bool _focusMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterBarVisible))]
    private bool _showFilterBar;

    public bool FilterBarVisible => ShowFilterBar && !FocusMode;

    partial void OnShowFilterBarChanged(bool value) => Settings.ShowFilterBar = value;

    [RelayCommand]
    private void ToggleFocusMode() => FocusMode = !FocusMode;

    [RelayCommand]
    private void ExitFocusMode() => FocusMode = false;

    [RelayCommand]
    private void ToggleFilterBar()
    {
        if (FocusMode)
        {
            FocusMode = false;
            ShowFilterBar = true;
            return;
        }
        ShowFilterBar = !ShowFilterBar;
    }

    partial void OnAlertsMutedChanged(bool value)
    {
        Settings.AlertsMuted = value;
        Alerts.Muted = value;
    }

    partial void OnSelectedTabChanged(LogTabViewModel? oldValue, LogTabViewModel? newValue)
    {
        if (oldValue != null)
            oldValue.IsActive = false;
        Browser.SetCurrent(newValue == null || newValue.IsMerged ? null : Path.GetDirectoryName(newValue.Path));
        if (newValue != null)
        {
            newValue.IsActive = true;
            if (!_restoring)
                MarkDevicesUsed(newValue);
        }
    }

    private bool _restoring;

    /// <summary>Selects the tab showing a log of this folder (the live log first, then a series, then a timeline).</summary>
    private bool ActivateFolder(string folder)
    {
        string wanted = folder.TrimEnd('\\', '/');
        bool InFolder(LogTabViewModel t) =>
            t.Paths.Any(p => string.Equals(Path.GetDirectoryName(p), wanted, StringComparison.OrdinalIgnoreCase));
        var tab = Tabs.FirstOrDefault(t => InFolder(t) && !t.IsSeries && !t.IsMerged)
                  ?? Tabs.FirstOrDefault(t => InFolder(t) && !t.IsMerged)
                  ?? Tabs.FirstOrDefault(InFolder);
        if (tab == null)
            return false;
        SelectedTab = tab;
        return true;
    }

    /// <summary>Moves the devices of this tab to the top of the Devices panel.</summary>
    private void MarkDevicesUsed(LogTabViewModel tab)
    {
        foreach (string path in tab.Paths)
        {
            string? folder = Path.GetDirectoryName(path);
            if (folder != null)
                Settings.MarkDeviceUsed(folder);
        }
        Browser.ApplyOrder();
    }

    partial void OnShowBrowserChanged(bool value) => Settings.ShowBrowser = value;
    partial void OnAlwaysOnTopChanged(bool value) => Settings.AlwaysOnTop = value;

    partial void OnHighlighterChanged(Highlighter value)
    {
        foreach (var tab in Tabs)
            tab.Highlighter = value;
    }

    public bool IsDarkTheme => Theme != ThemeChoice.Light && (Theme != ThemeChoice.System || ThemeService.IsDark);

    partial void OnThemeChanged(ThemeChoice value)
    {
        Settings.Theme = value;
        ThemeService.Apply(value, Application.Current.MainWindow);
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    // ---------------------------------------------------------------- tabs

    public LogTabViewModel? Open(string path, bool series = false, FilterSpec? filter = null)
    {
        path = Path.GetFullPath(path);
        var existing = Tabs.FirstOrDefault(t => !t.IsMerged && t.IsSeries == series && string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectedTab = existing;
            return existing;
        }

        var tab = AddTab(new LogTabViewModel(path, series, Settings, Highlighter), filter);
        Settings.AddRecent(path);
        OnPropertyChanged(nameof(RecentFiles));
        return tab;
    }

    /// <summary>Opens a timeline: the logs of several devices merged in time order.</summary>
    public LogTabViewModel? OpenTimeline(IEnumerable<string> paths, FilterSpec? filter = null)
    {
        var list = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
            return null;
        if (list.Count == 1)
            return Open(list[0], filter: filter);
        var existing = Tabs.FirstOrDefault(t => t.IsMerged && t.Paths.Count == list.Count &&
                                                t.Paths.All(p => list.Contains(p, StringComparer.OrdinalIgnoreCase)));
        if (existing != null)
        {
            SelectedTab = existing;
            return existing;
        }
        return AddTab(new LogTabViewModel(list, false, Settings, Highlighter), filter);
    }

    private LogTabViewModel AddTab(LogTabViewModel tab, FilterSpec? filter)
    {
        tab.Notify += (_, message) => Toast?.Invoke(this, message);
        tab.LiveLinesArrived += (_, lines) => Alerts.Process(tab, lines);
        Tabs.Add(tab);
        SelectedTab = tab;
        tab.Start();
        if (filter != null && !filter.IsEmpty)
            _ = tab.ApplyFilterAsync(filter);
        return tab;
    }

    [RelayCommand]
    private void NewTimeline() => TimelineRequested?.Invoke();

    [RelayCommand]
    private void GoToTime()
    {
        if (SelectedTab != null)
            GoToTimeRequested?.Invoke();
    }

    [RelayCommand]
    private void ToggleGroups()
    {
        if (SelectedTab != null)
            SelectedTab.ShowGroups = !SelectedTab.ShowGroups;
    }

    [RelayCommand]
    private void ToggleAlertsMuted() => AlertsMuted = !AlertsMuted;

    [RelayCommand]
    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open log file",
            Multiselect = true,
            Filter = "Log files (*.log;*.log.*;*.txt)|*.log;*.log.*;*.txt|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(Settings.LogRoot) ? Settings.LogRoot : "",
        };
        if (dialog.ShowDialog() != true)
            return;
        foreach (string file in dialog.FileNames)
            Open(file);
    }

    [RelayCommand]
    private void OpenRecent(string? path)
    {
        if (path == null)
            return;
        if (File.Exists(path))
        {
            Open(path);
        }
        else
        {
            Settings.RecentFiles.Remove(path);
            OnPropertyChanged(nameof(RecentFiles));
            Toast?.Invoke(this, "File not found: " + path);
        }
    }

    [RelayCommand]
    private void OpenSeriesOfCurrent()
    {
        if (SelectedTab != null)
            Open(LogSeries.GetActivePath(SelectedTab.Path), series: true);
    }

    [RelayCommand]
    public void CloseTab(LogTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab == null)
            return;
        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        tab.Dispose();
        if (SelectedTab == tab || SelectedTab == null)
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void CloseOtherTabs(LogTabViewModel? tab)
    {
        tab ??= SelectedTab;
        foreach (var other in Tabs.Where(t => t != tab).ToList())
            CloseTab(other);
    }

    [RelayCommand]
    private void NextTab() => MoveTab(1);

    [RelayCommand]
    private void PreviousTab() => MoveTab(-1);

    private void MoveTab(int delta)
    {
        if (Tabs.Count == 0)
            return;
        int index = SelectedTab == null ? 0 : Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(index + delta + Tabs.Count) % Tabs.Count];
    }

    [RelayCommand]
    private void CopyPath(LogTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab == null)
            return;
        try
        {
            Clipboard.SetText(tab.Path);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    [RelayCommand]
    private void ShowInExplorer(LogTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab == null)
            return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{tab.Path}\"");
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    // ---------------------------------------------------------------- commands forwarded to the active tab

    [RelayCommand] private void Clear() => SelectedTab?.Clear();
    [RelayCommand] private void Reload() => SelectedTab?.Reload();
    [RelayCommand] private void TogglePause() => SelectedTab?.TogglePause();
    [RelayCommand] private void FollowTail() => SelectedTab?.FollowTail();
    [RelayCommand] private void Find() => SelectedTab?.OpenFind();
    [RelayCommand] private void FindNext() => SelectedTab?.FindNext();
    [RelayCommand] private void FindPrevious() => SelectedTab?.FindPrevious();
    [RelayCommand] private void FocusFilter() => SelectedTab?.FocusFilter();
    [RelayCommand] private void ToggleBookmark() => SelectedTab?.ToggleBookmark(null);
    [RelayCommand] private void NextBookmark() => SelectedTab?.NextBookmark();
    [RelayCommand] private void PreviousBookmark() => SelectedTab?.PreviousBookmark();
    [RelayCommand] private void ToggleDetails() => SelectedTab?.ToggleDetails();
    [RelayCommand] private async Task ExportFiltered() { if (SelectedTab != null) await SelectedTab.ExportFiltered(); }
    [RelayCommand] private async Task ExportSelection() { if (SelectedTab != null) await SelectedTab.ExportSelection(); }
    [RelayCommand] private async Task ClearFilter() { if (SelectedTab != null) await SelectedTab.ClearFilter(); }

    [RelayCommand]
    private void ToggleColumns()
    {
        if (SelectedTab != null)
            SelectedTab.ColumnsMode = !SelectedTab.ColumnsMode;
    }

    [RelayCommand]
    private void ToggleWrap()
    {
        if (SelectedTab != null)
            SelectedTab.WordWrap = !SelectedTab.WordWrap;
    }

    [RelayCommand]
    private void ToggleBrowser() => ShowBrowser = !ShowBrowser;

    [RelayCommand]
    private void ToggleTheme() => Theme = IsDarkTheme ? ThemeChoice.Light : ThemeChoice.Dark;

    [RelayCommand]
    private void ZoomIn() => Zoom(1);

    [RelayCommand]
    private void ZoomOut() => Zoom(-1);

    [RelayCommand]
    private void ZoomReset()
    {
        foreach (var tab in Tabs)
            tab.FontSize = 12.5;
    }

    private void Zoom(int direction)
    {
        foreach (var tab in Tabs)
            tab.FontSize = Math.Clamp(tab.FontSize + direction, 8, 28);
    }

    // ---------------------------------------------------------------- presets

    public void SavePreset(string name)
    {
        if (SelectedTab == null || string.IsNullOrWhiteSpace(name))
            return;
        Settings.FilterPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Settings.FilterPresets.Add(new FilterPreset { Name = name.Trim(), Filter = SelectedTab.CurrentSpec() });
        SettingsStore.Save(Settings);
        Toast?.Invoke(this, $"Filter preset \"{name.Trim()}\" saved");
    }

    public async Task ApplyPreset(FilterPreset preset)
    {
        if (SelectedTab != null)
            await SelectedTab.ApplyFilterAsync(preset.Filter);
    }

    public void DeletePreset(FilterPreset preset)
    {
        Settings.FilterPresets.Remove(preset);
        SettingsStore.Save(Settings);
    }

    // ---------------------------------------------------------------- settings / session

    public void ApplySettings()
    {
        Highlighter = new Highlighter(Settings.HighlightRules);
        Alerts.SetRules(Settings.AlertRules);
        if (Browser.Root != Settings.LogRoot)
            Browser.Root = Settings.LogRoot;
        LogFontFamily = Settings.FontFamily;
        FadeIn = Settings.FadeInNewLines;
        LineSpacing = Settings.RowSpacing;
        SshEnabled = Settings.SshEnabled;
        foreach (var tab in Tabs)
        {
            tab.Document.MaxLines = Settings.MaxLines;
            tab.FontSize = Settings.FontSize;
        }
        SettingsStore.Save(Settings);
    }

    public void RestoreSession()
    {
        if (!Settings.RestoreSession)
            return;
        var states = Settings.OpenTabs.ToList();
        // Reopening tabs is not "using" a device: keep the order of the Devices panel as it was.
        _restoring = true;
        try
        {
            foreach (var state in states)
            {
                if (state.Paths is { Count: > 1 })
                    OpenTimeline(state.Paths.Where(File.Exists), state.Filter);
                else if (File.Exists(state.Path) || state.IsSeries)
                    Open(state.Path, state.IsSeries, state.Filter);
            }
            if (Settings.SelectedTab >= 0 && Settings.SelectedTab < Tabs.Count)
                SelectedTab = Tabs[Settings.SelectedTab];
        }
        finally
        {
            _restoring = false;
        }
        // The tab shown at startup is the one in use.
        if (SelectedTab != null)
            MarkDevicesUsed(SelectedTab);
    }

    public void SaveSession()
    {
        Settings.OpenTabs = Tabs.Select(t => t.ToState()).ToList();
        Settings.SelectedTab = SelectedTab == null ? 0 : Tabs.IndexOf(SelectedTab);
        SettingsStore.Save(Settings);
    }

    public void Shutdown()
    {
        SaveSession();
        foreach (var tab in Tabs)
            tab.Dispose();
        foreach (var session in Terminals)
            session.Close();
        Alerts.Dispose();
    }
}
