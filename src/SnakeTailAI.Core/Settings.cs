using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnakeTailAI.Core;

public enum ThemeChoice
{
    Dark,
    Light,
    System,
}

public sealed class OpenTabState
{
    public string Path { get; set; } = "";
    public bool IsSeries { get; set; }

    /// <summary>All the logs of a timeline tab (merged view of several devices); null for a single log.</summary>
    public List<string>? Paths { get; set; }

    public FilterSpec? Filter { get; set; }
}

public sealed class FilterPreset
{
    public string Name { get; set; } = "";
    public FilterSpec Filter { get; set; } = new();
}

public sealed class WindowPlacement
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 860;
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.Dark;
    public string LogRoot { get; set; } = @"C:\SPECIAL_DEVICES\ViuLog";
    public int InitialLoadMB { get; set; } = 20;
    public int MaxLines { get; set; } = 2_000_000;
    public int PollIntervalMs { get; set; } = 250;
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas";
    public double FontSize { get; set; } = 12.5;
    public bool FadeInNewLines { get; set; } = true;

    /// <summary>Line height as a multiple of the font size: 1.3 compact, 1.55 normal, 1.8 comfortable.</summary>
    public double RowSpacing { get; set; } = 1.55;

    public bool ShowFilterBar { get; set; } = true;

    /// <summary>Show the SSH buttons (terminal on the device of a log).</summary>
    public bool SshEnabled { get; set; } = true;
    public string SshUser { get; set; } = "root";
    public int SshPort { get; set; } = 22;

    /// <summary>The SSH password, encrypted for the Windows user (DPAPI, base64). Empty: typed in the terminal.</summary>
    public string SshPasswordProtected { get; set; } = "";

    /// <summary>Height of the terminal panel at the bottom of the window.</summary>
    public double TerminalHeight { get; set; } = 280;
    public bool ColumnsMode { get; set; }

    /// <summary>Widths (characters) of the Time, Level, Thread and Source columns.</summary>
    public string ColumnWidths { get; set; } = "15,5,9,34";
    public bool WordWrap { get; set; }
    public bool ShowBrowser { get; set; } = true;
    public double BrowserWidth { get; set; } = 290;
    public bool AlwaysOnTop { get; set; }
    public bool RestoreSession { get; set; } = true;
    public WindowPlacement Window { get; set; } = new();
    public List<HighlightRule> HighlightRules { get; set; } = HighlightRule.Defaults();
    public List<AlertRule> AlertRules { get; set; } = AlertRule.Defaults();
    public bool AlertsMuted { get; set; }
    public List<FilterPreset> FilterPresets { get; set; } = [];
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>When each device folder was last used (a log in it opened or viewed); orders the Devices panel.</summary>
    public Dictionary<string, DateTime> DeviceLastUsed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<OpenTabState> OpenTabs { get; set; } = [];
    public int SelectedTab { get; set; }

    public void MarkDeviceUsed(string folder) => DeviceLastUsed[folder] = DateTime.Now;

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 15)
            RecentFiles.RemoveRange(15, RecentFiles.Count - 15);
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>%AppData%\SnakeTailAI\settings.json, or the file named by the SNAKETAILAI_SETTINGS environment variable.</summary>
    public static string DefaultPath { get; } =
        Environment.GetEnvironmentVariable("SNAKETAILAI_SETTINGS") is { Length: > 0 } custom
            ? custom
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnakeTailAI", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
                // JSON gives a case-sensitive dictionary; folder paths on Windows are not.
                settings.DeviceLastUsed = new(settings.DeviceLastUsed ?? [], StringComparer.OrdinalIgnoreCase);
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must not stop the app; start from defaults.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
