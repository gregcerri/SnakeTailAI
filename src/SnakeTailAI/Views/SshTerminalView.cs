using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;
using SnakeTailAI.Core;
using SnakeTailAI.Services;
using SnakeTailAI.ViewModels;

namespace SnakeTailAI.Views;

/// <summary>
/// Hosts the terminal of one SSH session: Windows Terminal's renderer running ssh.exe through a pseudo console.
/// Reconnecting replaces the terminal control, so every connection starts clean.
/// </summary>
public sealed class SshTerminalView : Border
{
    private readonly DispatcherTimer _watch;
    private EasyTerminalControl? _terminal;
    private TerminalSessionViewModel? _session;
    private bool _handingOff;
    private long _startTicks;

    public SshTerminalView()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22));
        Padding = new Thickness(6, 4, 2, 2);
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _watch.Tick += (_, _) => Watch();
        DataContextChanged += (_, e) => Attach(e.NewValue as TerminalSessionViewModel);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(() => _terminal?.Terminal?.Focus(), DispatcherPriority.Input);
        };
    }

    private static AppSettings Settings => ((MainViewModel)Application.Current.MainWindow.DataContext).Settings;

    private void Attach(TerminalSessionViewModel? session)
    {
        if (_session != null)
        {
            _session.PropertyChanged -= OnSessionChanged;
            _session.Closed -= OnSessionClosed;
        }
        _session = session;
        if (session == null)
            return;
        session.PropertyChanged += OnSessionChanged;
        session.Closed += OnSessionClosed;
        Connect();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalSessionViewModel.ConnectVersion))
            Connect();
    }

    private void OnSessionClosed(object? sender, EventArgs e)
    {
        Stop();
        _watch.Stop();
    }

    private void Connect()
    {
        if (_session == null)
            return;
        Stop();

        // The terminal starts ssh with this process's environment: hand the (encrypted) password over just for that.
        SshLauncher.BeginPasswordHandOff(Settings);
        _handingOff = true;
        _startTicks = Environment.TickCount64;

        _terminal = new EasyTerminalControl
        {
            StartupCommandLine = _session.CommandLine,
            Theme = Theme(),
            FontFamilyWhenSettingTheme = new FontFamily("Cascadia Mono, Consolas"),
            FontSizeWhenSettingTheme = 13,
            InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey | EasyTerminalControl.INPUT_CAPTURE.DirectionKeys,
        };
        Child = _terminal;
        _watch.Start();
    }

    /// <summary>Ends the password hand-off once ssh runs, and notices when the session ends.</summary>
    private void Watch()
    {
        if (_session == null || _terminal == null)
            return;
        var term = _terminal.ConPTYTerm;
        var process = term?.TermProcIsStarted == true ? term.Process : null;

        if (_handingOff && (process != null || Environment.TickCount64 - _startTicks > 15_000))
        {
            SshLauncher.EndPasswordHandOff();
            _handingOff = false;
        }
        if (term == null || process == null)
            return;

        if (process.HasExited)
        {
            if (!_session.HasEnded)
            {
                _session.HasEnded = true;
                _session.StatusKind = "idle";
                term.WriteToUITerminal("\r\n\x1b[90m── session ended · use Reconnect to connect again ──\x1b[0m\r\n");
            }
            _watch.Stop();
        }
        else if (_session.StatusKind != "live")
        {
            _session.StatusKind = "live";
        }
    }

    private void Stop()
    {
        if (_handingOff)
        {
            SshLauncher.EndPasswordHandOff();
            _handingOff = false;
        }
        if (_terminal == null)
            return;
        try
        {
            var process = _terminal.ConPTYTerm?.Process;
            if (process is { HasExited: false })
                process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        Child = null;
        _terminal = null;
    }

    public void FocusTerminal() => _terminal?.Terminal?.Focus();

    /// <summary>A dark theme close to the log view (COLORREF values are 0x00BBGGRR).</summary>
    private static TerminalTheme Theme()
    {
        static uint C(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        return new TerminalTheme
        {
            DefaultBackground = C(0x1E1F22),
            DefaultForeground = C(0xDCDFE4),
            DefaultSelectionBackground = C(0x264F78),
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable =
            [
                C(0x1E1F22), C(0xFF5C5C), C(0x4ADE80), C(0xFFB454), C(0x4FC1FF), C(0xC792EA), C(0x5EEAD4), C(0xDCDFE4),
                C(0x7D8590), C(0xFF8A8A), C(0x86EFAC), C(0xFFD08A), C(0x93C5FD), C(0xF0ABFC), C(0x99F6E4), C(0xFFFFFF),
            ],
        };
    }
}
