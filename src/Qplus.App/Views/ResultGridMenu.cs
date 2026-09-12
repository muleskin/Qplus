using System.Data;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Qplus.App.ViewModels;
using Qplus.Core.Data;

namespace Qplus.App.Views;

/// <summary>
/// The SSMS-style right-click menu on a result grid: Execute (when the grid has a query to
/// re-run), Select All, Copy, Copy with Headers and Save Results As. Each column contributes the
/// same text it displays, so a blob copies as its size summary rather than "System.Byte[]".
/// </summary>
internal static class ResultGridMenu
{
    /// <summary>
    /// Gives a grid the context menu and its keyboard equivalents. Call once per grid.
    /// <paramref name="suggestedName"/> seeds the save dialog's file name, and
    /// <paramref name="status"/> reports where the file went. Passing <paramref name="execute"/>
    /// adds an Execute item that re-runs whatever query fills the grid, labelled with
    /// <paramref name="executeGesture"/> — pass "" where no key does the same thing.
    /// </summary>
    public static void Attach(DataGrid grid, Func<string> suggestedName, Action<string>? status = null,
        Action? execute = null, string executeGesture = "F5")
    {
        var selectAll = NewItem("Select _All", "Ctrl+A", () => grid.SelectAll());
        var copy = NewItem("_Copy", "Ctrl+C", () => Copy(grid, includeHeaders: false));
        var copyHeaders = NewItem("Copy with _Headers", "Ctrl+Shift+C",
            () => Copy(grid, includeHeaders: true));
        var save = NewItem("_Save Results As…", "", () => Save(grid, suggestedName(), status));

        var menu = new ContextMenu();
        if (execute is not null)
        {
            // Keys are bound elsewhere (F5 at the window), so the gesture here is a label only.
            menu.Items.Add(NewItem("E_xecute", executeGesture, execute));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(selectAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        menu.Items.Add(copyHeaders);
        menu.Items.Add(new Separator());
        menu.Items.Add(save);

        // Nothing selected means nothing to copy, so grey the copies out rather than no-op.
        menu.Opened += (_, _) =>
        {
            var hasSelection = grid.SelectedCells.Count > 0;
            copy.IsEnabled = hasSelection;
            copyHeaders.IsEnabled = hasSelection;
            selectAll.IsEnabled = grid.Items.Count > 0;
            save.IsEnabled = grid.ItemsSource is DataView;
        };

        grid.ContextMenu = menu;

        // Ctrl+A and Ctrl+C are already built into DataGrid; only the headers variant needs one.
        grid.InputBindings.Add(new KeyBinding(
            new RelayCommand(_ => Copy(grid, includeHeaders: true)),
            Key.C, ModifierKeys.Control | ModifierKeys.Shift));
    }

    /// <summary>
    /// Copies the selected cells as tab-separated text, optionally led by a header row.
    /// <para>
    /// The text is built here rather than by flipping ClipboardCopyMode and firing the routed
    /// Copy command: that route decides whether to include headers inside WPF's own handler, so
    /// anything that intercepts or defers the command produces a copy with the default
    /// (header-less) mode — the copy still happens, silently without the headers.
    /// </para>
    /// </summary>
    private static void Copy(DataGrid grid, bool includeHeaders)
    {
        var selected = grid.SelectedCells.Count;
        if (selected == 0) return;

        // Select All on a large result selects rows x columns cells. Walking that collection and
        // resolving every cell through its binding costs seconds once the result runs to tens of
        // thousands of rows, so when everything is selected read the rows straight off the view.
        var text = selected == grid.Items.Count * grid.Columns.Count
            ? CopyEverything(grid, includeHeaders)
            : CopySelection(grid, includeHeaders);

        if (text is not null) Clipboard.SetText(text);
    }

    /// <summary>Every row, read by ordinal — no per-cell selection or binding lookups.</summary>
    private static string? CopyEverything(DataGrid grid, bool includeHeaders)
    {
        var columns = grid.Columns.OrderBy(c => c.DisplayIndex).ToList();
        var table = (grid.ItemsSource as DataView)?.Table;
        var ordinals = columns
            .Select(c => table?.Columns[c.SortMemberPath ?? ""]?.Ordinal ?? -1)
            .ToArray();

        // Anything unexpected about the source and the general path still handles it correctly.
        if (table is null || ordinals.Any(o => o < 0)) return CopySelection(grid, includeHeaders);

        var text = new StringBuilder(grid.Items.Count * columns.Count * 8);
        if (includeHeaders) AppendRow(text, columns.Select(c => Cell(c.Header)));

        foreach (var item in grid.Items)
        {
            if (item is not DataRowView row) continue;   // e.g. the new-row placeholder
            AppendRow(text, ordinals.Select(o => Cell(CellFormat.Display(row[o]))));
        }
        return text.ToString();
    }

    /// <summary>A partial selection — small by nature, so per-cell resolution is fine here.</summary>
    private static string? CopySelection(DataGrid grid, bool includeHeaders)
    {
        // Cells come back in selection order; put them back into the order shown on screen.
        var columns = grid.SelectedCells.Select(c => c.Column).Distinct()
            .OrderBy(c => c.DisplayIndex).ToList();
        var wanted = grid.SelectedCells.Select(c => c.Item).ToHashSet();
        var rows = grid.Items.Cast<object>().Where(wanted.Contains).ToList();
        if (columns.Count == 0 || rows.Count == 0) return null;

        var text = new StringBuilder();
        if (includeHeaders) AppendRow(text, columns.Select(c => Cell(c.Header)));
        foreach (var row in rows)
            AppendRow(text, columns.Select(c => Cell(c.OnCopyingCellClipboardContent(row))));
        return text.ToString();
    }

    private static void AppendRow(StringBuilder text, IEnumerable<string> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) text.Append('\t');
            text.Append(cell);
            first = false;
        }
        text.Append('\n');
    }

    /// <summary>One clipboard cell, quoted the way a spreadsheet expects when it has to be.</summary>
    private static string Cell(object? value)
    {
        var text = value?.ToString() ?? "";
        return text.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0 || text.Contains('"')
            ? '"' + text.Replace("\"", "\"\"") + '"'
            : text;
    }

    /// <summary>
    /// Writes the grid's rows to CSV. The view is saved rather than the underlying table, so the
    /// file comes out in the order shown on screen. Shared with the toolbar's export button so
    /// both routes save the same thing.
    /// </summary>
    public static void Save(DataGrid grid, string suggestedName, Action<string>? status = null)
    {
        if (grid.ItemsSource is not DataView view) { status?.Invoke("Nothing to save."); return; }

        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = SafeFileName(suggestedName) + ".csv",
        };
        if (dlg.ShowDialog() != true) return;

        var table = view.ToTable();
        CsvExporter.Write(table, dlg.FileName);
        status?.Invoke($"Saved {table.Rows.Count} row(s) to {dlg.FileName}");
    }

    /// <summary>A table or tab name can hold characters a file name cannot.</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "results" : cleaned;
    }

    private static MenuItem NewItem(string header, string gesture, Action onClick)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (_, _) => onClick();
        return item;
    }
}
