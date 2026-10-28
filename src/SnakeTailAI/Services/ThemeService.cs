using System.Windows;
using System.Windows.Media;
using SnakeTailAI.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SnakeTailAI.Services;

/// <summary>Applies the Fluent theme (WPF-UI) and keeps the log palette and level brushes in step with it.</summary>
public static class ThemeService
{
    private static Window? _watched;

    public static bool IsDark => LogPalette.Current.IsDark;

    public static Color Accent
    {
        get
        {
            try
            {
                return ApplicationAccentColorManager.PrimaryAccent;
            }
            catch (Exception)
            {
                return Color.FromRgb(0x4C, 0xC2, 0xFF);
            }
        }
    }

    public static void Initialize()
    {
        ApplicationThemeManager.Changed += (theme, _) => SyncPalette(theme == ApplicationTheme.Dark || theme == ApplicationTheme.HighContrast);
    }

    public static void Apply(ThemeChoice choice, Window? window)
    {
        if (_watched != null)
        {
            SystemThemeWatcher.UnWatch(_watched);
            _watched = null;
        }

        ApplicationTheme theme = choice switch
        {
            ThemeChoice.Light => ApplicationTheme.Light,
            ThemeChoice.System => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
            _ => ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);

        if (choice == ThemeChoice.System && window != null)
        {
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
            _watched = window;
        }
        SyncPalette(theme == ApplicationTheme.Dark);
    }

    private static void SyncPalette(bool dark)
    {
        LogPalette.SetCurrent(dark);
        var p = LogPalette.Current;
        var res = Application.Current.Resources;
        res["Log.Background"] = p.Background;
        res["Log.Debug"] = p.Debug;
        res["Log.Info"] = p.Info;
        res["Log.Warning"] = p.Warning;
        res["Log.Error"] = p.Error;
        res["Log.Other"] = p.TextDim;
        res["Log.Bookmark"] = p.Bookmark;
        res["Log.Live"] = LogPalette.Parse(dark ? "#4ADE80" : "#16A34A");
        res["Log.Accent"] = new SolidColorBrush(Accent);
    }
}
