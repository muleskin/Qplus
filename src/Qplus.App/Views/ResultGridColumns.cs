using System.Data;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Qplus.Core.Data;

namespace Qplus.App.Views;

/// <summary>
/// Builds the columns of a grid backed by a <see cref="DataTable"/> — query results, the table
/// detail panes and the editable data pane.
/// <para>
/// WPF's AutoGenerateColumns binds each column by name, and a name it cannot parse as a binding
/// path breaks the grid: <c>SELECT COUNT(*)</c> returns an unnamed column that lands in the
/// "(no column name)" placeholder, whose leading "(" WPF reads as attached-property syntax, so
/// every cell renders blank. A "." in a name binds to a sub-property that does not exist (blank
/// again), and a "]" throws out of column generation before any handler can intervene.
/// </para>
/// <para>
/// Columns are built here instead. A name that survives the parser as an indexer keeps a
/// read/write binding, so the data pane stays editable; anything else falls back to reading the
/// value by ordinal, which keeps the parser out of it entirely at the cost of being read-only.
/// </para>
/// </summary>
internal static class ResultGridColumns
{
    /// <summary>Fills <paramref name="grid"/> with one bound column per column of the table.</summary>
    public static void Build(DataGrid grid, DataTable table)
    {
        grid.AutoGenerateColumns = false;
        grid.Columns.Clear();
        foreach (DataColumn column in table.Columns)
            grid.Columns.Add(NewColumn(column));
    }

    /// <summary>Drops every column, for a grid whose source is being cleared.</summary>
    public static void Clear(DataGrid grid)
    {
        grid.AutoGenerateColumns = false;
        grid.Columns.Clear();
    }

    private static DataGridColumn NewColumn(DataColumn column)
    {
        var col = BoundColumn(column);

        // A header is content rather than a path, so the real name is safe to show as-is.
        col.Header = column.ColumnName;
        col.SortMemberPath = column.ColumnName;   // also how the blob viewer finds the column

        // Keep SortMemberPath set but take the click away for the few names whose sort would
        // throw rather than sort.
        col.CanUserSort = IsSortable(column.ColumnName);
        return col;
    }

    private static DataGridColumn BoundColumn(DataColumn column)
    {
        // Never let a blob be edited as text — round-tripping raw bytes through a string would
        // silently corrupt them — and always render it as a size summary rather than the literal
        // "System.Byte[]" its ToString() produces.
        if (column.DataType == typeof(byte[]))
            return new DataGridTextColumn
            {
                Binding = ByOrdinal(column, CellTextConverter.Instance),
                ElementStyle = BlobTooltipStyle(column),
                IsReadOnly = true,
            };

        if (EditablePath(column.ColumnName) is { } path)
        {
            var binding = new Binding(path)
            {
                Mode = BindingMode.TwoWay,
                ValidatesOnExceptions = true,
                ValidatesOnDataErrors = true,
            };
            return column.DataType == typeof(bool)
                ? new DataGridCheckBoxColumn { Binding = binding, IsThreeState = column.AllowDBNull }
                : new DataGridTextColumn { Binding = binding };
        }

        // Unbindable name: readable, but there is no path to write back through.
        return column.DataType == typeof(bool)
            ? new DataGridCheckBoxColumn
            {
                Binding = ByOrdinal(column, CellValueConverter.Instance),
                IsThreeState = column.AllowDBNull,
                IsReadOnly = true,
            }
            : new DataGridTextColumn
            {
                Binding = ByOrdinal(column, CellTextConverter.Instance),
                IsReadOnly = true,
            };
    }

    /// <summary>
    /// "." binds the DataRowView itself; the converter picks the value out by ordinal, so the
    /// column's name is never handed to WPF as a path.
    /// </summary>
    private static Binding ByOrdinal(DataColumn column, IValueConverter converter) => new(".")
    {
        Mode = BindingMode.OneWay,
        Converter = converter,
        ConverterParameter = column.Ordinal,
    };

    /// <summary>
    /// An indexer path that both reads and writes the column, or null when WPF's parser cannot
    /// express the name: a leading "(" is read as a type qualifier, edge whitespace is trimmed
    /// away, and an unbalanced bracket throws outright.
    /// </summary>
    private static string? EditablePath(string name)
    {
        if (name.Length == 0 || name[0] == '(' || name.Trim() != name) return null;

        var sb = new StringBuilder("[");
        foreach (var c in name)
        {
            if (c is '^' or '[' or ']' or ',') sb.Append('^');   // PropertyPath's escape character
            sb.Append(c);
        }
        var path = sb.Append(']').ToString();

        return CanParse(path) ? path : null;
    }

    /// <summary>
    /// Whether a header click can safely sort by this name. Sorting resolves the column by exact
    /// name, so most of the names that defeat the binding parser still sort correctly — but the
    /// sort description is built through a property path first, which throws on an unbalanced
    /// bracket or parenthesis, and a comma splits the view's sort string in two.
    /// </summary>
    private static bool IsSortable(string name) => !name.Contains(',') && CanParse(name);

    /// <summary>Whether WPF can turn this into a binding path at all.</summary>
    private static bool CanParse(string path)
    {
        try { _ = new Binding(path); return true; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Hex-preview tooltip for binary columns.</summary>
    private static Style BlobTooltipStyle(DataColumn column)
    {
        // Base it on the theme's implicit TextBlock style so the themed foreground survives.
        var baseStyle = Application.Current?.TryFindResource(typeof(TextBlock)) as Style;
        var style = new Style(typeof(TextBlock), baseStyle);
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(".")
        {
            Converter = BlobTooltipConverter.Instance,
            ConverterParameter = column.Ordinal,
        }));
        return style;
    }
}

/// <summary>Renders the ordinal-th cell of a row as display text.</summary>
internal sealed class CellTextConverter : IValueConverter
{
    public static readonly CellTextConverter Instance = new();
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
        => value is DataRowView row && parameter is int i ? CellFormat.Display(row[i]) : "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Hands back the ordinal-th cell's bool, for check-box columns.</summary>
internal sealed class CellValueConverter : IValueConverter
{
    public static readonly CellValueConverter Instance = new();
    public object? Convert(object? value, Type t, object? parameter, CultureInfo c)
        => value is DataRowView row && parameter is int i && row[i] is bool b ? b : null;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Hex preview of the ordinal-th cell, for binary-column tooltips.</summary>
internal sealed class BlobTooltipConverter : IValueConverter
{
    public static readonly BlobTooltipConverter Instance = new();
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
        => value is DataRowView row && parameter is int i ? CellFormat.HexPreview(row[i]) ?? "" : "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
