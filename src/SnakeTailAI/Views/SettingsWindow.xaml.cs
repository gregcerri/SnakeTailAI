using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SnakeTailAI.Core;
using Wpf.Ui.Controls;

namespace SnakeTailAI.Views;

public partial class SettingsWindow : FluentWindow
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        ThemeBox.SelectedIndex = (int)settings.Theme;
        FontBox.Text = settings.FontFamily;
        FontSizeBox.Value = settings.FontSize;
        FadeSwitch.IsChecked = settings.FadeInNewLines;
        SpacingBox.SelectedIndex = settings.RowSpacing < 1.42 ? 0 : settings.RowSpacing > 1.68 ? 2 : 1;
        RootBox.Text = settings.LogRoot;
        InitialBox.Value = settings.InitialLoadMB;
        MaxLinesBox.Value = settings.MaxLines;
        PollBox.Value = settings.PollIntervalMs;
        RestoreSwitch.IsChecked = settings.RestoreSession;
        SettingsPathRun.Text = SettingsStore.DefaultPath;
        SshSwitch.IsChecked = settings.SshEnabled;
        SshUserBox.Text = settings.SshUser;
        SshPortBox.Value = settings.SshPort;
        _sshPassword = settings.SshPasswordProtected;
        UpdatePasswordStatus();
    }

    /// <summary>The saved (encrypted) SSH password; only replaced when a new one is typed.</summary>
    private string _sshPassword;

    private void UpdatePasswordStatus()
    {
        bool saved = Services.SshLauncher.Unprotect(_sshPassword).Length > 0;
        SshPasswordStatus.Text = saved
            ? "Saved, encrypted for your Windows user. Type a new one to change it."
            : "Not saved: you will type it in the terminal.";
        SshRemoveButton.IsEnabled = saved;
    }

    private void OnRemoveSshPassword(object sender, RoutedEventArgs e)
    {
        _sshPassword = "";
        SshPasswordBox.Clear();
        UpdatePasswordStatus();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Devices folder", InitialDirectory = RootBox.Text };
        if (dialog.ShowDialog(this) == true)
            RootBox.Text = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.Theme = (ThemeChoice)Math.Max(0, ThemeBox.SelectedIndex);
        _settings.FontFamily = string.IsNullOrWhiteSpace(FontBox.Text) ? "Cascadia Mono, Consolas" : FontBox.Text.Trim();
        _settings.FontSize = Math.Clamp(FontSizeBox.Value ?? 12.5, 8, 28);
        _settings.FadeInNewLines = FadeSwitch.IsChecked == true;
        _settings.RowSpacing = SpacingBox.SelectedIndex switch { 0 => 1.3, 2 => 1.8, _ => 1.55 };
        _settings.LogRoot = RootBox.Text.Trim();
        _settings.InitialLoadMB = (int)Math.Clamp(InitialBox.Value ?? 20, 0, 4096);
        _settings.MaxLines = (int)Math.Clamp(MaxLinesBox.Value ?? 2_000_000, 10_000, 20_000_000);
        _settings.PollIntervalMs = (int)Math.Clamp(PollBox.Value ?? 250, 50, 5000);
        _settings.RestoreSession = RestoreSwitch.IsChecked == true;
        _settings.SshEnabled = SshSwitch.IsChecked == true;
        _settings.SshUser = SshUserBox.Text.Trim();
        _settings.SshPort = (int)Math.Clamp(SshPortBox.Value ?? 22, 1, 65535);
        _settings.SshPasswordProtected = SshPasswordBox.Password.Length > 0
            ? Services.SshLauncher.Protect(SshPasswordBox.Password)
            : _sshPassword;
        DialogResult = true;
    }
}
