using System.Data;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Qplus.App.Completion;
using Qplus.Core.Data;
using Qplus.Core.Models;

namespace Qplus.App.Views;

/// <summary>A single query tab: editor + connection + results + export, self-contained.</summary>
public partial class QueryDocumentView : UserControl, ICommitTarget
{
    private IShell? _shell;
    private CancellationTokenSource? _cts;
    private SqlCompletionController? _completion;

    // Manual commit: while auto-commit is off the tab holds one connection with a transaction on
    // it, and that connection runs one command at a time.
    private QuerySession? _session;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly CommitActions _commits;
    private bool _suppressConnectionGuard;

    public QueryDocumentView()
    {
        InitializeComponent();
        _commits = CommitActions.For(this);
        // Right-click: execute / commit / rollback / cut / copy / paste / select all.
        EditorMenu.Attach(Editor, Run, _commits);
        ShowMessages(new[] { "Ready. Choose a connection, write SQL, press F5." });
        ConnectionCombo.SelectionChanged += ConnectionCombo_SelectionChanged;
    }

    /// <summary>Raised when the document's title (tab caption) should change.</summary>
    public event EventHandler? TitleChanged;

    /// <summary>Raised after any execution completes, so the shell can update the status bar.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Raised when an open transaction starts or ends.</summary>
    public event EventHandler? CommitStateChanged;

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

        // Re-binding the list clears the selection for a moment; that isn't the user switching
        // connection, so it mustn't ask about an open transaction.
        _suppressConnectionGuard = true;
        try
        {
            ConnectionCombo.ItemsSource = list;
            ConnectionCombo.DisplayMemberPath = nameof(ConnectionInfo.Name);
            ConnectionCombo.SelectedItem =
                list.FirstOrDefault(c => c.Id == selectId) ?? list.FirstOrDefault();
        }
        finally
        {
            _suppressConnectionGuard = false;
        }
        PrewarmSchema();
    }

    private async void ConnectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConnectionGuard) return;

        // A held-open transaction belongs to the connection it started on: settle it before moving.
        if (_session is { } session && Connection?.Id != session.Connection.Id)
        {
            if (!await ResolvePendingAsync("switching connection"))
            {
                _suppressConnectionGuard = true;
                Connection = ConnectionCombo.Items.OfType<ConnectionInfo>()
                    .FirstOrDefault(c => c.Id == session.Connection.Id);
                _suppressConnectionGuard = false;
                return;
            }
            await CloseSessionAsync();
        }

        PrewarmSchema();
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
        Status("Messages cleared.");
    }

    public void Cancel()
    {
        _cts?.Cancel();
        Status("Cancelling…");
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
        if (string.IsNullOrWhiteSpace(sql)) { Status("Nothing to run."); return; }
        if (Connection is not { } conn) { Status("Select a connection first."); return; }
        if (_shell is null) return;

        // A held-open transaction can't follow the tab to a different connection.
        if (_session is { } held && held.Connection.Id != conn.Id)
        {
            if (held.HasPendingTransaction)
            {
                Status($"This tab has an open transaction on {held.Connection.Name}. " +
                       $"Commit or roll back before running against {conn.Name}.");
                return;
            }
            await CloseSessionAsync();
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        StatsText.Text = "Executing…";
        Status("Executing…");

        var result = AutoCommitBox.IsChecked == true
            ? await QueryRunner.ExecuteAsync(conn, sql, cts.Token)
            : await ExecuteInTransactionAsync(conn, sql, cts);

        if (result is null)
        {
            // Cancelled while it waited its turn. If nothing newer has started, say so.
            if (ReferenceEquals(_cts, cts)) { StatsText.Text = string.Empty; Status("Query cancelled."); }
            return;
        }

        _shell.Store.TouchConnection(conn.Id);

        RenderResult(result);

        var rows = result.Grids.Sum(g => g.Rows.Count);
        var summary = $"{result.Grids.Count} grid(s), {rows} row(s), {result.TotalRowsAffected} affected · {result.Elapsed.TotalMilliseconds:0} ms";
        StatsText.Text = summary;
        Status(result.HasError ? "Query failed" : "Query completed");
    }

    /// <summary>
    /// Runs on the tab's held connection, inside its transaction. Null when the run was
    /// cancelled before the connection came free (a newer run, or Cancel, got there first).
    /// </summary>
    private async Task<QueryExecutionResult?> ExecuteInTransactionAsync(
        ConnectionInfo conn, string sql, CancellationTokenSource cts)
    {
        await _sessionGate.WaitAsync();
        try
        {
            if (cts.IsCancellationRequested) return null;

            _session ??= new QuerySession(conn);
            var result = await _session.ExecuteAsync(sql, cts.Token);
            if (_session.HasPendingTransaction)
                result.Messages.Add("Transaction open: nothing run in this tab is saved until you Commit.");
            return result;
        }
        finally
        {
            _sessionGate.Release();
            OnCommitStateChanged();
        }
    }

    // ---- Transactions (auto-commit off) ------------------------------------

    /// <summary>Whether the tab holds an open transaction.</summary>
    public bool CanCommit => _session?.HasPendingTransaction == true;

    public Task CommitAsync() => SettleFromButtonAsync(commit: true);

    public Task RollbackAsync() => SettleFromButtonAsync(commit: false);

    /// <summary>Settles any open transaction and releases the held connection before the tab closes.</summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        if (!await ResolvePendingAsync("closing this tab")) return false;
        await CloseSessionAsync();
        return true;
    }

    private void Commit_Click(object sender, RoutedEventArgs e) => _ = CommitAsync();
    private void Rollback_Click(object sender, RoutedEventArgs e) => _ = RollbackAsync();

    private async void AutoCommit_Click(object sender, RoutedEventArgs e)
    {
        if (AutoCommitBox.IsChecked != true)
        {
            Status("Auto-commit off: changes are held until you Commit or Roll back.");
            return;
        }

        // Back to auto-commit: settle the open transaction, then let the held connection go.
        if (!await ResolvePendingAsync("switching auto-commit back on"))
        {
            AutoCommitBox.IsChecked = false;
            return;
        }
        await CloseSessionAsync();
        Status("Auto-commit on: each statement is saved as soon as it runs.");
    }

    /// <summary>The Commit / Rollback buttons and menu items: act now, or say why not.</summary>
    private async Task SettleFromButtonAsync(bool commit)
    {
        if (!CanCommit) { Status(commit ? "Nothing to commit." : "Nothing to roll back."); return; }

        // Settling mid-query would commit or undo half of it; the running query has to finish first.
        if (_sessionGate.CurrentCount == 0)
        {
            Status("A query is still running in this tab. Wait for it, or cancel it, first.");
            return;
        }

        _ = commit ? await CommitCoreAsync() : await RollbackCoreAsync();
    }

    /// <summary>
    /// Settles an open transaction before something that would otherwise drop it, asking whether
    /// to commit or roll back. False when the user chose to keep it, or the commit failed.
    /// </summary>
    private async Task<bool> ResolvePendingAsync(string action)
    {
        if (!CanCommit) return true;

        var answer = MessageBox.Show(Window.GetWindow(this),
            "This tab has an open transaction that hasn't been committed.\n\n" +
            $"Commit it before {action}?\n\nYes: commit\nNo: roll back\nCancel: keep it open",
            "Uncommitted transaction", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel) return false;

        // A query still running belongs to the transaction being settled, so stop it first.
        _cts?.Cancel();
        return answer == MessageBoxResult.Yes ? await CommitCoreAsync() : await RollbackCoreAsync();
    }

    private Task<bool> CommitCoreAsync() =>
        SettleAsync(s => s.CommitAsync(), "Commit complete.", "Commit failed");

    private Task<bool> RollbackCoreAsync() =>
        SettleAsync(s => s.RollbackAsync(), "Rollback complete.", "Rollback failed");

    private async Task<bool> SettleAsync(Func<QuerySession, Task> settle, string done, string failed)
    {
        if (_session is not { } session) return true;

        await _sessionGate.WaitAsync();
        try
        {
            await settle(session);
            AppendMessage(done);
            Status(done);
            return true;
        }
        catch (Exception ex)
        {
            AppendMessage($"{failed}: {ex.Message}");
            Status(failed);
            MessageBox.Show(Window.GetWindow(this), $"{failed}:\n\n{ex.Message}", failed,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            _sessionGate.Release();
            OnCommitStateChanged();
        }
    }

    /// <summary>Rolls back anything still open and closes the held connection.</summary>
    private async Task CloseSessionAsync()
    {
        if (_session is not { } session) return;

        _cts?.Cancel();
        await _sessionGate.WaitAsync();
        try
        {
            await session.DisposeAsync();
        }
        finally
        {
            _session = null;
            _sessionGate.Release();
            OnCommitStateChanged();
        }
    }

    private void OnCommitStateChanged()
    {
        var pending = CanCommit;
        CommitButton.IsEnabled = RollbackButton.IsEnabled = pending;
        TransactionText.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        CommitStateChanged?.Invoke(this, EventArgs.Empty);
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
                // Right-click: execute / commit / rollback / select all / copy / save. Execute
                // re-runs the query exactly as F5 does. The name is resolved on use so a renamed
                // tab seeds the save dialog with its current name.
                ResultGridMenu.Attach(grid, () => label, Status, Run, commits: _commits);
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
        ResultGridMenu.Save(grid, grid.Tag as string ?? Title, Status);
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

    private void Status(string message) => StatusChanged?.Invoke(this, message);

    private void ShowMessages(IEnumerable<string> lines)
    {
        ResultTabs.Items.Clear();
        ResultTabs.Items.Add(new TabItem { Header = "Messages", Content = MakeTextBox(string.Join("\n", lines)) });
    }

    /// <summary>Adds a line to the Messages pane — how a commit or rollback confirms itself.</summary>
    private void AppendMessage(string line)
    {
        if (ResultTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == "Messages")
                ?.Content is not TextBox box)
        {
            ShowMessages(new[] { line });
            return;
        }
        box.AppendText((box.Text.Length > 0 ? "\n" : "") + line);
        box.ScrollToEnd();
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
