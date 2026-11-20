using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SnakeTailAI.Core;
using SnakeTailAI.Services;

namespace SnakeTailAI.Controls;

/// <summary>What view models may ask of the log view (navigation and selection).</summary>
public interface ILogViewport
{
    int CaretIndex { get; }
    void ScrollToIndex(int index, bool select);
    void ScrollToEnd();
    IReadOnlyList<LogLine> GetSelectedLines();
    void FocusView();
}

/// <summary>
/// Draws the visible lines of a <see cref="LogDocument"/> directly (only what is on screen), so millions of lines scroll smoothly.
/// Raw mode colours each field of the line; columns mode lays fields out in aligned columns.
/// A strip on the right shows where errors, warnings, bookmarks and find matches are.
/// </summary>
public sealed class LogViewControl : UserControl, ILogViewport
{
    private const double BookmarkWidth = 16;
    /// <summary>Width of the strip on the right: scrollbar and overview of errors, warnings, matches and bookmarks.</summary>
    private const double MinimapWidth = 18;
    private const double MinThumbHeight = 30;
    private const double TopPadding = 4;
    private const int FadeMs = 900;
    private const int MaxWrapChars = 10_000;

    private readonly Surface _surface;
    private readonly ScrollBar _hbar;
    private readonly DispatcherTimer _animTimer;
    private readonly List<(int Index, double Y, double Height)> _rows = [];

    private LogDocument? _doc;
    private int _count;
    private int _top;
    private long _topNumber;
    private int _hChar;
    private long _anchorNumber = -1;
    private long _caretNumber = -1;
    private bool _updatingBars;
    private bool _minimapDrag;
    private double _thumbGrab;
    private double _stripHoverY = double.NaN;
    private bool _selectDrag;
    private int _columnDrag = -1;
    private double _columnDragX;
    private int _columnDragWidth;
    private bool _suppressLineMenu;

    private Typeface _typeface = new("Consolas");
    private Typeface _uiTypeface = new("Segoe UI");
    private double _fontSize = 12.5;
    private double _charWidth = 7;
    private double _lineHeight = 18;
    private double _pixelsPerDip = 1;

    public LogViewControl()
    {
        _surface = new Surface(this) { Focusable = true, FocusVisualStyle = null, ClipToBounds = true };
        // The vertical scrollbar is drawn by the surface itself (the strip on the right), with a large thumb.
        _hbar = new ScrollBar { Orientation = Orientation.Horizontal, SmallChange = 4, Minimum = 0, Visibility = Visibility.Collapsed };
        _hbar.ValueChanged += OnHorizontalBarChanged;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(_surface);
        Grid.SetRow(_hbar, 1);
        grid.Children.Add(_hbar);
        Content = grid;

        _animTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _animTimer.Tick += (_, _) => _surface.InvalidateVisual();

        _surface.MouseLeftButtonDown += OnMouseLeftDown;
        _surface.MouseMove += OnMouseMove;
        _surface.MouseLeftButtonUp += OnMouseLeftUp;
        _surface.MouseRightButtonDown += OnMouseRightDown;
        _surface.MouseLeave += (_, _) =>
        {
            if (!double.IsNaN(_stripHoverY))
            {
                _stripHoverY = double.NaN;
                _surface.InvalidateVisual();
            }
        };
        ContextMenuOpening += (_, e) =>
        {
            if (_suppressLineMenu)
            {
                _suppressLineMenu = false;
                e.Handled = true;
            }
        };
        _surface.SizeChanged += (_, _) => Refresh(keepFollowing: true);

        Loaded += (_, _) =>
        {
            LogPalette.Changed += OnPaletteChanged;
            UpdateFont();
        };
        Unloaded += (_, _) =>
        {
            LogPalette.Changed -= OnPaletteChanged;
            _animTimer.Stop();
        };
    }

    // ---------------------------------------------------------------- dependency properties

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(LogDocument), typeof(LogViewControl), new PropertyMetadata(null, (d, e) => ((LogViewControl)d).OnDocumentChanged((LogDocument?)e.OldValue, (LogDocument?)e.NewValue)));

    public LogDocument? Document
    {
        get => (LogDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public static readonly DependencyProperty HighlighterProperty = DependencyProperty.Register(
        nameof(Highlighter), typeof(Highlighter), typeof(LogViewControl), new PropertyMetadata(Highlighter.Empty, Redraw));

    public Highlighter Highlighter
    {
        get => (Highlighter)GetValue(HighlighterProperty);
        set => SetValue(HighlighterProperty, value);
    }

    public static readonly DependencyProperty FindMatcherProperty = DependencyProperty.Register(
        nameof(FindMatcher), typeof(TextMatcher), typeof(LogViewControl), new PropertyMetadata(null, Redraw));

    public TextMatcher? FindMatcher
    {
        get => (TextMatcher?)GetValue(FindMatcherProperty);
        set => SetValue(FindMatcherProperty, value);
    }

    public static readonly DependencyProperty FindMarksProperty = DependencyProperty.Register(
        nameof(FindMarks), typeof(IReadOnlyList<int>), typeof(LogViewControl), new PropertyMetadata(null, Redraw));

    /// <summary>View indices of find matches (sorted). Replace the list instance, or bump <see cref="MarksVersion"/>, to redraw.</summary>
    public IReadOnlyList<int>? FindMarks
    {
        get => (IReadOnlyList<int>?)GetValue(FindMarksProperty);
        set => SetValue(FindMarksProperty, value);
    }

    public static readonly DependencyProperty MarksVersionProperty = DependencyProperty.Register(
        nameof(MarksVersion), typeof(int), typeof(LogViewControl), new PropertyMetadata(0, Redraw));

    public int MarksVersion
    {
        get => (int)GetValue(MarksVersionProperty);
        set => SetValue(MarksVersionProperty, value);
    }

    public static readonly DependencyProperty ColumnsModeProperty = DependencyProperty.Register(
        nameof(ColumnsMode), typeof(bool), typeof(LogViewControl), new PropertyMetadata(false, (d, _) => ((LogViewControl)d).Refresh(keepFollowing: true)));

    public bool ColumnsMode
    {
        get => (bool)GetValue(ColumnsModeProperty);
        set => SetValue(ColumnsModeProperty, value);
    }

    public static readonly DependencyProperty WordWrapProperty = DependencyProperty.Register(
        nameof(WordWrap), typeof(bool), typeof(LogViewControl), new PropertyMetadata(false, (d, _) => ((LogViewControl)d).Refresh(keepFollowing: true)));

    public bool WordWrap
    {
        get => (bool)GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    public static readonly DependencyProperty IsFollowingProperty = DependencyProperty.Register(
        nameof(IsFollowing), typeof(bool), typeof(LogViewControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) =>
        {
            if ((bool)e.NewValue)
                ((LogViewControl)d).ScrollToEnd();
        }));

    public bool IsFollowing
    {
        get => (bool)GetValue(IsFollowingProperty);
        set => SetValue(IsFollowingProperty, value);
    }

    public static readonly DependencyProperty IsPausedProperty = DependencyProperty.Register(
        nameof(IsPaused), typeof(bool), typeof(LogViewControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((LogViewControl)d).OnPausedChanged((bool)e.NewValue)));

    /// <summary>Freezes what is shown; new lines are still collected and appear on resume.</summary>
    public bool IsPaused
    {
        get => (bool)GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    public static readonly DependencyProperty LogFontFamilyProperty = DependencyProperty.Register(
        nameof(LogFontFamily), typeof(string), typeof(LogViewControl), new PropertyMetadata("Cascadia Mono, Consolas", (d, _) => ((LogViewControl)d).UpdateFont()));

    public string LogFontFamily
    {
        get => (string)GetValue(LogFontFamilyProperty);
        set => SetValue(LogFontFamilyProperty, value);
    }

    public static readonly DependencyProperty LogFontSizeProperty = DependencyProperty.Register(
        nameof(LogFontSize), typeof(double), typeof(LogViewControl),
        new FrameworkPropertyMetadata(12.5, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((LogViewControl)d).UpdateFont()));

    public double LogFontSize
    {
        get => (double)GetValue(LogFontSizeProperty);
        set => SetValue(LogFontSizeProperty, value);
    }

    public static readonly DependencyProperty PositionTextProperty = DependencyProperty.Register(
        nameof(PositionText), typeof(string), typeof(LogViewControl), new FrameworkPropertyMetadata(""));

    /// <summary>Where the view is, e.g. "Rows 12,345–12,390 of 1,234,567 · 37%". Read it with a OneWayToSource binding.</summary>
    public string PositionText
    {
        get => (string)GetValue(PositionTextProperty);
        set => SetValue(PositionTextProperty, value);
    }

    public static readonly DependencyProperty ColumnWidthsProperty = DependencyProperty.Register(
        nameof(ColumnWidths), typeof(string), typeof(LogViewControl),
        new FrameworkPropertyMetadata("15,5,9,34", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((LogViewControl)d).OnColumnWidthsChanged()));

    /// <summary>Widths of the Time, Level, Thread and Source columns in characters, e.g. "15,5,9,34".</summary>
    public string ColumnWidths
    {
        get => (string)GetValue(ColumnWidthsProperty);
        set => SetValue(ColumnWidthsProperty, value);
    }

    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing), typeof(double), typeof(LogViewControl), new PropertyMetadata(1.55, (d, _) => ((LogViewControl)d).UpdateFont()));

    /// <summary>Line height as a multiple of the font size.</summary>
    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    public static readonly DependencyProperty FadeInProperty = DependencyProperty.Register(
        nameof(FadeIn), typeof(bool), typeof(LogViewControl), new PropertyMetadata(true));

    public bool FadeIn
    {
        get => (bool)GetValue(FadeInProperty);
        set => SetValue(FadeInProperty, value);
    }

    public static readonly DependencyProperty SelectedLineProperty = DependencyProperty.Register(
        nameof(SelectedLine), typeof(LogLine), typeof(LogViewControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public LogLine? SelectedLine
    {
        get => (LogLine?)GetValue(SelectedLineProperty);
        set => SetValue(SelectedLineProperty, value);
    }

    public static readonly DependencyProperty ContextLineProperty = DependencyProperty.Register(
        nameof(ContextLine), typeof(LogLine), typeof(LogViewControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>The line under the mouse when the context menu was opened.</summary>
    public LogLine? ContextLine
    {
        get => (LogLine?)GetValue(ContextLineProperty);
        set => SetValue(ContextLineProperty, value);
    }

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(LogViewControl), new PropertyMetadata("Waiting for lines…", Redraw));

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>Double-click on a line.</summary>
    public event EventHandler<LogLine>? LineActivated;

    /// <summary>Click in the bookmark gutter.</summary>
    public event EventHandler<LogLine>? BookmarkToggleRequested;

    private static void Redraw(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LogViewControl)d)._surface.InvalidateVisual();

    // ---------------------------------------------------------------- document events

    private void OnDocumentChanged(LogDocument? oldDoc, LogDocument? newDoc)
    {
        if (oldDoc != null)
        {
            oldDoc.Appended -= OnAppended;
            oldDoc.ViewReset -= OnViewReset;
        }
        _doc = newDoc;
        if (newDoc != null)
        {
            newDoc.Appended += OnAppended;
            newDoc.ViewReset += OnViewReset;
        }
        _count = newDoc?.View.Count ?? 0;
        _top = 0;
        _anchorNumber = _caretNumber = -1;
        Refresh(keepFollowing: true);
    }

    private void OnAppended(object? sender, AppendResult e)
    {
        if (IsPaused || e.ViewAdded == 0)
            return;
        _count = _doc!.View.Count;
        Refresh(keepFollowing: true);
    }

    private void OnViewReset(object? sender, EventArgs e)
    {
        var view = _doc!.View;
        _count = view.Count;
        if (!IsFollowing)
        {
            // Keep the selected line in view if there is one, else the line that was at the top.
            int caret = _caretNumber > 0 ? _doc.FindViewIndex(_caretNumber) : -1;
            _top = caret >= 0 ? Math.Max(0, caret - Math.Max(1, VisibleRowEstimate() / 3)) : Math.Max(0, _doc.FindViewIndex(_topNumber));
        }
        Refresh(keepFollowing: true);
    }

    private void OnPausedChanged(bool paused)
    {
        if (!paused && _doc != null)
        {
            _count = _doc.View.Count;
            Refresh(keepFollowing: true);
        }
    }

    private void OnPaletteChanged(object? sender, EventArgs e) => _surface.InvalidateVisual();

    // ---------------------------------------------------------------- layout helpers

    private void UpdateFont()
    {
        _fontSize = Math.Clamp(LogFontSize, 7, 32);
        _typeface = new Typeface(new FontFamily(LogFontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _uiTypeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var probe = MakeText("MMMMMMMMMM", Brushes.Black);
        _charWidth = probe.WidthIncludingTrailingWhitespace / 10;
        _lineHeight = Math.Ceiling(_fontSize * Math.Clamp(LineSpacing, 1.1, 2.5));
        Refresh(keepFollowing: true);
    }

    private double HeaderHeight => ColumnsMode ? Math.Ceiling(_lineHeight + 6) : 0;
    private double SurfaceHeight => Math.Max(0, _surface.ActualHeight - TopPadding - HeaderHeight);

    private double NumberWidth
    {
        get
        {
            long max = _count > 0 ? _doc!.View[_count - 1].Number : 1;
            int digits = Math.Max(5, (int)Math.Floor(Math.Log10(Math.Max(1, max))) + 1);
            return digits * _charWidth;
        }
    }

    /// <summary>Where the device column of a timeline starts.</summary>
    private double OriginLeft => BookmarkWidth + NumberWidth + 16;
    private double OriginWidth => _doc is { OriginWidth: > 0 } doc ? (doc.OriginWidth + 1) * _charWidth + 8 : 0;
    private double TextLeft => OriginLeft + OriginWidth;
    private double TextWidth => Math.Max(_charWidth * 10, _surface.ActualWidth - TextLeft - MinimapWidth - 8);
    private int VisibleChars => Math.Max(1, (int)(TextWidth / _charWidth));

    // Column layout in characters (columns mode).
    // Columns mode: Time, Level, Thread, Source, then the message. Widths are in characters; Thread and Source can be 0 (hidden).
    private const int ColTime = 0, ColLevel = 1, ColThread = 2, ColSource = 3, ColMessage = 4, ColGap = 2, MaxColumnWidth = 120;
    private static readonly int[] DefaultColumns = [15, 5, 9, 34];
    private static readonly int[] MinColumns = [5, 1, 0, 0];
    private static readonly string[] ColumnNames = ["TIME", "LEVEL", "THREAD", "SOURCE", "MESSAGE"];
    private int[] _columns = [.. DefaultColumns];

    /// <summary>Start of a column, in characters from the text left edge (hidden columns take no space).</summary>
    private int ColumnStart(int column)
    {
        int x = 0;
        for (int i = 0; i < column; i++)
        {
            if (_columns[i] > 0)
                x += _columns[i] + ColGap;
        }
        return x;
    }

    private int MessageColumn => ColumnStart(ColMessage);

    /// <summary>X of the draggable edge after a column (middle of the gap).</summary>
    private double ColumnEdge(int column) =>
        TextLeft + (ColumnStart(column) + _columns[column]) * _charWidth + (_columns[column] > 0 ? ColGap * _charWidth / 2 : 0);

    private bool InHeader(Point pos) => ColumnsMode && pos.Y < TopPadding + HeaderHeight;

    /// <summary>The column whose right edge is under the pointer in the header, or -1.</summary>
    private int ColumnEdgeAt(Point pos)
    {
        if (!InHeader(pos))
            return -1;
        for (int i = ColSource; i >= ColTime; i--)
        {
            if (Math.Abs(pos.X - ColumnEdge(i)) <= 5)
                return i;
        }
        return -1;
    }

    private void OnColumnWidthsChanged()
    {
        var parts = (ColumnWidths ?? "").Split(',');
        var columns = new int[DefaultColumns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = i < parts.Length && int.TryParse(parts[i], out int w)
                ? Math.Clamp(w, MinColumns[i], MaxColumnWidth)
                : DefaultColumns[i];
        }
        _columns = columns;
        Refresh(keepFollowing: true);
    }

    private void CommitColumnWidths() => ColumnWidths = string.Join(",", _columns);

    private int VisibleRowEstimate() => Math.Max(1, (int)(SurfaceHeight / _lineHeight));

    private double RowHeight(int index)
    {
        var line = _doc!.View[index];
        if (line.IsSeparator)
            return Math.Ceiling(_lineHeight * 1.5);
        if (!WordWrap)
            return _lineHeight;
        string text = ColumnsMode ? MessageOf(line) : line.Raw;
        if (text.Length * _charWidth <= WrapWidth)
            return _lineHeight;
        var ft = MakeText(Cap(text), Brushes.Black);
        ft.MaxTextWidth = WrapWidth;
        ft.LineHeight = _lineHeight;
        return Math.Max(_lineHeight, ft.Height);
    }

    private double WrapWidth => ColumnsMode ? Math.Max(_charWidth * 10, TextWidth - MessageColumn * _charWidth) : TextWidth;

    /// <summary>The smallest top index that still fills the view (the "end" position).</summary>
    private int MaxTop()
    {
        if (_count == 0)
            return 0;
        double h = SurfaceHeight;
        int i = _count - 1;
        double used = 0;
        while (i >= 0)
        {
            used += RowHeight(i);
            if (used > h)
                break;
            i--;
        }
        return Math.Clamp(i + 1, 0, _count - 1);
    }

    /// <summary>Top index that makes <paramref name="index"/> the last visible row.</summary>
    private int TopForBottom(int index)
    {
        double h = SurfaceHeight;
        double used = 0;
        int i = index;
        while (i >= 0)
        {
            used += RowHeight(i);
            if (used > h)
                break;
            i--;
        }
        return Math.Clamp(i + 1, 0, Math.Max(0, _count - 1));
    }

    private void Refresh(bool keepFollowing)
    {
        if (_doc == null || (!IsLoaded && _surface.ActualHeight == 0))
        {
            _surface.InvalidateVisual();
            return;
        }

        _count = Math.Min(_count, _doc.View.Count);
        int maxTop = MaxTop();
        if (keepFollowing && IsFollowing && !IsPaused)
            _top = maxTop;
        _top = Math.Clamp(_top, 0, maxTop);
        _topNumber = _count > 0 ? _doc.View[_top].Number : 0;

        if (_count == 0)
        {
            PositionText = "";
        }
        else
        {
            int last = Math.Min(_count, _top + VisibleRowEstimate());
            PositionText = $"Rows {_top + 1:N0}–{last:N0} of {_count:N0} · {(long)last * 100 / _count}%";
        }

        _updatingBars = true;

        int maxChars = ColumnsMode ? Math.Max(0, _doc.MaxLineLength - 40) : _doc.MaxLineLength;
        int visible = ColumnsMode ? Math.Max(1, VisibleChars - MessageColumn) : VisibleChars;
        int hMax = WordWrap ? 0 : Math.Max(0, maxChars - visible + 4);
        _hbar.Maximum = hMax;
        _hbar.ViewportSize = visible;
        _hbar.LargeChange = Math.Max(1, visible / 2);
        _hChar = Math.Clamp(_hChar, 0, hMax);
        _hbar.Value = _hChar;
        _hbar.Visibility = hMax > 0 ? Visibility.Visible : Visibility.Collapsed;
        _updatingBars = false;

        _surface.InvalidateVisual();
    }

    private void SetTop(int top, bool userAction)
    {
        int maxTop = MaxTop();
        _top = Math.Clamp(top, 0, maxTop);
        if (userAction)
        {
            bool atEnd = _top >= maxTop;
            if (IsFollowing != atEnd && !IsPaused)
                IsFollowing = atEnd;
        }
        Refresh(keepFollowing: false);
    }

    private void OnHorizontalBarChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingBars)
            return;
        _hChar = (int)Math.Round(e.NewValue);
        _surface.InvalidateVisual();
    }

    // ---------------------------------------------------------------- ILogViewport

    public int CaretIndex => _caretNumber > 0 && _doc != null ? Math.Min(_doc.FindViewIndex(_caretNumber), _count - 1) : -1;

    public void ScrollToIndex(int index, bool select)
    {
        if (_doc == null || _count == 0)
            return;
        index = Math.Clamp(index, 0, _count - 1);
        if (select)
            SetCaret(index, extend: false);
        if (IsFollowing)
            IsFollowing = false;
        SetTop(index - VisibleRowEstimate() / 3, userAction: true);
    }

    public void ScrollToEnd()
    {
        if (_doc == null)
            return;
        if (!IsPaused)
            _count = _doc.View.Count;
        if (!IsFollowing)
            IsFollowing = true;
        Refresh(keepFollowing: true);
    }

    public IReadOnlyList<LogLine> GetSelectedLines()
    {
        if (_doc == null || _caretNumber < 0)
            return [];
        int a = _doc.FindViewIndex(_anchorNumber), c = _doc.FindViewIndex(_caretNumber);
        if (a < 0 || c < 0)
            return [];
        int from = Math.Min(a, c), to = Math.Min(Math.Max(a, c), _count - 1);
        var result = new List<LogLine>(to - from + 1);
        for (int i = from; i <= to; i++)
            result.Add(_doc.View[i]);
        return result;
    }

    public void FocusView() => _surface.Focus();

    public void SelectAll()
    {
        if (_doc == null || _count == 0)
            return;
        _anchorNumber = _doc.View[0].Number;
        _caretNumber = _doc.View[_count - 1].Number;
        SelectedLine = _doc.View[_count - 1];
        _surface.InvalidateVisual();
    }

    private void SetCaret(int index, bool extend)
    {
        var line = _doc!.View[index];
        _caretNumber = line.Number;
        if (!extend || _anchorNumber < 0)
            _anchorNumber = line.Number;
        SelectedLine = line;
        _surface.InvalidateVisual();
    }

    private void EnsureVisible(int index)
    {
        if (index < _top)
            SetTop(index, userAction: true);
        else if (_rows.Count > 0 && index > _rows[^1].Index - (_rows[^1].Y + _rows[^1].Height > _surface.ActualHeight ? 1 : 0))
            SetTop(TopForBottom(index), userAction: true);
    }

    private (int From, int To) SelectionRange()
    {
        if (_caretNumber < 0 || _doc == null)
            return (-1, -2);
        int a = _doc.FindViewIndex(_anchorNumber), c = _doc.FindViewIndex(_caretNumber);
        return (Math.Min(a, c), Math.Max(a, c));
    }

    // ---------------------------------------------------------------- input

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            LogFontSize = Math.Clamp(LogFontSize + (e.Delta > 0 ? 0.5 : -0.5), 8, 28);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _hChar = Math.Clamp(_hChar - Math.Sign(e.Delta) * 8, 0, (int)_hbar.Maximum);
            Refresh(keepFollowing: false);
            e.Handled = true;
            return;
        }
        int lines = Math.Max(1, SystemParameters.WheelScrollLines) * e.Delta / 120;
        SetTop(_top - lines, userAction: true);
        e.Handled = true;
    }

    private int RowAt(double y)
    {
        foreach (var row in _rows)
        {
            if (y >= row.Y && y < row.Y + row.Height)
                return row.Index;
        }
        if (_rows.Count == 0)
            return -1;
        return y < _rows[0].Y ? Math.Max(0, _rows[0].Index - 1) : Math.Min(_count - 1, _rows[^1].Index + 1);
    }

    private bool InMinimap(double x) => x >= _surface.ActualWidth - MinimapWidth - 2;

    private int RowsOnScreen => Math.Max(1, _rows.Count > 0 ? _rows.Count : VisibleRowEstimate());

    /// <summary>
    /// The thumb of the strip: its centre sits where the visible rows are (the same scale as the marks), and it is
    /// never shorter than <see cref="MinThumbHeight"/>, so it stays easy to see and grab even with millions of lines.
    /// </summary>
    private (double Top, double Height) Thumb()
    {
        double track = Math.Max(1, _surface.ActualHeight - 4);
        int count = Math.Max(1, _count);
        double height = Math.Clamp(track * RowsOnScreen / count, Math.Min(MinThumbHeight, track), track);
        double center = track * (_top + RowsOnScreen / 2.0) / count;
        return (2 + Math.Clamp(center - height / 2, 0, track - height), height);
    }

    /// <summary>Scrolls so that the thumb's top edge is at <paramref name="thumbTop"/>.</summary>
    private void MoveThumbTo(double thumbTop)
    {
        if (_count == 0)
            return;
        double track = Math.Max(1, _surface.ActualHeight - 4);
        var (_, height) = Thumb();
        double center = thumbTop - 2 + height / 2;
        int top = (int)Math.Round(center / track * _count - RowsOnScreen / 2.0);
        SetTop(top, userAction: true);
    }

    /// <summary>The view index at a height of the strip.</summary>
    private int IndexAtStrip(double y)
    {
        double track = Math.Max(1, _surface.ActualHeight - 4);
        return Math.Clamp((int)((y - 2) / track * _count), 0, Math.Max(0, _count - 1));
    }

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        _surface.Focus();
        var pos = e.GetPosition(_surface);
        if (InHeader(pos))
        {
            int edge = ColumnEdgeAt(pos);
            if (edge >= 0 && e.ClickCount == 2)
            {
                _columns[edge] = DefaultColumns[edge];
                CommitColumnWidths();
            }
            else if (edge >= 0)
            {
                _columnDrag = edge;
                _columnDragX = pos.X;
                _columnDragWidth = _columns[edge];
                _surface.CaptureMouse();
            }
            e.Handled = true;
            return;
        }
        if (InMinimap(pos.X))
        {
            // Grab the thumb where it was clicked, or bring it under the pointer when clicking elsewhere on the strip.
            var (thumbTop, thumbHeight) = Thumb();
            if (pos.Y >= thumbTop && pos.Y <= thumbTop + thumbHeight)
            {
                _thumbGrab = pos.Y - thumbTop;
            }
            else
            {
                _thumbGrab = thumbHeight / 2;
                MoveThumbTo(pos.Y - _thumbGrab);
            }
            _minimapDrag = true;
            _surface.CaptureMouse();
            _surface.InvalidateVisual();
            e.Handled = true;
            return;
        }

        int index = RowAt(pos.Y);
        if (index < 0 || _doc == null)
            return;
        var line = _doc.View[index];

        if (pos.X < BookmarkWidth + 4 && !line.IsSeparator)
        {
            BookmarkToggleRequested?.Invoke(this, line);
            _surface.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            SetCaret(index, extend: false);
            LineActivated?.Invoke(this, line);
            e.Handled = true;
            return;
        }

        SetCaret(index, extend: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        _selectDrag = true;
        _surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_surface);
        if (_columnDrag >= 0)
        {
            int delta = (int)Math.Round((pos.X - _columnDragX) / _charWidth);
            int width = Math.Clamp(_columnDragWidth + delta, MinColumns[_columnDrag], MaxColumnWidth);
            if (width != _columns[_columnDrag])
            {
                _columns[_columnDrag] = width;
                Refresh(keepFollowing: false);
            }
            return;
        }
        _surface.Cursor = ColumnEdgeAt(pos) >= 0 ? Cursors.SizeWE
            : InMinimap(pos.X) || (pos.X < BookmarkWidth + 4 && !InHeader(pos)) ? Cursors.Hand
            : Cursors.Arrow;
        if (_minimapDrag)
        {
            MoveThumbTo(pos.Y - _thumbGrab);
            return;
        }
        bool overStrip = InMinimap(pos.X) && _count > 0;
        if (overStrip || !double.IsNaN(_stripHoverY))
        {
            _stripHoverY = overStrip ? pos.Y : double.NaN;
            _surface.InvalidateVisual();
        }
        if (!_selectDrag || _count == 0)
            return;
        if (pos.Y < HeaderHeight + TopPadding)
            SetTop(_top - 1, userAction: true);
        else if (pos.Y > _surface.ActualHeight)
            SetTop(_top + 1, userAction: true);
        int index = RowAt(pos.Y);
        if (index >= 0)
            SetCaret(index, extend: true);
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_columnDrag >= 0)
        {
            _columnDrag = -1;
            CommitColumnWidths();
        }
        if (_minimapDrag)
            _surface.InvalidateVisual();
        _minimapDrag = false;
        _selectDrag = false;
        _surface.ReleaseMouseCapture();
    }

    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        _surface.Focus();
        if (InHeader(e.GetPosition(_surface)))
        {
            // The header has its own menu instead of the line menu.
            _suppressLineMenu = true;
            var reset = new MenuItem { Header = "Reset column widths" };
            reset.Click += (_, _) =>
            {
                _columns = [.. DefaultColumns];
                CommitColumnWidths();
            };
            var menu = new ContextMenu { PlacementTarget = _surface };
            menu.Items.Add(reset);
            menu.IsOpen = true;
            e.Handled = true;
            return;
        }
        int index = RowAt(e.GetPosition(_surface).Y);
        if (index < 0 || _doc == null)
        {
            ContextLine = null;
            return;
        }
        var (from, to) = SelectionRange();
        if (index < from || index > to)
            SetCaret(index, extend: false);
        ContextLine = _doc.View[index];
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_surface.IsKeyboardFocused || _doc == null)
            return;

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        int caret = CaretIndex;
        int page = Math.Max(1, _rows.Count - 1);

        switch (e.Key)
        {
            case Key.Up: MoveCaret(caret < 0 ? _top : caret - 1, shift); break;
            case Key.Down: MoveCaret(caret < 0 ? _top : caret + 1, shift); break;
            case Key.PageUp: MoveCaret((caret < 0 ? _top : caret) - page, shift); break;
            case Key.PageDown: MoveCaret((caret < 0 ? _top : caret) + page, shift); break;
            case Key.Home: MoveCaret(0, shift); break;
            case Key.End:
                if (shift)
                    MoveCaret(_count - 1, true);
                else
                    ScrollToEnd();
                break;
            case Key.Left:
                _hChar = Math.Max(0, _hChar - 4);
                Refresh(keepFollowing: false);
                break;
            case Key.Right:
                _hChar = Math.Min((int)_hbar.Maximum, _hChar + 4);
                Refresh(keepFollowing: false);
                break;
            case Key.A when ctrl: SelectAll(); break;
            case Key.C when ctrl: CopySelection(); break;
            case Key.Space when !ctrl: IsPaused = !IsPaused; break;
            case Key.Escape:
                _anchorNumber = _caretNumber = -1;
                SelectedLine = null;
                _surface.InvalidateVisual();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void MoveCaret(int index, bool extend)
    {
        if (_count == 0)
            return;
        index = Math.Clamp(index, 0, _count - 1);
        SetCaret(index, extend);
        EnsureVisible(index);
    }

    public void CopySelection()
    {
        var lines = GetSelectedLines();
        if (lines.Count == 0)
            return;
        string text = string.Join(Environment.NewLine, lines.Select(ViewModels.LogTabViewModel.TextOf));
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy (another app holds it); copying can simply be retried.
        }
    }

    // ---------------------------------------------------------------- rendering

    private FormattedText MakeText(string text, Brush brush, Typeface? typeface = null, double? size = null) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface ?? _typeface, size ?? _fontSize, brush, null, TextFormattingMode.Display, _pixelsPerDip);

    private static string Cap(string text) => text.Length > MaxWrapChars ? text[..MaxWrapChars] : text;

    private static string MessageOf(LogLine line) =>
        line.Kind == LineKind.Continuation || line.TimeLength == 0 ? line.Raw : line.MessageText;

    private static int MessageOffset(LogLine line) =>
        line.Kind == LineKind.Continuation || line.TimeLength == 0 ? 0 : line.MessageStart;

    private void Render(DrawingContext dc)
    {
        var p = LogPalette.Current;
        double width = _surface.ActualWidth, height = _surface.ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        _rows.Clear();

        if (_doc == null || _count == 0)
        {
            RenderEmpty(dc, p, width, height);
            return;
        }

        var accent = ThemeService.Accent;
        var selectionBrush = LogPalette.WithAlpha(accent, p.IsDark ? (byte)0x40 : (byte)0x30);
        var accentBrush = LogPalette.WithAlpha(accent, 0xFF);
        var highlighter = Highlighter;
        var find = FindMatcher;
        var (selFrom, selTo) = SelectionRange();
        long now = Environment.TickCount64;
        bool animating = false;

        // When lines pour in, everything on screen is "new": the glow would only tint the whole view, so skip it.
        bool fade = FadeIn;
        if (fade)
        {
            int fresh = 0, rows = VisibleRowEstimate();
            for (int i = Math.Max(_top, _count - rows); i < _count; i++)
            {
                if (_doc.View[i].ArrivedTicks > 0 && now - _doc.View[i].ArrivedTicks < FadeMs)
                    fresh++;
            }
            fade = fresh <= Math.Max(4, rows / 4);
        }

        double numberWidth = NumberWidth;
        double textLeft = TextLeft;
        double textRight = width - MinimapWidth - 6;
        double y = TopPadding + HeaderHeight;

        // Gutter divider
        dc.DrawLine(p.Divider, new Point(BookmarkWidth + numberWidth + 8.5, 0), new Point(BookmarkWidth + numberWidth + 8.5, height));

        for (int i = _top; i < _count && y < height; i++)
        {
            var line = _doc.View[i];
            double rowHeight = RowHeight(i);
            var rowRect = new Rect(0, y, textRight, rowHeight);
            _rows.Add((i, y, rowHeight));

            if (line.IsSeparator)
            {
                RenderSeparator(dc, p, line, rowRect, textLeft);
                if (i >= selFrom && i <= selTo)
                    dc.DrawRectangle(selectionBrush, null, rowRect);
                y += rowHeight;
                continue;
            }

            var (lineRule, spans) = highlighter.IsEmpty ? (null, null) : highlighter.Evaluate(line.Raw);

            // Row background: rule, level tint
            var rowBackground = LogPalette.Parse(lineRule?.Background) ?? (line.Level >= LogLevel.Error ? p.ErrorRow : line.Level == LogLevel.Warning ? p.WarningRow : null);
            if (rowBackground != null)
                dc.DrawRectangle(rowBackground, null, new Rect(textLeft - 6, y, textRight - textLeft + 6, rowHeight));

            // Fade-in for lines that just arrived
            if (fade && line.ArrivedTicks > 0)
            {
                long age = now - line.ArrivedTicks;
                if (age < FadeMs)
                {
                    byte alpha = (byte)(60 * (1 - age / (double)FadeMs));
                    dc.DrawRectangle(LogPalette.WithAlpha(accent, alpha), null, new Rect(textLeft - 6, y, textRight - textLeft + 6, rowHeight));
                    animating = true;
                }
            }

            bool selected = i >= selFrom && i <= selTo;
            if (selected)
            {
                dc.DrawRectangle(selectionBrush, null, rowRect);
                dc.DrawRectangle(accentBrush, null, new Rect(textLeft - 6, y, 2, rowHeight));
            }

            // Bookmark marker
            if (line.IsBookmarked)
            {
                dc.DrawRoundedRectangle(p.Bookmark, null, new Rect(4, y + 3, 8, Math.Max(4, _lineHeight - 6)), 3, 3);
            }

            // Line number
            var number = MakeText(line.Number.ToString(CultureInfo.InvariantCulture), selected ? p.TextDim : p.LineNumber);
            number.LineHeight = _lineHeight;
            dc.DrawText(number, new Point(BookmarkWidth + numberWidth - number.Width, y));

            // Level bar for warnings / errors
            if (line.Level >= LogLevel.Warning)
                dc.DrawRectangle(p.LevelBrush(line.Level), null, new Rect(BookmarkWidth + numberWidth + 11, y, 3, rowHeight));

            // Device of the line, in a timeline
            if (line.Origin != null && OriginWidth > 0)
            {
                var origin = MakeText(line.Origin, p.ThreadBrush(line.Origin));
                origin.LineHeight = _lineHeight;
                origin.SetFontWeight(FontWeights.SemiBold);
                dc.DrawText(origin, new Point(OriginLeft, y));
            }

            if (ColumnsMode)
                RenderColumns(dc, p, line, textLeft, y, textRight, lineRule, spans, find, rowHeight);
            else
                RenderRaw(dc, p, line, textLeft, y, textRight, lineRule, spans, find);

            y += rowHeight;
        }

        if (ColumnsMode)
            RenderHeader(dc, p, textLeft, width);
        RenderMinimap(dc, p, width, height, accent);

        if (animating && !_animTimer.IsEnabled)
            _animTimer.Start();
        else if (!animating && _animTimer.IsEnabled)
            _animTimer.Stop();
    }

    private void RenderRaw(DrawingContext dc, LogPalette p, LogLine line, double x, double y, double right,
        HighlightRule? lineRule, List<HighlightSpan>? spans, TextMatcher? find)
    {
        string raw = line.Raw;
        int winStart, winLength;
        string text;
        if (WordWrap)
        {
            text = Cap(raw);
            winStart = 0;
            winLength = text.Length;
        }
        else
        {
            winStart = Math.Min(_hChar, raw.Length);
            winLength = Math.Min(raw.Length - winStart, VisibleChars + 1);
            text = raw.Substring(winStart, winLength);
        }
        if (text.Length == 0)
            return;

        var ruleFg = LogPalette.Parse(lineRule?.Foreground);
        var ft = MakeText(text, ruleFg ?? p.MessageBrush(line.Level));
        ft.LineHeight = _lineHeight;
        if (WordWrap)
            ft.MaxTextWidth = right - x;
        if (lineRule?.Bold == true)
            ft.SetFontWeight(FontWeights.SemiBold);

        if (ruleFg == null && line.Kind == LineKind.Normal)
        {
            // Field colours
            ApplyStyle(ft, winStart, winLength, 0, line.TimeLength, p.TextDim, false);
            ApplyStyle(ft, winStart, winLength, line.LevelStart, line.LevelLength, p.LevelBrush(line.Level), true);
            if (line.ThreadLength > 0)
                ApplyStyle(ft, winStart, winLength, line.ThreadStart - 1, line.ThreadLength + 2, p.ThreadBrush(line.ThreadSpan), false);
            if (line.SourceLineLength > 0)
                ApplyStyle(ft, winStart, winLength, line.SourceLineStart - 5, line.SourceLineLength + 8, p.TextDim, false);
            if (line.SourceLength > 0)
                ApplyStyle(ft, winStart, winLength, line.SourceStart, line.SourceLength, p.Source, false);
            if (line.FunctionLength > 0)
                ApplyStyle(ft, winStart, winLength, line.SourceStart + line.SourceLength, line.FunctionStart + line.FunctionLength - line.SourceStart - line.SourceLength + 2, p.Function, false);
        }

        var origin = new Point(x, y);
        DrawSpans(dc, ft, origin, winStart, winLength, 0, spans, find, raw);
        dc.DrawText(ft, origin);
    }

    private void RenderColumns(DrawingContext dc, LogPalette p, LogLine line, double x, double y, double right,
        HighlightRule? lineRule, List<HighlightSpan>? spans, TextMatcher? find, double rowHeight)
    {
        var ruleFg = LogPalette.Parse(lineRule?.Foreground);
        double cw = _charWidth;

        if (line.Kind == LineKind.Normal && line.TimeLength > 0)
        {
            // A Time column wide enough for the whole timestamp shows the date too.
            int timeWidth = _columns[ColTime];
            string time = timeWidth >= line.TimeLength || line.TimeLength < 19
                ? line.TimeText
                : line.Raw.Substring(11, Math.Min(timeWidth, line.TimeLength - 11));
            DrawCell(dc, time, ruleFg ?? p.TextDim, x, y, timeWidth);

            double lx = x + ColumnStart(ColLevel) * cw;
            if (line.Level != LogLevel.None)
            {
                var levelBrush = p.LevelBrush(line.Level);
                var pillColor = ((SolidColorBrush)levelBrush).Color;
                var pill = new Rect(lx - 3, y + 2, _columns[ColLevel] * cw + 6, _lineHeight - 4);
                dc.DrawRoundedRectangle(LogPalette.WithAlpha(pillColor, 0x2E), null, pill, 4, 4);
                string name = line.Level.ShortName();
                var lt = MakeText(name.Length <= _columns[ColLevel] ? name : name[..1], levelBrush, size: _fontSize * 0.86);
                lt.SetFontWeight(FontWeights.SemiBold);
                dc.DrawText(lt, new Point(pill.X + (pill.Width - lt.Width) / 2, pill.Y + (pill.Height - lt.Height) / 2));
            }

            double tx = x + ColumnStart(ColThread) * cw;
            if (line.ThreadLength > 0 && _columns[ColThread] > 0)
                DrawCell(dc, line.ThreadText, ruleFg ?? p.ThreadBrush(line.ThreadSpan), tx, y, _columns[ColThread]);

            double sx = x + ColumnStart(ColSource) * cw;
            if ((line.SourceLength > 0 || line.FunctionLength > 0) && _columns[ColSource] > 0)
            {
                string src = line.SourceText + (line.SourceLineLength > 0 ? ":" + line.SourceLineText : "");
                string fn = line.FunctionLength > 0 ? " › " + line.FunctionText : "";
                string cell = Fit(src + fn, _columns[ColSource]);
                var ft = MakeText(cell, ruleFg ?? p.Source);
                ft.LineHeight = _lineHeight;
                int fnAt = Math.Min(cell.Length, src.Length);
                if (ruleFg == null && fnAt < cell.Length)
                    ft.SetForegroundBrush(p.Function, fnAt, cell.Length - fnAt);
                dc.DrawText(ft, new Point(sx, y));
            }
        }

        double mx = x + MessageColumn * _charWidth;
        string message = MessageOf(line);
        int offset = MessageOffset(line);
        int winStart, winLength;
        string text;
        if (WordWrap)
        {
            text = Cap(message);
            winStart = 0;
            winLength = text.Length;
        }
        else
        {
            int visible = Math.Max(1, (int)((right - mx) / cw));
            winStart = Math.Min(_hChar, message.Length);
            winLength = Math.Min(message.Length - winStart, visible + 1);
            text = message.Substring(winStart, winLength);
        }
        if (text.Length == 0)
            return;

        var mt = MakeText(text, ruleFg ?? p.MessageBrush(line.Level));
        mt.LineHeight = _lineHeight;
        if (WordWrap)
            mt.MaxTextWidth = Math.Max(cw * 10, right - mx);
        if (lineRule?.Bold == true)
            mt.SetFontWeight(FontWeights.SemiBold);
        var origin = new Point(mx, y);
        DrawSpans(dc, mt, origin, winStart, winLength, offset, spans, find, line.Raw);
        dc.DrawText(mt, origin);
    }

    private void DrawCell(DrawingContext dc, string text, Brush brush, double x, double y, int width)
    {
        var ft = MakeText(Fit(text, width), brush);
        ft.LineHeight = _lineHeight;
        dc.DrawText(ft, new Point(x, y));
    }

    private static string Fit(string text, int width) =>
        width <= 0 ? "" : text.Length <= width ? text : width == 1 ? "…" : text[..(width - 1)] + "…";

    /// <summary>
    /// Applies highlight-rule spans and draws find-match backgrounds. Spans are positions in <paramref name="raw"/>;
    /// <paramref name="offset"/> is where the drawn text starts in raw, <paramref name="winStart"/> the horizontal scroll.
    /// </summary>
    private void DrawSpans(DrawingContext dc, FormattedText ft, Point origin, int winStart, int winLength, int offset,
        List<HighlightSpan>? spans, TextMatcher? find, string raw)
    {
        if (spans != null)
        {
            foreach (var span in spans)
            {
                int start = span.Start - offset;
                var fg = LogPalette.Parse(span.Rule.Foreground);
                var bg = LogPalette.Parse(span.Rule.Background);
                if (bg != null)
                    DrawRangeBackground(dc, ft, origin, winStart, winLength, start, span.Length, bg);
                ApplyStyle(ft, winStart, winLength, start, span.Length, fg, span.Rule.Bold);
            }
        }

        if (find != null)
        {
            var findBrush = LogPalette.Current.FindMatch;
            foreach (var (start, length) in find.Matches(raw, 64))
                DrawRangeBackground(dc, ft, origin, winStart, winLength, start - offset, length, findBrush);
        }
    }

    private static void DrawRangeBackground(DrawingContext dc, FormattedText ft, Point origin, int winStart, int winLength, int start, int length, Brush brush)
    {
        int s = Math.Max(start, winStart), e = Math.Min(start + length, winStart + winLength);
        if (e <= s)
            return;
        var geometry = ft.BuildHighlightGeometry(origin, s - winStart, e - s);
        if (geometry != null)
            dc.DrawRoundedRectangle(brush, null, geometry.Bounds, 2, 2);
    }

    private static void ApplyStyle(FormattedText ft, int winStart, int winLength, int start, int length, Brush? fg, bool bold)
    {
        int s = Math.Max(start, winStart), e = Math.Min(start + length, winStart + winLength);
        if (e <= s)
            return;
        if (fg != null)
            ft.SetForegroundBrush(fg, s - winStart, e - s);
        if (bold)
            ft.SetFontWeight(FontWeights.SemiBold, s - winStart, e - s);
    }

    private void RenderSeparator(DrawingContext dc, LogPalette p, LogLine line, Rect row, double textLeft)
    {
        var ft = MakeText(line.Origin != null ? $"{line.Origin} · {line.Raw}" : line.Raw, p.SeparatorText, _uiTypeface, _fontSize * 0.9);
        ft.SetFontWeight(FontWeights.SemiBold);
        double mid = row.Y + row.Height / 2;
        double centerX = textLeft + (row.Right - textLeft) / 2;
        var pill = new Rect(centerX - ft.Width / 2 - 12, mid - ft.Height / 2 - 3, ft.Width + 24, ft.Height + 6);
        dc.DrawLine(p.SeparatorLine, new Point(textLeft, Math.Floor(mid) + 0.5), new Point(pill.Left - 6, Math.Floor(mid) + 0.5));
        dc.DrawLine(p.SeparatorLine, new Point(pill.Right + 6, Math.Floor(mid) + 0.5), new Point(row.Right, Math.Floor(mid) + 0.5));
        dc.DrawRoundedRectangle(p.SeparatorFill, null, pill, pill.Height / 2, pill.Height / 2);
        dc.DrawText(ft, new Point(pill.X + 12, pill.Y + 3));
    }

    private void RenderHeader(DrawingContext dc, LogPalette p, double x, double width)
    {
        double h = HeaderHeight;
        dc.DrawRectangle(p.Header, null, new Rect(0, 0, width - MinimapWidth - 4, h + TopPadding));
        dc.DrawLine(p.Divider, new Point(0, h + TopPadding - 0.5), new Point(width - MinimapWidth - 4, h + TopPadding - 0.5));
        double cw = _charWidth;
        void Label(string text, double cx)
        {
            var ft = MakeText(text, p.HeaderText, _uiTypeface, _fontSize * 0.82);
            ft.SetFontWeight(FontWeights.SemiBold);
            dc.DrawText(ft, new Point(cx, TopPadding + (h - ft.Height) / 2));
        }
        Label("#", BookmarkWidth);
        if (OriginWidth > 0)
            Label("DEVICE", OriginLeft);
        for (int i = ColTime; i <= ColMessage; i++)
        {
            if (i < ColMessage && _columns[i] == 0)
                continue;
            int room = i < ColMessage ? _columns[i] : 7;
            string name = ColumnNames[i];
            if (name.Length > room + 1)
                name = room >= 2 ? name[..room] : "";
            Label(name, x + ColumnStart(i) * cw);
        }
        // Edges that can be dragged to resize a column.
        double top = TopPadding + 4, bottom = TopPadding + h - 4;
        for (int i = ColTime; i <= ColSource; i++)
        {
            double edge = Math.Round(ColumnEdge(i)) + 0.5;
            var pen = i == _columnDrag ? new Pen(LogPalette.WithAlpha(ThemeService.Accent, 0xFF), 2) : p.Divider;
            dc.DrawLine(pen, new Point(edge, top), new Point(edge, bottom));
        }
    }

    private void RenderEmpty(DrawingContext dc, LogPalette p, double width, double height)
    {
        var icon = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            44, p.LineNumber, _pixelsPerDip);
        var text = MakeText(EmptyText, p.TextDim, _uiTypeface, 14);
        double total = icon.Height + 10 + text.Height;
        double top = (height - total) / 2;
        dc.DrawText(icon, new Point((width - icon.Width) / 2, top));
        dc.DrawText(text, new Point((width - text.Width) / 2, top + icon.Height + 10));
    }

    private void RenderMinimap(DrawingContext dc, LogPalette p, double width, double height, Color accent)
    {
        double x = width - MinimapWidth;
        var track = new Rect(x, 2, MinimapWidth - 2, height - 4);
        dc.DrawRoundedRectangle(p.Minimap, p.Divider, track, 5, 5);
        if (_count == 0)
            return;

        DrawMarks(dc, _doc!.WarningMarks, p.Warning, x + 3, height);
        DrawMarks(dc, _doc.ErrorMarks, p.Error, x + 3, height);
        if (FindMarks is { Count: > 0 } find)
            DrawMarks(dc, find, LogPalette.WithAlpha(Color.FromRgb(0xF5, 0xC5, 0x18), 0xFF), x + 3, height);
        DrawMarks(dc, _doc.BookmarkMarks, p.Bookmark, x + 3, height);

        // Thumb: nearly solid, with a ring in the background colour so it stands out even over a strip full of marks;
        // brighter when hovered or dragged.
        var (thumbTop, thumbHeight) = Thumb();
        bool hot = _minimapDrag || (!double.IsNaN(_stripHoverY) && _stripHoverY >= thumbTop && _stripHoverY <= thumbTop + thumbHeight);
        byte fill = _minimapDrag ? (byte)0xF0 : hot ? (byte)0xD8 : (byte)0xB8;
        var thumb = new Rect(x + 1, thumbTop, MinimapWidth - 4, thumbHeight);
        dc.DrawRoundedRectangle(null, new Pen(p.Background, 3), thumb, 5, 5);
        dc.DrawRoundedRectangle(LogPalette.WithAlpha(accent, fill), new Pen(LogPalette.WithAlpha(accent, 0xFF), 1.5), thumb, 5, 5);

        // Grip lines in the middle of the thumb.
        if (thumbHeight >= 24)
        {
            var grip = new Pen(LogPalette.WithAlpha(Colors.White, 0xB0), 1.2);
            double cy = thumbTop + thumbHeight / 2;
            for (int i = -1; i <= 1; i++)
                dc.DrawLine(grip, new Point(thumb.Left + 4, Math.Round(cy + i * 3) + 0.5), new Point(thumb.Right - 4, Math.Round(cy + i * 3) + 0.5));
        }

        // Where am I: line and time, at the pointer while hovering, at the top of the view while dragging.
        if (_minimapDrag || !double.IsNaN(_stripHoverY))
        {
            int index = _minimapDrag ? Math.Min(_top, _count - 1) : IndexAtStrip(_stripHoverY);
            double anchor = _minimapDrag ? thumbTop + thumbHeight / 2 : _stripHoverY;
            RenderPositionLabel(dc, p, accent, _doc.View[index], x - 8, anchor, height);
        }
    }

    private void RenderPositionLabel(DrawingContext dc, LogPalette p, Color accent, LogLine line, double right, double anchorY, double height)
    {
        string text = line.Timestamp is { } time
            ? $"Line {line.Number:N0} · {time:HH:mm:ss}"
            : $"Line {line.Number:N0}";
        var ft = MakeText(text, p.Text, _uiTypeface, 12);
        ft.SetFontWeight(FontWeights.SemiBold);
        double w = ft.Width + 18, h = ft.Height + 8;
        double y = Math.Clamp(anchorY - h / 2, 2, Math.Max(2, height - h - 2));
        var box = new Rect(right - w, y, w, h);
        dc.DrawRoundedRectangle(p.SeparatorFill, new Pen(LogPalette.WithAlpha(accent, 0xC0), 1), box, 6, 6);
        dc.DrawText(ft, new Point(box.X + 9, box.Y + 4));
    }

    /// <summary>One tick per 2px band that contains at least one mark (binary search per band, so huge lists stay cheap).</summary>
    private void DrawMarks(DrawingContext dc, IReadOnlyList<int> marks, Brush brush, double x, double height)
    {
        if (marks.Count == 0)
            return;
        double usable = height - 4;
        const double band = 2;
        for (double y = 0; y < usable; y += band)
        {
            int lo = (int)(y / usable * _count);
            int hi = Math.Max(lo + 1, (int)((y + band) / usable * _count));
            int k = LowerBound(marks, lo);
            if (k < marks.Count && marks[k] < hi && marks[k] < _count)
                dc.DrawRectangle(brush, null, new Rect(x, 2 + y, MinimapWidth - 8, band));
        }
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

    private sealed class Surface(LogViewControl owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => owner.Render(dc);
    }
}
