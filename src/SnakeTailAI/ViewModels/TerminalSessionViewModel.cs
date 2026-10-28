using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SnakeTailAI.ViewModels;

/// <summary>An SSH session to one device, shown as a tab of the terminal panel.</summary>
public sealed partial class TerminalSessionViewModel(string host, string commandLine) : ObservableObject
{
    public string Host { get; } = host;
    public string CommandLine { get; } = commandLine;
    public string Title => Host;

    [ObservableProperty] private bool _isActive;

    /// <summary>"loading" while connecting, "live" while ssh runs, "idle" once it ended (same keys as the log status dot).</summary>
    [ObservableProperty] private string _statusKind = "loading";

    [ObservableProperty] private bool _hasEnded;

    /// <summary>Bumped to ask the view to start ssh again.</summary>
    [ObservableProperty] private int _connectVersion;

    /// <summary>Raised when the session is closed, so the view can end ssh.</summary>
    public event EventHandler? Closed;

    [RelayCommand]
    private void Reconnect()
    {
        HasEnded = false;
        StatusKind = "loading";
        ConnectVersion++;
    }

    public void Close() => Closed?.Invoke(this, EventArgs.Empty);
}
