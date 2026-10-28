using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SnakeTailAI.Core;
using SnakeTailAI.Services;
using Wpf.Ui.Controls;

namespace SnakeTailAI.Views;

/// <summary>Editable copy of a <see cref="HighlightRule"/> with a live preview.</summary>
public sealed partial class RuleRow : ObservableObject
{
    public static IReadOnlyList<string> Colors { get; } =
        ["", "#FF6B6B", "#FF9F43", "#FFD166", "#4ADE80", "#2DD4BF", "#4FC1FF", "#818CF8", "#C084FC", "#F472B6", "#E5E7EB", "#9CA3AF"];

    public static IReadOnlyList<string> Backgrounds { get; } =
        ["", "#33FF5C5C", "#33FF9F43", "#33FFD166", "#334ADE80", "#332DD4BF", "#334FC1FF", "#33818CF8", "#33C084FC", "#33F472B6", "#22FFFFFF"];

    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _pattern = "";
    [ObservableProperty] private bool _isRegex;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private string _foreground = "";
    [ObservableProperty] private string _background = "";
    [ObservableProperty] private bool _bold;
    [ObservableProperty] private bool _wholeLine;

    public RuleRow(HighlightRule rule)
    {
        _enabled = rule.Enabled;
        _pattern = rule.Pattern;
        _isRegex = rule.IsRegex;
        _matchCase = rule.MatchCase;
        _foreground = rule.Foreground;
        _background = rule.Background;
        _bold = rule.Bold;
        _wholeLine = rule.WholeLine;
    }

    public string PreviewText => string.IsNullOrWhiteSpace(Pattern) ? "sample text" : (IsRegex ? "regex: " + Pattern : Pattern);
    public Brush PreviewForeground => LogPalette.Parse(Foreground) ?? LogPalette.Current.Text;
    public Brush? PreviewBackground => LogPalette.Parse(Background);
    public FontWeight PreviewWeight => Bold ? FontWeights.Bold : FontWeights.Normal;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Pattern) or nameof(IsRegex))
            OnPropertyChanged(nameof(PreviewText));
        else if (e.PropertyName == nameof(Foreground))
            OnPropertyChanged(nameof(PreviewForeground));
        else if (e.PropertyName == nameof(Background))
            OnPropertyChanged(nameof(PreviewBackground));
        else if (e.PropertyName == nameof(Bold))
            OnPropertyChanged(nameof(PreviewWeight));
    }

    public HighlightRule ToRule() => new()
    {
        Enabled = Enabled,
        Pattern = Pattern.Trim(),
        IsRegex = IsRegex,
        MatchCase = MatchCase,
        Foreground = Foreground.Trim(),
        Background = Background.Trim(),
        Bold = Bold,
        WholeLine = WholeLine,
    };
}

public partial class HighlightEditorWindow : FluentWindow
{
    private readonly ObservableCollection<RuleRow> _rows;

    public HighlightEditorWindow(IEnumerable<HighlightRule> rules)
    {
        InitializeComponent();
        _rows = new ObservableCollection<RuleRow>(rules.Select(r => new RuleRow(r)));
        RulesList.ItemsSource = _rows;
    }

    public List<HighlightRule> Rules { get; private set; } = [];

    private void OnAdd(object sender, RoutedEventArgs e) =>
        _rows.Add(new RuleRow(new HighlightRule { Foreground = "#FFD166", Bold = true }));

    private void OnDefaults(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        foreach (var rule in HighlightRule.Defaults())
            _rows.Add(new RuleRow(rule));
    }

    private void OnUp(object sender, RoutedEventArgs e) => Move(sender, -1);
    private void OnDown(object sender, RoutedEventArgs e) => Move(sender, 1);

    private void Move(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.Tag is not RuleRow row)
            return;
        int index = _rows.IndexOf(row);
        int target = index + delta;
        if (target >= 0 && target < _rows.Count)
            _rows.Move(index, target);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RuleRow row)
            _rows.Remove(row);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            string? error = TextMatcher.Validate(row.Pattern, row.IsRegex);
            if (error != null)
            {
                System.Windows.MessageBox.Show(this, $"Invalid regular expression \"{row.Pattern}\":\n\n{error}", "Highlight rules",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
        }
        Rules = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Pattern)).Select(r => r.ToRule()).ToList();
        DialogResult = true;
    }
}
