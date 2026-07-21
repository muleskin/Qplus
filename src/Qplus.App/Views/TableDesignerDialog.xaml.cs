using System.Collections.ObjectModel;
using System.Windows;
using Qplus.App.ViewModels;
using Qplus.Core.Data;
using Qplus.Core.Models;

namespace Qplus.App.Views;

public partial class TableDesignerDialog : Window
{
    private readonly ConnectionInfo _conn;
    private readonly IDbEngine _engine;
    private readonly ObservableCollection<ColumnDesign> _columns = new();

    /// <summary>SQL emitted when the user chose Generate; null if cancelled.</summary>
    public string? GeneratedSql { get; private set; }

    public TableDesignerDialog(ConnectionInfo conn, ExplorerNode? seed)
    {
        InitializeComponent();
        _conn = conn;
        _engine = DbEngines.For(conn);

        EngineLabel.Text = $"({_engine.DisplayName})";
        TypeColumn.ItemsSource = _engine.CommonColumnTypes;
        ColumnsGrid.ItemsSource = _columns;

        // Seed schema/table from the explorer selection when available.
        if (seed is not null && !string.IsNullOrEmpty(seed.Schema))
            SchemaBox.Text = seed.Schema;
        if (seed is { Kind: SchemaNodeKind.Table })
        {
            TableNameBox.Text = seed.ObjectName;
        }
        else
        {
            LoadColumnsButton.IsEnabled = false; // nothing to load for a brand-new table
        }

        if (_columns.Count == 0)
            _columns.Add(new ColumnDesign { Name = "id", DataType = _engine.CommonColumnTypes[0], Nullable = false, PrimaryKey = true });
    }

    private void AddColumn_Click(object sender, RoutedEventArgs e)
        => _columns.Add(new ColumnDesign { DataType = _engine.CommonColumnTypes[0] });

    private void RemoveColumn_Click(object sender, RoutedEventArgs e)
    {
        if (ColumnsGrid.SelectedItem is ColumnDesign c) _columns.Remove(c);
    }

    private async void LoadColumns_Click(object sender, RoutedEventArgs e)
    {
        var schema = SchemaBox.Text.Trim();
        var table = TableNameBox.Text.Trim();
        if (string.IsNullOrEmpty(table)) { StatusText.Text = "Enter a table name to load."; return; }

        StatusText.Text = "Loading columns…";
        try
        {
            await using var open = _engine.CreateConnection(_engine.BuildConnectionString(_conn));
            await open.OpenAsync();
            var cols = await _engine.GetColumnsAsync(open, schema, table, CancellationToken.None);
            _columns.Clear();
            foreach (var c in cols)
            {
                // Detail is like "varchar(50) null" — split off the type text for display only.
                var detail = c.Detail ?? "";
                var nullable = detail.EndsWith("null", StringComparison.OrdinalIgnoreCase)
                               && !detail.EndsWith("not null", StringComparison.OrdinalIgnoreCase);
                _columns.Add(new ColumnDesign { Name = c.Name, DataType = detail.Split(' ')[0], Nullable = nullable });
            }
            StatusText.Text = $"Loaded {_columns.Count} columns. Add new rows and use ALTER ADD, or edit and CREATE a copy.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Load failed: " + ex.Message;
        }
    }

    private TableDesign BuildDesign() => new()
    {
        Schema = SchemaBox.Text.Trim(),
        Name = TableNameBox.Text.Trim(),
        Columns = _columns.ToList(),
    };

    private bool Validate()
    {
        if (string.IsNullOrWhiteSpace(TableNameBox.Text))
        {
            StatusText.Text = "Table name is required.";
            return false;
        }
        if (_columns.All(c => string.IsNullOrWhiteSpace(c.Name)))
        {
            StatusText.Text = "Add at least one named column.";
            return false;
        }
        return true;
    }

    private void GenerateCreate_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        GeneratedSql = "-- Review, then press F5 to create the table.\n" + _engine.BuildCreateTableSql(BuildDesign());
        DialogResult = true;
    }

    private void GenerateAlter_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        var schema = SchemaBox.Text.Trim();
        var table = TableNameBox.Text.Trim();
        var lines = _columns
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => _engine.BuildAddColumnSql(schema, table, c));
        GeneratedSql = "-- Review, then press F5. One ADD per new column.\n" + string.Join("\n", lines);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
