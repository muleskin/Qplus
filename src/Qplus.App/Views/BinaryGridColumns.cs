using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Qplus.App.Views;

/// <summary>
/// The blob inspector for grids whose columns come from <see cref="ResultGridColumns"/>. Those
/// columns already render binary values as a size summary with a hex-preview tooltip; this adds
/// the double-click that opens the full viewer.
/// </summary>
internal static class BinaryGridColumns
{
    /// <summary>
    /// Opens the full hex / image viewer when a binary cell is double-clicked. The trigger is the
    /// cell's value being a non-empty byte[], so ordinary cells keep their normal double-click
    /// behaviour and only blob cells are intercepted. Attach once per grid.
    /// </summary>
    public static void EnableViewer(DataGrid grid)
    {
        grid.MouseDoubleClick += (_, e) =>
        {
            if (grid.CurrentCell is not { Column: { } col, Item: DataRowView drv }) return;

            var name = ColumnName(col);
            if (name is null) return;

            object value;
            try { value = drv.Row[name]; }
            catch { return; }                            // e.g. a just-added row not yet committed

            if (value is byte[] { Length: > 0 } bytes)
            {
                new BlobViewerWindow(bytes, name) { Owner = Window.GetWindow(grid) }.ShowDialog();
                e.Handled = true;                        // don't also enter cell edit
            }
        };
    }

    /// <summary>
    /// The underlying DataTable column name for a grid column. <see cref="ResultGridColumns"/>
    /// sets SortMemberPath to it; if that is empty we fall back to the binding path, stripping the
    /// [brackets] an indexer path uses so the DataRow indexer accepts it.
    /// </summary>
    private static string? ColumnName(DataGridColumn col)
    {
        if (!string.IsNullOrEmpty(col.SortMemberPath)) return col.SortMemberPath;
        if (col is DataGridBoundColumn { Binding: Binding { Path.Path: { Length: > 0 } p } })
            return p.StartsWith('[') && p.EndsWith(']') ? p[1..^1] : p;
        return null;
    }
}
