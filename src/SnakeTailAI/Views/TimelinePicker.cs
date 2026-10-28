using System.Windows;
using System.Windows.Controls;
using SnakeTailAI.ViewModels;
using Wpf.Ui.Controls;

namespace SnakeTailAI.Views;

/// <summary>Asks which devices to merge into a timeline. Devices open in a tab are checked to start with.</summary>
public static class TimelinePicker
{
    public static IReadOnlyList<string>? Show(Window owner, IEnumerable<DeviceViewModel> devices)
    {
        var candidates = devices.Where(d => d.HasLog && d.ActiveLog != null).ToList();
        var boxes = new List<(System.Windows.Controls.CheckBox Box, DeviceViewModel Device)>();
        var list = new StackPanel();
        foreach (var device in candidates)
        {
            var text = new System.Windows.Controls.TextBlock();
            text.Inlines.Add(new System.Windows.Documents.Run(device.Title) { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new System.Windows.Documents.Run($"   {device.Subtitle} · {device.SizeText} · {device.LastWriteText}")
            {
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorSecondaryBrush"),
                FontSize = 12,
            });
            var box = new System.Windows.Controls.CheckBox { Content = text, IsChecked = device.IsOpen, Margin = new Thickness(0, 3, 0, 3) };
            boxes.Add((box, device));
            list.Children.Add(box);
        }

        var ok = new Wpf.Ui.Controls.Button
        {
            Content = "Open timeline",
            Appearance = ControlAppearance.Primary,
            IsDefault = true,
            MinWidth = 120,
            Margin = new Thickness(0, 0, 8, 0),
            Icon = new SymbolIcon(SymbolRegular.Timeline24),
        };
        var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        void UpdateOk() => ok.IsEnabled = boxes.Count(b => b.Box.IsChecked == true) >= 2;
        foreach (var (box, _) in boxes)
        {
            box.Checked += (_, _) => UpdateOk();
            box.Unchecked += (_, _) => UpdateOk();
        }
        UpdateOk();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new DockPanel { Margin = new Thickness(20, 6, 20, 18) };
        var intro = new System.Windows.Controls.TextBlock
        {
            Text = "The lines of the chosen devices are shown together, in time order, each marked with its device. Choose at least two.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(intro);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 });

        var root = new DockPanel();
        var titleBar = new TitleBar { Title = "New timeline", ShowMaximize = false, ShowMinimize = false };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        root.Children.Add(panel);

        var window = new FluentWindow
        {
            Owner = owner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ExtendsContentIntoTitleBar = true,
            WindowBackdropType = WindowBackdropType.Mica,
            Content = root,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        if (window.ShowDialog() != true)
            return null;
        return boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Device.ActiveLog!).ToList();
    }
}
