using System.Windows.Media;
using SnakeTailAI.Core;

namespace SnakeTailAI.Services;

/// <summary>Colours used to paint log lines. One frozen instance per theme; <see cref="Current"/> follows the app theme.</summary>
public sealed class LogPalette
{
    public required bool IsDark { get; init; }
    public required Brush Background { get; init; }
    public required Brush Text { get; init; }
    public required Brush TextDim { get; init; }
    public required Brush LineNumber { get; init; }
    public required Brush DebugText { get; init; }
    public required Brush Debug { get; init; }
    public required Brush Info { get; init; }
    public required Brush Warning { get; init; }
    public required Brush Error { get; init; }
    public required Brush WarningText { get; init; }
    public required Brush ErrorText { get; init; }
    public required Brush WarningRow { get; init; }
    public required Brush ErrorRow { get; init; }
    public required Brush Source { get; init; }
    public required Brush Function { get; init; }
    public required Brush SeparatorText { get; init; }
    public required Brush SeparatorFill { get; init; }
    public required Pen SeparatorLine { get; init; }
    public required Brush FindMatch { get; init; }
    public required Brush Bookmark { get; init; }
    public required Brush Minimap { get; init; }
    public required Brush MinimapViewport { get; init; }
    public required Brush Header { get; init; }
    public required Brush HeaderText { get; init; }
    public required Pen Divider { get; init; }
    public required Brush[] Threads { get; init; }

    public Brush LevelBrush(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => Debug,
        LogLevel.Info => Info,
        LogLevel.Warning => Warning,
        LogLevel.Error or LogLevel.Fatal => Error,
        _ => TextDim,
    };

    public Brush MessageBrush(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => DebugText,
        LogLevel.Warning => WarningText,
        LogLevel.Error or LogLevel.Fatal => ErrorText,
        _ => Text,
    };

    /// <summary>A stable colour per thread id, so the eye can follow one thread.</summary>
    public Brush ThreadBrush(ReadOnlySpan<char> thread)
    {
        uint hash = 2166136261;
        foreach (char c in thread)
            hash = (hash ^ c) * 16777619;
        return Threads[hash % (uint)Threads.Length];
    }

    public static LogPalette Dark { get; } = new()
    {
        IsDark = true,
        Background = B("#1E1F22"),
        Text = B("#DCDFE4"),
        TextDim = B("#7D8590"),
        LineNumber = B("#4D525B"),
        DebugText = B("#AAB0B9"),
        Debug = B("#8A8F98"),
        Info = B("#4FC1FF"),
        Warning = B("#FFB454"),
        Error = B("#FF5C5C"),
        WarningText = B("#FFD08A"),
        ErrorText = B("#FF8A8A"),
        WarningRow = B("#14FFB454"),
        ErrorRow = B("#22FF5C5C"),
        Source = B("#A5ADE6"),
        Function = B("#67C7B8"),
        SeparatorText = B("#9AA3AE"),
        SeparatorFill = B("#2B2E35"),
        SeparatorLine = P("#33FFFFFF"),
        FindMatch = B("#73F5C518"),
        Bookmark = B("#A78BFA"),
        Minimap = B("#25272C"),
        MinimapViewport = B("#22FFFFFF"),
        Header = B("#24262B"),
        HeaderText = B("#8B949E"),
        Divider = P("#2E3036"),
        Threads = Bs("#7DD3FC", "#A5B4FC", "#F0ABFC", "#FDA4AF", "#FCD34D", "#86EFAC", "#5EEAD4", "#C4B5FD", "#FDBA74", "#93C5FD"),
    };

    public static LogPalette Light { get; } = new()
    {
        IsDark = false,
        Background = B("#FBFBFC"),
        Text = B("#1F2328"),
        TextDim = B("#6E7781"),
        LineNumber = B("#A6ADB6"),
        DebugText = B("#57606A"),
        Debug = B("#6E7781"),
        Info = B("#0969DA"),
        Warning = B("#B35900"),
        Error = B("#CF222E"),
        WarningText = B("#8A4600"),
        ErrorText = B("#B3141F"),
        WarningRow = B("#1AFFB454"),
        ErrorRow = B("#1ACF222E"),
        Source = B("#6639BA"),
        Function = B("#0E7C6B"),
        SeparatorText = B("#57606A"),
        SeparatorFill = B("#E8EAED"),
        SeparatorLine = P("#22000000"),
        FindMatch = B("#80FFD33D"),
        Bookmark = B("#7C3AED"),
        Minimap = B("#EEF0F2"),
        MinimapViewport = B("#22000000"),
        Header = B("#F0F1F3"),
        HeaderText = B("#57606A"),
        Divider = P("#E1E4E8"),
        Threads = Bs("#0369A1", "#4338CA", "#A21CAF", "#BE123C", "#A16207", "#15803D", "#0F766E", "#6D28D9", "#C2410C", "#1D4ED8"),
    };

    public static LogPalette Current { get; private set; } = Dark;

    public static event EventHandler? Changed;

    public static void SetCurrent(bool dark)
    {
        Current = dark ? Dark : Light;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static readonly BrushConverter Converter = new();
    private static readonly Dictionary<string, Brush?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses a <c>#RRGGBB</c> / <c>#AARRGGBB</c> colour from settings; null when empty or invalid.</summary>
    public static Brush? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        if (Cache.TryGetValue(hex, out var cached))
            return cached;
        Brush? brush = null;
        try
        {
            brush = (Brush?)Converter.ConvertFromInvariantString(hex.Trim());
            brush?.Freeze();
        }
        catch (FormatException)
        {
        }
        Cache[hex] = brush;
        return brush;
    }

    public static SolidColorBrush WithAlpha(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush B(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Brush[] Bs(params string[] hex) => hex.Select(h => (Brush)B(h)).ToArray();

    private static Pen P(string hex)
    {
        var pen = new Pen(B(hex), 1);
        pen.Freeze();
        return pen;
    }
}
