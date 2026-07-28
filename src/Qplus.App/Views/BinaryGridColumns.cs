using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Qplus.Core.Data;

namespace Qplus.App.Views;

/// <summary>
/// Fixes up binary columns in an auto-generating <see cref="DataGrid"/>. WPF binds a byte[]
/// straight through its ToString(), which renders every BLOB / varbinary / RAW / IMAGE cell as
/// the literal "System.Byte[]". Attaching <see cref="Fix"/> to the grid's AutoGeneratingColumn
/// event replaces those columns with a read-only text column showing a size summary and a
/// hex-preview tooltip. Subscribe BEFORE assigning ItemsSource so the handler sees the columns
/// as they are generated.
/// </summary>
internal static class BinaryGridColumns
{
    public static void Fix(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyType != typeof(byte[])) return;
        if (e.Column is not DataGridTextColumn col || col.Binding is not Binding bind) return;

        // Show a readable summary instead of "System.Byte[]", and never let a blob be edited as
        // text — round-tripping raw bytes through a string would silently corrupt them.
        bind.Converter = BlobDisplayConverter.Instance;
        col.IsReadOnly = true;

        // Reuse the generated binding's path (it already quotes column names with spaces or
        // dots correctly) for the tooltip, and base the cell style on the theme's implicit
        // TextBlock style so the themed foreground survives in dark mode.
        var path = bind.Path?.Path ?? e.PropertyName;
        var baseStyle = Application.Current?.TryFindResource(typeof(TextBlock)) as Style;
        var style = new Style(typeof(TextBlock), baseStyle);
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
            new Binding(path) { Converter = BlobHexConverter.Instance }));
        col.ElementStyle = style;
    }
}

/// <summary>Renders a binary cell value as a size summary, everything else as its text.</summary>
internal sealed class BlobDisplayConverter : IValueConverter
{
    public static readonly BlobDisplayConverter Instance = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c) => CellFormat.Display(value);
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Renders a binary cell value as a hex preview for tooltips; empty for non-binary.</summary>
internal sealed class BlobHexConverter : IValueConverter
{
    public static readonly BlobHexConverter Instance = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c) => CellFormat.HexPreview(value) ?? "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
