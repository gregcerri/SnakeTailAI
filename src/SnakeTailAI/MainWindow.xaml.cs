using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SnakeTailAI.ViewModels;
using SnakeTailAI.Views;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SnakeTailAI;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        RestorePlacement();
        UpdateBrowserColumn();
        UpdateTerminalRow();
        // While typing in the terminal, keys like Ctrl+L or F3 belong to the remote shell, not to the log view.
        TerminalPanel.IsKeyboardFocusWithinChanged += (_, e) => SuspendShortcuts((bool)e.NewValue);
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.Toast += (_, message) => ShowToast(message);
        vm.GoToTimeRequested += AskGoToTime;
        vm.TimelineRequested += AskTimeline;
        Loaded += (_, _) => vm.Browser.Start();
        Closing += OnClosing;
        Drop += OnDrop;
        DragOver += OnDragOver;
    }

    public void ShowToast(string message, bool isError = false)
    {
        bool warning = isError || message.Contains("not found", StringComparison.OrdinalIgnoreCase) || message.Contains("failed", StringComparison.OrdinalIgnoreCase);
        var snackbar = new Snackbar(SnackbarPresenter)
        {
            Title = warning ? "Attention" : "SnakeTail AI",
            Content = message,
            Appearance = warning ? ControlAppearance.Caution : ControlAppearance.Secondary,
            Icon = new SymbolIcon(warning ? SymbolRegular.Warning24 : SymbolRegular.Info24),
            Timeout = TimeSpan.FromSeconds(warning ? 5 : 3),
        };
        snackbar.Show();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowBrowser) or nameof(MainViewModel.FocusMode))
            UpdateBrowserColumn();
        else if (e.PropertyName == nameof(MainViewModel.ShowTerminal))
            UpdateTerminalRow();
    }

    private void UpdateBrowserColumn()
    {
        bool show = _vm.ShowBrowser && !_vm.FocusMode;
        Browser.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = Browser.Visibility;
        BrowserColumn.Width = show ? new GridLength(Math.Clamp(_vm.Settings.BrowserWidth, 200, 600)) : new GridLength(0);
        SplitterColumn.Width = show ? new GridLength(10) : new GridLength(0);
    }

    private void UpdateTerminalRow()
    {
        bool show = _vm.ShowTerminal;
        TerminalPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TerminalSplitter.Visibility = TerminalPanel.Visibility;
        TerminalRow.Height = show ? new GridLength(Math.Clamp(_vm.Settings.TerminalHeight, 120, 2000)) : new GridLength(0);
    }

    private void OnTerminalSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _vm.Settings.TerminalHeight = TerminalRow.ActualHeight;

    private List<System.Windows.Input.InputBinding>? _suspendedShortcuts;

    private void SuspendShortcuts(bool suspend)
    {
        if (suspend && _suspendedShortcuts == null)
        {
            _suspendedShortcuts = InputBindings.Cast<System.Windows.Input.InputBinding>().ToList();
            InputBindings.Clear();
        }
        else if (!suspend && _suspendedShortcuts != null)
        {
            foreach (var binding in _suspendedShortcuts)
                InputBindings.Add(binding);
            _suspendedShortcuts = null;
        }
    }

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _vm.Settings.BrowserWidth = BrowserColumn.ActualWidth;

    private void RestorePlacement()
    {
        var p = _vm.Settings.Window;
        Width = Math.Max(MinWidth, p.Width);
        Height = Math.Max(MinHeight, p.Height);
        if (!double.IsNaN(p.Left) && !double.IsNaN(p.Top) &&
            p.Left > SystemParameters.VirtualScreenLeft - 50 && p.Top > SystemParameters.VirtualScreenTop - 50 &&
            p.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            p.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.Left;
            Top = p.Top;
        }
        if (p.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var p = _vm.Settings.Window;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        p.Left = bounds.Left;
        p.Top = bounds.Top;
        p.Width = bounds.Width;
        p.Height = bounds.Height;
        p.Maximized = WindowState == WindowState.Maximized;
        if (_vm.ShowBrowser)
            _vm.Settings.BrowserWidth = BrowserColumn.ActualWidth;
        _vm.Shutdown();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                _vm.Open(path);
            }
            else if (Directory.Exists(path))
            {
                string? active = Core.LogSeries.FindActiveLog(path);
                if (active != null)
                    _vm.Open(active);
            }
        }
    }

    private void OnBookmarksClick(object sender, RoutedEventArgs e)
    {
        var tab = _vm.SelectedTab;
        if (tab == null)
            return;
        var menu = new ContextMenu { PlacementTarget = BookmarksButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuItemFor("Toggle bookmark", "Ctrl+F2", SymbolRegular.BookmarkAdd24, () => tab.ToggleBookmark(null)));
        menu.Items.Add(MenuItemFor("Next bookmark", "F2", SymbolRegular.ArrowDown24, tab.NextBookmark));
        menu.Items.Add(MenuItemFor("Previous bookmark", "Shift+F2", SymbolRegular.ArrowUp24, tab.PreviousBookmark));

        var lines = tab.BookmarkedLines();
        if (lines.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var line in lines.Take(40))
            {
                string text = line.Raw.Length > 110 ? line.Raw[..110] + "…" : line.Raw;
                var item = new MenuItem { Header = text, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), FontSize = 12 };
                item.Click += (_, _) => tab.GoToLine(line);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Clear all bookmarks", null, SymbolRegular.BookmarkOff24, tab.ClearBookmarks));
        }
        menu.IsOpen = true;
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var tab = _vm.SelectedTab;
        if (tab == null)
            return;
        var menu = new ContextMenu { PlacementTarget = ExportButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuItemFor("Export shown lines…", null, SymbolRegular.ArrowExportLtr24, async () => await tab.ExportFiltered()));
        menu.Items.Add(MenuItemFor("Export selected lines…", null, SymbolRegular.CopySelect20, async () => await tab.ExportSelection()));
        menu.IsOpen = true;
    }

    private static MenuItem MenuItemFor(string header, string? gesture, SymbolRegular icon, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", Icon = new SymbolIcon(icon) };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_vm.Settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _vm.Theme = _vm.Settings.Theme;
            _vm.ApplySettings();
        }
    }

    private string _lastGoToTime = "";

    private void AskGoToTime()
    {
        var tab = _vm.SelectedTab;
        if (tab == null)
            return;
        string? text = InputDialog.Show(this, "Go to time", "Time to jump to: HH:mm, HH:mm:ss or yyyy-MM-dd HH:mm:ss", _lastGoToTime);
        if (string.IsNullOrWhiteSpace(text))
            return;
        _lastGoToTime = text.Trim();
        string? problem = tab.GoToTime(_lastGoToTime);
        if (problem != null)
            ShowToast(problem);
    }

    private void AskTimeline()
    {
        var paths = TimelinePicker.Show(this, _vm.Browser.Devices);
        if (paths is { Count: > 1 })
            _vm.OpenTimeline(paths);
    }

    private void OnAlertsClick(object sender, RoutedEventArgs e)
    {
        var window = new AlertsWindow(_vm.Settings.AlertRules, _vm.AlertsMuted, _vm.Alerts) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _vm.Settings.AlertRules = window.Rules;
            _vm.AlertsMuted = window.Muted;
            _vm.ApplySettings();
        }
    }

    private void OnHighlightsClick(object sender, RoutedEventArgs e)
    {
        var window = new HighlightEditorWindow(_vm.Settings.HighlightRules) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _vm.Settings.HighlightRules = window.Rules;
            _vm.ApplySettings();
        }
    }
}
