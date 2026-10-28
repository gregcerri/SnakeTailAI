using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SnakeTailAI.ViewModels;

namespace SnakeTailAI.Views;

public partial class LogTabView : UserControl
{
    public LogTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        LogView.LineActivated += (_, _) =>
        {
            if (Tab != null)
                Tab.ShowDetails = true;
        };
        LogView.BookmarkToggleRequested += (_, line) => Tab?.ToggleBookmark(line);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(() => LogView.FocusView(), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private LogTabViewModel? Tab => DataContext as LogTabViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LogTabViewModel old)
        {
            old.FocusRequested -= OnFocusRequested;
            old.Viewport = null;
        }
        if (e.NewValue is LogTabViewModel tab)
        {
            tab.Viewport = LogView;
            tab.FocusRequested += OnFocusRequested;
        }
    }

    private void OnFocusRequested(object? sender, string target)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var box = target == "find" ? FindBox : IncludeBox;
            box.Focus();
            box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (Tab == null)
            return;
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                Tab.FindPrevious();
            else
                Tab.FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Tab.CloseFindCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPresetsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main)
            return;

        var menu = new ContextMenu { PlacementTarget = PresetsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var save = new MenuItem { Header = "Save current filter as…", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Save24) };
        save.Click += (_, _) =>
        {
            string? name = InputDialog.Show(Window.GetWindow(this)!, "Save filter preset", "Name of the preset", "");
            if (!string.IsNullOrWhiteSpace(name))
                main.SavePreset(name);
        };
        menu.Items.Add(save);

        var presets = main.Settings.FilterPresets.ToList();
        if (presets.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var preset in presets)
            {
                var item = new MenuItem { Header = preset.Name, Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Filter24) };
                item.Click += async (_, _) => await main.ApplyPreset(preset);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = "Delete preset", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Delete24) };
            foreach (var preset in presets)
            {
                var item = new MenuItem { Header = preset.Name };
                item.Click += (_, _) => main.DeletePreset(preset);
                delete.Items.Add(item);
            }
            menu.Items.Add(delete);
        }
        menu.IsOpen = true;
    }
}
