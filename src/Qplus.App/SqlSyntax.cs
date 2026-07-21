using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Qplus.App;

/// <summary>
/// Recolours AvalonEdit's built-in "TSQL" highlighting for readability, per theme.
/// The definition is shared (cached in <see cref="HighlightingManager"/>), so mutating its
/// named colors re-themes every editor; call <c>RefreshSyntax</c> on open editors afterwards.
/// </summary>
public static class SqlSyntax
{
    public static void Apply(AppTheme theme)
    {
        var def = HighlightingManager.Instance.GetDefinition("TSQL");
        if (def is null) return;

        var dark = theme == AppTheme.Dark;
        var keyword = dark ? Color.FromRgb(0x4F, 0xD6, 0xE6)  // bright cyan
                           : Color.FromRgb(0x00, 0x55, 0xAA); // deep blue
        var comment = dark ? Color.FromRgb(0x6A, 0x99, 0x55) : Color.FromRgb(0x00, 0x80, 0x00);
        var str     = dark ? Color.FromRgb(0xCE, 0x91, 0x78) : Color.FromRgb(0xA3, 0x15, 0x15);
        var number  = dark ? Color.FromRgb(0xB5, 0xCE, 0xA8) : Color.FromRgb(0x09, 0x86, 0x58);

        foreach (var c in def.NamedHighlightingColors)
        {
            var n = c.Name?.ToLowerInvariant() ?? "";
            Color fg =
                n.Contains("comment") ? comment :
                (n.Contains("string") || n.Contains("char")) ? str :
                (n.Contains("digit") || n.Contains("number")) ? number :
                keyword; // keywords, functions, operators, punctuation
            c.Foreground = new SimpleHighlightingBrush(fg);
        }
    }

    /// <summary>Returns the shared TSQL definition (already recoloured by <see cref="Apply"/>).</summary>
    public static IHighlightingDefinition? Definition =>
        HighlightingManager.Instance.GetDefinition("TSQL");
}
