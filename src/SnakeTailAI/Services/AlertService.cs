using System.Media;
using System.Windows;
using SnakeTailAI.Core;
using SnakeTailAI.ViewModels;
using Forms = System.Windows.Forms;

namespace SnakeTailAI.Services;

/// <summary>
/// Checks new live lines against the alert rules and tells the user: a Windows notification (through a tray icon)
/// and/or a sound. Each rule fires at most once every <see cref="QuietPeriod"/> per tab; the lines in between are counted.
/// </summary>
public sealed class AlertService : IDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(30);

    private readonly Forms.NotifyIcon _tray;
    private readonly Dictionary<(AlertRule Rule, LogTabViewModel Tab), (DateTime Last, int Suppressed)> _recent = [];
    private AlertMatcher _matcher = AlertMatcher.Empty;
    private (LogTabViewModel Tab, LogLine Line)? _lastAlert;

    public AlertService()
    {
        _tray = new Forms.NotifyIcon
        {
            Text = "SnakeTail AI",
            Icon = LoadIcon(),
            Visible = true,
        };
        _tray.BalloonTipClicked += (_, _) => OpenLastAlert();
        _tray.DoubleClick += (_, _) => ShowWindow();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show SnakeTail AI", null, (_, _) => ShowWindow());
        _muteItem = new Forms.ToolStripMenuItem("Mute alerts", null, (_, _) => Muted = !Muted);
        menu.Items.Add(_muteItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Current.MainWindow?.Close());
        _tray.ContextMenuStrip = menu;
    }

    private readonly Forms.ToolStripMenuItem _muteItem;

    /// <summary>The user clicked a notification: show this line.</summary>
    public event Action<LogTabViewModel, LogLine>? AlertOpened;

    public event Action<bool>? MutedChanged;

    private bool _muted;

    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value)
                return;
            _muted = value;
            _muteItem.Checked = value;
            MutedChanged?.Invoke(value);
        }
    }

    public void SetRules(IEnumerable<AlertRule> rules)
    {
        _matcher = new AlertMatcher(rules);
        _recent.Clear();
    }

    public void Process(LogTabViewModel tab, IReadOnlyList<LogLine> lines)
    {
        if (_muted || _matcher.IsEmpty)
            return;
        foreach (var line in lines)
        {
            var rule = _matcher.Match(line);
            if (rule == null)
                continue;

            var key = (rule, tab);
            var now = DateTime.Now;
            if (_recent.TryGetValue(key, out var recent) && now - recent.Last < QuietPeriod)
            {
                _recent[key] = (recent.Last, recent.Suppressed + 1);
                continue;
            }
            _recent[key] = (now, 0);
            Fire(tab, line, rule, recent.Suppressed);
        }
    }

    private void Fire(LogTabViewModel tab, LogLine line, AlertRule rule, int suppressed)
    {
        _lastAlert = (tab, line);
        string where = line.Origin ?? tab.FolderName;
        string message = line.MessageText.Trim();
        if (message.Length == 0)
            message = line.Raw;
        if (message.Length > 180)
            message = message[..180] + "…";
        if (suppressed > 0)
            message += $"\n(+{suppressed} more in the last {QuietPeriod.TotalSeconds:0} s)";

        if (rule.ShowNotification)
        {
            var icon = line.Level >= LogLevel.Error ? Forms.ToolTipIcon.Error : line.Level == LogLevel.Warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
            _tray.ShowBalloonTip(8000, $"{where} · {rule.Describe()}", message, icon);
        }
        if (rule.PlaySound)
            (line.Level >= LogLevel.Error ? SystemSounds.Hand : SystemSounds.Exclamation).Play();
    }

    /// <summary>Shows a sample notification, so the user sees what an alert looks like.</summary>
    public void Test() =>
        _tray.ShowBalloonTip(5000, "SnakeTail AI · test", "Alerts work: matching lines will appear like this.", Forms.ToolTipIcon.Info);

    private void OpenLastAlert()
    {
        ShowWindow();
        if (_lastAlert is { } alert)
            AlertOpened?.Invoke(alert.Tab, alert.Line);
    }

    private static void ShowWindow()
    {
        var window = Application.Current.MainWindow;
        if (window == null)
            return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        return stream != null ? new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
    }
}
