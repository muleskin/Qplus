using System.Data;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Qplus.Core.Data;
using Qplus.Core.Models;

namespace Qplus.App.Views;

/// <summary>
/// SQL-Developer-style detail view for one table/view: Columns, Data (editable),
/// Constraints, Indexes, Triggers, Dependencies, Grants, Statistics and generated SQL.
/// Panes load lazily on first selection and can each be refreshed.
/// </summary>
public partial class TableDetailsView : UserControl
{
    private const string DataTabKey = "__data";
    private const string SqlTabKey = "__sql";

    private readonly ConnectionInfo _conn;
    private readonly string _schema;
    private readonly string _table;

    private readonly Dictionary<TableDetailKind, DataGrid> _detailGrids = new();
    private readonly HashSet<string> _loaded = new();

    private DataGrid _dataGrid = null!;
    private TextBox _rowLimitBox = null!;
    private TextBox _ddlBox = null!;
    private DataTable? _data;

    public TableDetailsView(ConnectionInfo conn, string schema, string table)
    {
        InitializeComponent();
        _conn = conn;
        _schema = schema;
        _table = table;

        TitleText.Text = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
        SubtitleText.Text = $"{conn.Name} · {DbEngines.For(conn).DisplayName}";

        BuildTabs();
    }

    // ================= Tab construction =================

    private void BuildTabs()
    {
        AddDetailTab("Columns", TableDetailKind.Columns);
        AddDataTab();
        AddDetailTab("Constraints", TableDetailKind.Constraints);
        AddDetailTab("Indexes", TableDetailKind.Indexes);
        AddDetailTab("Triggers", TableDetailKind.Triggers);
        AddDetailTab("Dependencies", TableDetailKind.Dependencies);
        AddDetailTab("Grants", TableDetailKind.Grants);
        AddDetailTab("Statistics", TableDetailKind.Statistics);
        AddSqlTab();

        DetailTabs.SelectedIndex = 0;
        _ = EnsureLoadedAsync(DetailTabs.SelectedItem as TabItem);
    }

    /// <summary>A read-only, sortable grid pane backed by one metadata query.</summary>
    private void AddDetailTab(string header, TableDetailKind kind)
    {
        var grid = NewGrid(readOnly: true);
        _detailGrids[kind] = grid;

        var bar = NewToolbar();
        bar.Children.Add(NewButton("⟳ Refresh", (_, _) => _ = LoadDetailAsync(kind, force: true)));
        bar.Children.Add(NewButton("Export CSV…", (_, _) => ExportGrid(grid, $"{_table}_{kind}")));

        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(grid);

        DetailTabs.Items.Add(new TabItem { Header = header, Content = panel, Tag = kind });
    }

    /// <summary>The editable data pane.</summary>
    private void AddDataTab()
    {
        _dataGrid = NewGrid(readOnly: false);

        _rowLimitBox = new TextBox { Text = "200", Width = 60, VerticalAlignment = VerticalAlignment.Center };
        _rowLimitBox.ToolTip = "Maximum rows to fetch";

        var bar = NewToolbar();
        bar.Children.Add(new TextBlock
        {
            Text = "Rows:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 4, 0),
        });
        bar.Children.Add(_rowLimitBox);
        bar.Children.Add(NewButton("⟳ Refresh", (_, _) => _ = LoadDataAsync(force: true)));
        bar.Children.Add(NewButton("＋ Add row", (_, _) => AddRow()));
        bar.Children.Add(NewButton("－ Delete row", (_, _) => DeleteRows()));
        bar.Children.Add(NewButton("💾 Save changes", (_, _) => _ = SaveDataAsync()));
        bar.Children.Add(NewButton("↺ Revert", (_, _) => RevertChanges()));
        bar.Children.Add(NewButton("Export CSV…", (_, _) => ExportGrid(_dataGrid, _table)));

        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(_dataGrid);

        DetailTabs.Items.Add(new TabItem { Header = "Data", Content = panel, Tag = DataTabKey });
    }

    /// <summary>Generated CREATE script.</summary>
    private void AddSqlTab()
    {
        _ddlBox = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
        };

        var bar = NewToolbar();
        bar.Children.Add(NewButton("⟳ Refresh", (_, _) => _ = LoadDdlAsync(force: true)));
        bar.Children.Add(NewButton("Copy", (_, _) =>
        {
            if (!string.IsNullOrEmpty(_ddlBox.Text)) Clipboard.SetText(_ddlBox.Text);
            SetStatus("DDL copied to clipboard.");
        }));

        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(_ddlBox);

        DetailTabs.Items.Add(new TabItem { Header = "SQL", Content = panel, Tag = SqlTabKey });
    }

    // ================= Loading =================

    private void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, DetailTabs)) return; // ignore inner selection changes
        _ = EnsureLoadedAsync(DetailTabs.SelectedItem as TabItem);
    }

    private async Task EnsureLoadedAsync(TabItem? tab)
    {
        if (tab?.Tag is null) return;
        switch (tab.Tag)
        {
            case TableDetailKind kind: await LoadDetailAsync(kind, force: false); break;
            case DataTabKey: await LoadDataAsync(force: false); break;
            case SqlTabKey: await LoadDdlAsync(force: false); break;
        }
    }

    private async Task LoadDetailAsync(TableDetailKind kind, bool force)
    {
        var key = kind.ToString();
        if (!force && !_loaded.Add(key)) return;
        _loaded.Add(key);

        SetStatus($"Loading {kind}…");
        try
        {
            var engine = DbEngines.For(_conn);
            await using var open = engine.CreateConnection(engine.BuildConnectionString(_conn));
            await open.OpenAsync();
            await using var cmd = engine.CreateTableDetailCommand(open, kind, _schema, _table);
            await using var reader = await cmd.ExecuteReaderAsync();

            var result = new QueryExecutionResult();
            await QueryRunner.CollectResultsAsync(reader, result, CancellationToken.None);

            var grid = _detailGrids[kind];
            if (result.Grids.Count > 0)
            {
                grid.ItemsSource = result.Grids[0].DefaultView;
                SetStatus($"{kind}: {result.Grids[0].Rows.Count} row(s)");
            }
            else
            {
                grid.ItemsSource = null;
                SetStatus($"{kind}: no data");
            }
        }
        catch (Exception ex)
        {
            _loaded.Remove(key);
            SetStatus($"{kind} failed: {ex.Message}");
        }
    }

    private async Task LoadDataAsync(bool force)
    {
        if (!force && !_loaded.Add(DataTabKey)) return;
        _loaded.Add(DataTabKey);

        SetStatus("Loading data…");
        var (table, error) = await TableDataEditor.LoadAsync(_conn, _schema, _table, RowLimit, CancellationToken.None);

        if (error is not null)
        {
            _loaded.Remove(DataTabKey);
            SetStatus("Load failed: " + error);
            return;
        }

        _data = table;
        _dataGrid.ItemsSource = table.DefaultView;

        var editable = TableDataEditor.IsEditable(table);
        _dataGrid.IsReadOnly = !editable;
        _dataGrid.CanUserAddRows = editable;
        _dataGrid.CanUserDeleteRows = editable;

        SetStatus(editable
            ? $"{table.Rows.Count} row(s) — editable (primary key found)"
            : $"{table.Rows.Count} row(s) — read-only: no primary key on this table");
    }

    private async Task LoadDdlAsync(bool force)
    {
        if (!force && !_loaded.Add(SqlTabKey)) return;
        _loaded.Add(SqlTabKey);

        SetStatus("Generating DDL…");
        try
        {
            var engine = DbEngines.For(_conn);
            await using var open = engine.CreateConnection(engine.BuildConnectionString(_conn));
            await open.OpenAsync();
            _ddlBox.Text = await engine.BuildTableDdlAsync(open, _schema, _table, CancellationToken.None);
            SetStatus("DDL ready.");
        }
        catch (Exception ex)
        {
            _loaded.Remove(SqlTabKey);
            SetStatus("DDL failed: " + ex.Message);
        }
    }

    private void RefreshAll_Click(object sender, RoutedEventArgs e)
    {
        _loaded.Clear();
        _ = EnsureLoadedAsync(DetailTabs.SelectedItem as TabItem);
        SetStatus("Refreshed.");
    }

    // ================= Data editing =================

    private int RowLimit =>
        int.TryParse(_rowLimitBox.Text.Trim(), out var n) && n > 0 ? n : 200;

    private void AddRow()
    {
        if (_data is null) { SetStatus("Load the Data tab first."); return; }
        if (!TableDataEditor.IsEditable(_data)) { SetStatus("This table has no primary key, so rows can't be added here."); return; }
        _data.Rows.Add(_data.NewRow());
        SetStatus("Row added — press Save changes to commit.");
    }

    private void DeleteRows()
    {
        if (_data is null) { SetStatus("Load the Data tab first."); return; }
        if (!TableDataEditor.IsEditable(_data)) { SetStatus("This table has no primary key, so rows can't be deleted here."); return; }

        var rows = _dataGrid.SelectedItems.OfType<DataRowView>().ToList();
        if (rows.Count == 0) { SetStatus("Select one or more rows to delete."); return; }

        foreach (var r in rows) r.Row.Delete();
        SetStatus($"{rows.Count} row(s) marked for deletion — press Save changes to commit.");
    }

    private void RevertChanges()
    {
        if (_data is null) return;
        _data.RejectChanges();
        SetStatus("Pending changes reverted.");
    }

    private async Task SaveDataAsync()
    {
        if (_data is null) { SetStatus("Nothing loaded."); return; }

        // Commit any cell/row still being edited so it's part of the change set.
        _dataGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var pending = _data.GetChanges();
        if (pending is null) { SetStatus("No changes to save."); return; }

        var confirm = MessageBox.Show(Window.GetWindow(this),
            $"Write {pending.Rows.Count} changed row(s) to {_schema}.{_table}?",
            "Save changes", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) { SetStatus("Save cancelled."); return; }

        SetStatus("Saving…");
        var result = await TableDataEditor.SaveAsync(_conn, _schema, _table, RowLimit, _data, CancellationToken.None);
        SetStatus(result.Message);

        if (!result.Ok)
        {
            MessageBox.Show(Window.GetWindow(this), result.Message, "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ================= Helpers =================

    private void ExportGrid(DataGrid grid, string suggestedName)
    {
        if (grid.ItemsSource is not DataView view) { SetStatus("Nothing to export."); return; }

        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = suggestedName + ".csv",
        };
        if (dlg.ShowDialog() == true)
        {
            CsvExporter.Write(view.ToTable(), dlg.FileName);
            SetStatus($"Exported to {dlg.FileName}");
        }
    }

    private static DataGrid NewGrid(bool readOnly) => new()
    {
        AutoGenerateColumns = true,
        IsReadOnly = readOnly,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        CanUserSortColumns = true,   // click a header to sort
        CanUserResizeColumns = true,
        CanUserReorderColumns = true,
        EnableRowVirtualization = true,
        SelectionMode = DataGridSelectionMode.Extended,
        SelectionUnit = DataGridSelectionUnit.FullRow,
    };

    private static StackPanel NewToolbar() => new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(4),
    };

    private static Button NewButton(string content, RoutedEventHandler onClick)
    {
        var b = new Button { Content = content, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += onClick;
        return b;
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
