using System.Windows;

namespace Qplus.App;

public enum AppTheme { Dark, Light }

/// <summary>
/// Swaps the active palette dictionary at runtime. The palette lives at index 0 of the
/// application's merged dictionaries; control styles reference its keys via DynamicResource,
/// so replacing it re-themes the whole UI instantly.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    private static Uri Uri(AppTheme t) =>
        new($"Themes/{(t == AppTheme.Dark ? "Dark" : "Light")}.xaml", UriKind.Relative);

    public static void Apply(AppTheme theme)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary { Source = Uri(theme) };
        if (dicts.Count == 0) dicts.Add(replacement);
        else dicts[0] = replacement; // index 0 is the palette (see App.xaml merge order)
        Current = theme;
    }

    public static AppTheme Parse(string? s) =>
        string.Equals(s, "Light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;
}
