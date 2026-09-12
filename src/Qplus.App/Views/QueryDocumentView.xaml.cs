using System.Data;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Qplus.App.Completion;
using Qplus.Core.Data;
using Qplus.Core.Models;

namespace Qplus.App.Views;

/// <summary>A single query tab: editor + connection + results + export, self-contained.</summary>
public partial class QueryDocumentView : UserControl
{
    private IShell? _shell;
    private CancellationTokenSource? _cts;
    private SqlCompletionController? _completion;

    public QueryDocumentView()
    {
        InitializeComponent();
        EditorMenu.Attach(Editor, Run);   // right-click: execute / cut / copy / paste / select all
        ShowMessages(new[] { "Ready. Choose a connection, write SQL, press F5." });
        ConnectionCombo.SelectionChanged += (_, _) => PrewarmSchema();
    }

    /// <summary>Raised when the document's title (tab caption) should change.</summary>
    public event EventHandler? TitleChanged;

    /// <summary>Raised after any execution completes, so the shell can update the status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    private string _title = "Query";
    public string Title
    {
        get => _title;
        set { _title = value; TitleChanged?.Invoke(this, EventArgs.Empty); }
    }

    public string EditorText
    {
        get => Editor.Text;
        set => Editor.Text = value;
    }

    public ConnectionInfo? Connection
    {
        get => ConnectionCombo.SelectedItem as ConnectionInfo;
        set => ConnectionCombo.SelectedItem = value;
    }

    public void Attach(IShell shell)
    {
        _shell = shell;
        RefreshConnections(Connection?.Id);

        // IntelliSense-style table/column completion in this tab's editor.
        _completion ??= new SqlCompletionController(Editor, () => Connection, shell.Schema);
        PrewarmSchema();
    }

    /// <summary>Loads this connection's table/view list in the background so completion is instant.</summary>
    private void PrewarmSchema()
    {
        if (_shell is not null && Connection is { } conn)
            _shell.Schema.Prewarm(conn);
    }

    /// <summary>Re-applies the (recoloured) SQL highlighting so a theme change takes effect.</summary>
    public void RefreshSyntax() => Editor.SyntaxHighlighting = SqlSyntax.Definition;

    public void RefreshConnections(string? selectId)
    {
        if (_shell is null) return;
        var list = _shell.Connections;
        ConnectionCombo.ItemsSource = list;
        ConnectionCombo.DisplayMemberPath = nameof(ConnectionInfo.Name);
        ConnectionCombo.SelectedItem =
            list.FirstOrDefault(c => c.Id == selectId) ?? list.FirstOrDefault();
    }

    // ---- Execution -------------------------------------------------------

    private void Execute_Click(object sender, RoutedEventArgs e) => Run();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
    private void ClearMessages_Click(object sender, RoutedEventArgs e) => ClearMessages();

    /// <summary>Empties the output pane so the next run's grids and messages are unmistakably new.</summary>
    public void ClearMessages()
    {
        ShowMessages(Array.Empty<string>());
        StatsText.Text = string.Empty;
        StatusChanged?.Invoke(this, "Messages cleared.");
    }

    public void Cancel()
    {
        _cts?.Cancel();
        StatusChanged?.Invoke(this, "Cancelling…");
    }

    /// <summary>
    /// Runs the highlighted SQL, or the whole tab when nothing is highlighted — in which case
    /// the text is selected first, so the highlight always shows exactly what was sent.
    /// </summary>
    public void Run()
    {
        if (Editor.SelectionLength == 0)
        {
            Editor.SelectAll();
            Editor.Focus();
        }

        _ = ExecuteAsync(Editor.SelectedText);
    }

    public async Task ExecuteAsync(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) { StatusChanged?.Invoke(this, "Nothing to run."); return; }
        if (Connection is not { } conn) { StatusChanged?.Invoke(this, "Select a connection first."); return; }
        if (_shell is null) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        StatsText.Text = "Executing…";
        StatusChanged?.Invoke(this, "Executing…");

        var result = await QueryRunner.ExecuteAsync(conn, sql, _cts.Token);
        _shell.Store.TouchConnection(conn.Id);

        RenderResult(result);

        var rows = result.Grids.Sum(g => g.Rows.Count);
        var summary = $"{result.Grids.Count} grid(s), {rows} row(s), {result.TotalRowsAffected} affected · {result.Elapsed.TotalMilliseconds:0} ms";
        StatsText.Text = summary;
        StatusChanged?.Invoke(this, result.HasError ? "Query failed" : "Query completed");
    }

    private void RenderResult(QueryExecutionResult result)
    {
        ResultTabs.Items.Clear();

        if (ResultsAsTextBox.IsChecked == true)
        {
            ResultTabs.Items.Add(new TabItem { Header = "Results (text)", Content = MakeTextBox(FormatResultsAsText(result)) });
        }
        else
        {
            for (var i = 0; i < result.Grids.Count; i++)
            {
                var table = result.Grids[i];
                var label = result.Grids.Count > 1 ? $"{Title}_Result{i + 1}" : Title;
                var grid = new DataGrid
                {
                    AutoGenerateColumns = false,
                    IsReadOnly = true,
                    CanUserAddRows = false,
                    EnableRowVirtualization = true,
                    // Cell-level selection, so Copy can take a single value; clicking the row
                    // number still takes the whole row, as it does in SSMS.
                    SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
                    Tag = label, // suggested file name, for either route to CSV
                };
                // Build the columns rather than letting WPF auto-generate them: it binds by
                // column name, which blanks out (or throws on) names a query can legitimately
                // produce — "(no column name)" from SELECT COUNT(*) among them.
                ResultGridColumns.Build(grid, table);
                // Right-click: execute / select all / copy / save. Execute re-runs the query
                // exactly as F5 does. The name is resolved on use so a renamed tab seeds the
                // save dialog with its current name.
                ResultGridMenu.Attach(grid, () => label, msg => StatusChanged?.Invoke(this, msg), Run);
                BinaryGridColumns.EnableViewer(grid);   // double-click a blob to inspect it
                GridRowNumbers.Enable(grid, table.Rows.Count);
                grid.ItemsSource = table.DefaultView;
                ResultTabs.Items.Add(new TabItem { Header = $"Result {i + 1}", Content = grid });
            }
        }

        ResultTabs.Items.Add(new TabItem { Header = "Messages", Content = MakeTextBox(string.Join("\n", result.Messages)) });

        // A failure belongs in front of the user, not behind a grid tab.
        ResultTabs.SelectedIndex = result.HasError ? ResultTabs.Items.Count - 1 : 0;
    }

    // ---- CSV export ------------------------------------------------------

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGrid() is not { } grid)
        {
            MessageBox.Show(Window.GetWindow(this), "Select a result grid to export first.",
                "Export CSV", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Same route as the grid's own "Save Results As…", so both save what is on screen.
        ResultGridMenu.Save(grid, grid.Tag as string ?? Title, msg => StatusChanged?.Invoke(this, msg));
    }

    private DataGrid? SelectedGrid()
    {
        // Prefer the currently selected result tab if it holds a grid; else the first grid.
        if (ResultTabs.SelectedItem is TabItem { Content: DataGrid selected }) return selected;
        foreach (var item in ResultTabs.Items)
            if (item is TabItem { Content: DataGrid first }) return first;
        return null;
    }

    // ---- Helpers ---------------------------------------------------------

    private void ShowMessages(IEnumerable<string> lines)
    {
        ResultTabs.Items.Clear();
        ResultTabs.Items.Add(new TabItem { Header = "Messages", Content = MakeTextBox(string.Join("\n", lines)) });
    }

    private static TextBox MakeTextBox(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap,
    };

    private static string FormatResultsAsText(QueryExecutionResult result)
    {
        var sb = new StringBuilder();
        foreach (var table in result.Grids)
        {
            var widths = new int[table.Columns.Count];
            for (var c = 0; c < table.Columns.Count; c++)
            {
                widths[c] = table.Columns[c].ColumnName.Length;
                foreach (DataRow row in table.Rows)
                    widths[c] = Math.Max(widths[c], CellText(row[c]).Length);
                widths[c] = Math.Min(widths[c], 60);
            }

            sb.AppendLine(string.Join(" | ", table.Columns.Cast<DataColumn>()
                .Select((col, i) => col.ColumnName.PadRight(widths[i]))));
            sb.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));
            foreach (DataRow row in table.Rows)
            {
                sb.AppendLine(string.Join(" | ", Enumerable.Range(0, table.Columns.Count)
                    .Select(i => Trunc(CellText(row[i]), widths[i]).PadRight(widths[i]))));
            }
            sb.AppendLine();
        }
        foreach (var m in result.Messages) sb.AppendLine(m);
        return sb.ToString();
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // NULL stays distinct from an empty value in the text view; everything else, binary
    // included, goes through the shared formatter so "System.Byte[]" never appears.
    private static string CellText(object? value) =>
        value is null or DBNull ? "NULL" : CellFormat.Display(value);
}
