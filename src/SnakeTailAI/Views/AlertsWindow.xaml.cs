using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SnakeTailAI.Core;
using SnakeTailAI.Services;
using Wpf.Ui.Controls;

namespace SnakeTailAI.Views;

/// <summary>Editable copy of an <see cref="AlertRule"/>.</summary>
public sealed partial class AlertRow : ObservableObject
{
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _pattern;
    [ObservableProperty] private bool _isRegex;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private int _levelIndex;
    [ObservableProperty] private bool _showNotification;
    [ObservableProperty] private bool _playSound;

    public AlertRow(AlertRule rule)
    {
        _enabled = rule.Enabled;
        _pattern = rule.Pattern;
        _isRegex = rule.IsRegex;
        _matchCase = rule.MatchCase;
        _levelIndex = (int)rule.MinLevel;
        _showNotification = rule.ShowNotification;
        _playSound = rule.PlaySound;
    }

    public AlertRule ToRule() => new()
    {
        Enabled = Enabled,
        Pattern = Pattern.Trim(),
        IsRegex = IsRegex,
        MatchCase = MatchCase,
        MinLevel = (AlertLevel)Math.Clamp(LevelIndex, 0, 2),
        ShowNotification = ShowNotification,
        PlaySound = PlaySound,
    };
}

public partial class AlertsWindow : FluentWindow
{
    private readonly ObservableCollection<AlertRow> _rows;
    private readonly AlertService _alerts;

    public AlertsWindow(IEnumerable<AlertRule> rules, bool muted, AlertService alerts)
    {
        InitializeComponent();
        _alerts = alerts;
        _rows = new ObservableCollection<AlertRow>(rules.Select(r => new AlertRow(r)));
        RulesList.ItemsSource = _rows;
        MutedBox.IsChecked = muted;
    }

    public List<AlertRule> Rules { get; private set; } = [];
    public bool Muted => MutedBox.IsChecked == true;

    private void OnAdd(object sender, RoutedEventArgs e) =>
        _rows.Add(new AlertRow(new AlertRule { MinLevel = AlertLevel.ErrorOrAbove }));

    private void OnTest(object sender, RoutedEventArgs e) => _alerts.Test();

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlertRow row)
            _rows.Remove(row);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            string? error = TextMatcher.Validate(row.Pattern, row.IsRegex);
            if (error != null)
            {
                System.Windows.MessageBox.Show(this, $"Invalid regular expression \"{row.Pattern}\":\n\n{error}", "Alerts",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
        }
        Rules = _rows.Select(r => r.ToRule()).ToList();
        DialogResult = true;
    }
}
