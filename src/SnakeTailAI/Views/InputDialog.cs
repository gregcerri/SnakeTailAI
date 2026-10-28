using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace SnakeTailAI.Views;

/// <summary>A small Fluent dialog that asks for one line of text.</summary>
public static class InputDialog
{
    public static string? Show(Window owner, string title, string label, string initial)
    {
        var box = new Wpf.Ui.Controls.TextBox { Text = initial, PlaceholderText = label, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = ControlAppearance.Primary, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20, 10, 20, 20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, FontSize = 13 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var root = new DockPanel();
        var titleBar = new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        root.Children.Add(panel);

        var window = new FluentWindow
        {
            Owner = owner,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ExtendsContentIntoTitleBar = true,
            WindowBackdropType = WindowBackdropType.Mica,
            Content = root,
            ShowInTaskbar = false,
        };
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = box.Text;
            window.DialogResult = true;
        };
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
            Keyboard.Focus(box);
        };
        return window.ShowDialog() == true ? result : null;
    }
}
