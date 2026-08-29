using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Qplus.App.Views;

/// <summary>
/// Numbers the rows of a result <see cref="DataGrid"/> down its row header, the way SSMS and
/// SQL Developer do. Numbers follow the grid's display order, so re-sorting a column renumbers
/// from the top instead of dragging the original ordinals around with the rows.
/// </summary>
internal static class GridRowNumbers
{
    /// <summary>Turns on numbered row headers for a grid holding <paramref name="rowCount"/> rows.</summary>
    public static void Enable(DataGrid grid, int rowCount)
    {
        // The header style is monospaced, so left-padding the number right-aligns the column
        // no matter what the row-header template does with content alignment. Sizing the header
        // from the digit count up front avoids the width jumping about as the user scrolls.
        var digits = Math.Max(1, rowCount.ToString().Length);
        grid.RowHeaderWidth = 14 + 8 * digits;

        grid.LoadingRow += (_, e) => e.Row.Header = Label(e.Row.GetIndex(), digits);

        // Sorting reorders rows beneath containers that are already realised, so LoadingRow does
        // not fire for them; renumber whatever is on screen once the new order has settled.
        ((INotifyCollectionChanged)grid.Items).CollectionChanged += (_, _) =>
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => Renumber(grid, digits)));
    }

    private static string Label(int index, int digits) => (index + 1).ToString().PadLeft(digits);

    /// <summary>Renumbers the realised rows only — virtualised ones get theirs from LoadingRow.</summary>
    private static void Renumber(DependencyObject parent, int digits)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is DataGridRow row) row.Header = Label(row.GetIndex(), digits);
            else Renumber(child, digits);
        }
    }
}
