using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Qplus.App.ViewModels;
using Qplus.App.Views;
using Qplus.Core.Completion;
using Qplus.Core.Data;
using Qplus.Core.Models;
using Qplus.Core.Security;
using Qplus.Core.Storage;
using Qplus.Core.Sync;

namespace Qplus.App;

public partial class MainWindow : Window, IShell
{
    private readonly CatalogStore _store = new();
    private List<ConnectionInfo> _connections = new();
    private List<SavedQuery> _savedQueries = new();
    private bool _suppressSavedSelection;
    private int _tabCounter;

    public MainWindow()
    {
        InitializeComponent();

        // Unlock the query library before anything reads it, so encrypted rows decrypt
        // rather than showing as placeholders.
        _keyRing = new QueryKeyRing(_store);
        if (_keyRing.IsUnlocked) _store.ProtectionKeys = _keyRing.Keys;

        LoadConnections();
        LoadSavedQueries();

        if (_keyRing.IsEnabled && !_keyRing.IsUnlocked)
        {
            StatusText.Text = "Query library is encrypted and locked — open Query ▸ Encryption… to unlock.";
        }

        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ActiveDoc?.Run(false)), Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ActiveDoc?.Run(true)), Key.F5, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => NewDocument()), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => CloseActiveTab()), Key.W, ModifierKeys.Control));

        SyncThemeMenu();

        // Start with one empty query tab.
        var doc = NewDocument();
        doc.EditorText = "-- Welcome to Qplus\n-- Pick a connection, write SQL, and press F5 to run.\n\nSELECT 1 AS hello;";
    }

    // ================= Theme =================

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var theme = ReferenceEquals(sender, LightThemeItem) ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.Apply(theme);
        _store.SetSetting(App.ThemeSettingKey, theme.ToString());

        // Recolour SQL highlighting for the new theme and refresh every open editor.
        SqlSyntax.Apply(theme);
        foreach (TabItem tab in DocTabs.Items)
            if (tab.Content is QueryDocumentView doc)
                doc.RefreshSyntax();

        SyncThemeMenu();
    }

    private void SyncThemeMenu()
    {
        DarkThemeItem.IsChecked = ThemeManager.Current == AppTheme.Dark;
        LightThemeItem.IsChecked = ThemeManager.Current == AppTheme.Light;
    }

    // ===== IShell =====
    public IReadOnlyList<ConnectionInfo> Connections => _connections;
    public CatalogStore Store => _store;
    public SchemaCache Schema { get; } = new();

    private QuerySyncService? _sync;
    private QuerySyncService Sync => _sync ??= new QuerySyncService(_store);
    private readonly QueryKeyRing _keyRing;

    private QueryDocumentView? ActiveDoc => (DocTabs.SelectedItem as TabItem)?.Content as QueryDocumentView;

    // ================= Document tabs =================

    private QueryDocumentView NewDocument(string? title = null, string? sql = null, ConnectionInfo? conn = null)
    {
        var doc = new QueryDocumentView { Title = title ?? $"Query {++_tabCounter}" };
        doc.Attach(this);
        if (conn is not null) doc.Connection = conn;
        if (sql is not null) doc.EditorText = sql;
        doc.StatusChanged += (_, msg) => StatusText.Text = msg;

        var tab = AddTab(doc, doc.Title);
        doc.TitleChanged += (_, _) => tab.Header = BuildTabHeader(doc.Title, tab);
        return doc;
    }

    /// <summary>Adds a closeable tab hosting any content (query editor, table details, …).</summary>
    private TabItem AddTab(object content, string title)
    {
        var tab = new TabItem { Content = content };
        tab.Header = BuildTabHeader(title, tab);
        DocTabs.Items.Add(tab);
        DocTabs.SelectedItem = tab;
        return tab;
    }

    private object BuildTabHeader(string title, TabItem tab)
    {
        var panel = new DockPanel { LastChildFill = false };
        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Content = "✕",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.Gray,
            Cursor = Cursors.Hand,
            ToolTip = "Close tab",
        };
        close.Click += (_, _) => CloseTab(tab);
        DockPanel.SetDock(close, Dock.Right);
        panel.Children.Add(close);
        panel.Children.Add(text);
        return panel;
    }

    private void CloseActiveTab()
    {
        if (DocTabs.SelectedItem is TabItem tab) CloseTab(tab);
    }

    private void CloseTab(TabItem tab)
    {
        DocTabs.Items.Remove(tab);
        if (DocTabs.Items.Count == 0) NewDocument();
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e) => CloseActiveTab();
    private void NewQuery_Click(object sender, RoutedEventArgs e) => NewDocument();

    // ================= Connections =================

    private void LoadConnections()
    {
        _connections = _store.GetConnections();
        RefreshAllDocConnections();
        RebuildExplorer();
    }

    private void RefreshAllDocConnections()
    {
        foreach (TabItem tab in DocTabs.Items)
            if (tab.Content is QueryDocumentView doc)
                doc.RefreshConnections(doc.Connection?.Id);
    }

    private void RebuildExplorer()
    {
        ExplorerTree.Items.Clear();
        foreach (var conn in _connections)
            ExplorerTree.Items.Add(ExplorerBuilder.BuildServerNode(conn));
    }

    private void AddConnection_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ConnectionDialog(null) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _store.UpsertConnection(dlg.Result);
            LoadConnections();
            if (ActiveDoc is { } doc && doc.Connection is null)
                doc.RefreshConnections(dlg.Result.Id);
        }
    }

    private void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedExplorerConnection() ?? ActiveDoc?.Connection;
        if (target is null) { Warn("Select a connection first."); return; }
        var dlg = new ConnectionDialog(target) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _store.UpsertConnection(dlg.Result);
            LoadConnections();
        }
    }

    private void RemoveConnection_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedExplorerConnection() ?? ActiveDoc?.Connection;
        if (target is null) { Warn("Select a connection first."); return; }
        if (MessageBox.Show(this, $"Remove connection '{target.Name}'?", "Qplus",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _store.DeleteConnection(target.Id);
            LoadConnections();
        }
    }

    private ConnectionInfo? SelectedExplorerConnection()
        => (ExplorerTree.SelectedItem as ExplorerNode)?.Connection;

    private void RefreshExplorer_Click(object sender, RoutedEventArgs e) => RebuildExplorer();

    // ================= Object explorer interactions =================

    private void ExplorerTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not ExplorerNode node) return;
        if (node.Kind is not (SchemaNodeKind.Table or SchemaNodeKind.View)) return;
        if (node.Connection is not { } conn) return;

        OpenTableDetails(conn, node.Schema, node.ObjectName);
    }

    /// <summary>Opens the SQL-Developer-style detail view for a table/view in a new tab.</summary>
    private void OpenTableDetails(ConnectionInfo conn, string schema, string table)
    {
        var view = new TableDetailsView(conn, schema, table);
        AddTab(view, table);
        StatusText.Text = $"Opened {schema}.{table}";
    }

    private void OpenDetails_Click(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is ExplorerNode { Kind: SchemaNodeKind.Table or SchemaNodeKind.View } node
            && node.Connection is { } conn)
        {
            OpenTableDetails(conn, node.Schema, node.ObjectName);
        }
        else Warn("Select a table or view in the object explorer first.");
    }

    /// <summary>Opens a query tab pre-filled with a SELECT for the selected object.</summary>
    private async void OpenInQueryEditor_Click(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not ExplorerNode { Kind: SchemaNodeKind.Table or SchemaNodeKind.View } node
            || node.Connection is not { } conn)
        {
            Warn("Select a table or view in the object explorer first.");
            return;
        }

        var sql = DbEngines.For(conn).BuildSelectTopSql(node.Schema, node.ObjectName, 1000);
        var doc = NewDocument(node.ObjectName, sql, conn);
        await doc.ExecuteAsync(sql);
    }

    // ================= Query execution (route to active doc) =================

    private void Execute_Click(object sender, RoutedEventArgs e) => ActiveDoc?.Run(false);
    private void ExecuteSelection_Click(object sender, RoutedEventArgs e) => ActiveDoc?.Run(true);
    private void CancelQuery_Click(object sender, RoutedEventArgs e) => ActiveDoc?.Cancel();

    // ================= Saved queries =================

    private void LoadSavedQueries()
    {
        _savedQueries = _store.GetSavedQueries();
        FilterSavedQueries("");
    }

    private void FilterSavedQueries(string term)
    {
        term = term.Trim().ToLowerInvariant();
        var items = string.IsNullOrEmpty(term)
            ? _savedQueries
            : _savedQueries.Where(q => q.SearchBlob.Contains(term)).ToList();

        _suppressSavedSelection = true;
        SavedQueryCombo.ItemsSource = items;
        // DisplayLabel appends the engine scope, e.g. "Rig list  ·  Oracle only".
        SavedQueryCombo.DisplayMemberPath = nameof(SavedQuery.DisplayLabel);
        _suppressSavedSelection = false;
    }

    private void SavedQueryCombo_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Enter or Key.Tab) return;
        var text = SavedQueryCombo.Text;
        FilterSavedQueries(text);
        SavedQueryCombo.IsDropDownOpen = true;
        SavedQueryCombo.Text = text;
    }

    private void SavedQueryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSavedSelection) return;
        if (SavedQueryCombo.SelectedItem is SavedQuery q)
        {
            NewDocument(q.Name, q.Sql, ActiveDoc?.Connection);
            StatusText.Text = $"Loaded saved query '{q.Name}' into a new tab.";
        }
    }

    private void SaveQuery_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveDoc is not { } doc || string.IsNullOrWhiteSpace(doc.EditorText))
        {
            Warn("Active tab is empty."); return;
        }

        var existing = SavedQueryCombo.SelectedItem as SavedQuery;

        // Default the scope to the tab's engine — a query written against one engine
        // usually belongs to it — unless we're re-saving one that already has a scope.
        var suggested = existing?.Scope
            ?? (doc.Connection is { } c ? QueryEngineScopeExtensions.FromEngine(c.Engine) : QueryEngineScope.Any);

        var dlg = new SaveQueryDialog(existing?.Name ?? doc.Title, existing?.Tags ?? "", suggested) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var match = _savedQueries.FirstOrDefault(q =>
            string.Equals(q.Name, dlg.QueryName, StringComparison.OrdinalIgnoreCase));
        var query = match ?? new SavedQuery();
        query.Name = dlg.QueryName;
        query.Tags = dlg.Tags;
        query.Sql = doc.EditorText;
        query.Scope = dlg.Scope;

        _store.UpsertSavedQuery(query);
        LoadSavedQueries();
        StatusText.Text = $"Saved query '{query.Name}'.";
    }

    private void DeleteSavedQuery_Click(object sender, RoutedEventArgs e)
    {
        if (SavedQueryCombo.SelectedItem is not SavedQuery q) { Warn("Pick a saved query to delete."); return; }
        if (MessageBox.Show(this, $"Delete saved query '{q.Name}'?", "Qplus",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _store.DeleteSavedQuery(q.Id);
            LoadSavedQueries();
            SavedQueryCombo.Text = "";
        }
    }

    // ================= Query sync =================

    private async void SyncQueries_Click(object sender, RoutedEventArgs e) => await RunSyncAsync(full: false);

    private async void SyncQueriesFull_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Exchange the entire query library with the server, ignoring the last-sync marker?\n\n" +
            "Use this to seed a new machine or if you think a change was missed.",
            "Full sync", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) await RunSyncAsync(full: true);
    }

    private async Task RunSyncAsync(bool full)
    {
        StatusText.Text = $"Syncing queries with {Sync.ServerUrl}…";
        SyncButton.IsEnabled = false;
        try
        {
            var result = await Sync.SyncAsync(full, CancellationToken.None);
            StatusText.Text = result.Message;

            if (result.Ok)
            {
                LoadSavedQueries();   // pick up anything that arrived
            }
            else
            {
                MessageBox.Show(this,
                    result.Message + "\n\nCheck the server address under Query ▸ Sync settings…",
                    "Query sync", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private void QueryLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new QueryLibraryDialog(_store, Sync) { Owner = this };
        var opened = dlg.ShowDialog();

        if (dlg.Changed) LoadSavedQueries();

        // The dialog closes with a query chosen to open.
        if (opened == true && dlg.SqlToOpen is { } sql)
            NewDocument(dlg.TitleToOpen, sql, ActiveDoc?.Connection);
    }

    private void Encryption_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new EncryptionDialog(_store, _keyRing) { Owner = this };
        dlg.ShowDialog();

        if (dlg.Changed)
        {
            LoadSavedQueries();
            StatusText.Text = _keyRing.IsEnabled
                ? (_keyRing.IsUnlocked ? "Query library is encrypted and unlocked." : "Query library is locked.")
                : "Query encryption is off.";
        }
    }

    private void SyncSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SyncSettingsDialog(Sync) { Owner = this };
        if (dlg.ShowDialog() == true)
            StatusText.Text = $"Query server set to {Sync.ServerUrl}.";
    }

    // ================= Tools =================

    private void TableDesigner_Click(object sender, RoutedEventArgs e)
    {
        var conn = SelectedExplorerConnection() ?? ActiveDoc?.Connection;
        if (conn is null) { Warn("Select or open a connection first."); return; }

        var seedTable = ExplorerTree.SelectedItem as ExplorerNode;
        var dlg = new TableDesignerDialog(conn, seedTable) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.GeneratedSql is { } sql)
            NewDocument("Designer DDL", sql, conn);
    }

    private void UserManagement_Click(object sender, RoutedEventArgs e)
    {
        var conn = SelectedExplorerConnection() ?? ActiveDoc?.Connection;
        if (conn is null) { Warn("Select or open a connection first."); return; }

        var dlg = new UserManagementDialog(conn) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.GeneratedSql is { } sql)
            NewDocument("User DDL", sql, conn);
    }

    private void CloneTable_Click(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not ExplorerNode { Kind: SchemaNodeKind.Table } node)
        {
            Warn("Select a table in the object explorer, then choose Clone Table.");
            return;
        }
        var conn = node.Connection!;
        var engine = DbEngines.For(conn);
        var src = $"{engine.QuoteIdentifier(node.Schema)}.{engine.QuoteIdentifier(node.ObjectName)}";
        var newName = engine.QuoteIdentifier(node.ObjectName + "_copy");

        var sql = conn.Engine == DbEngineKind.SqlServer
            ? $"SELECT * INTO {engine.QuoteIdentifier(node.Schema)}.{newName}\nFROM {src};"
            : $"CREATE TABLE {engine.QuoteIdentifier(node.Schema)}.{newName} AS\nSELECT * FROM {src};";

        NewDocument("Clone " + node.ObjectName,
            "-- Review, edit the target name if needed, then press F5 to clone.\n" + sql, conn);
        StatusText.Text = "Clone script generated — review before running.";
    }

    // ================= File / misc =================

    private void OpenSql_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true)
            NewDocument(Path.GetFileName(dlg.FileName), File.ReadAllText(dlg.FileName), ActiveDoc?.Connection);
    }

    private void SaveSqlFile_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveDoc is not { } doc) return;
        var dlg = new SaveFileDialog { Filter = "SQL files (*.sql)|*.sql", DefaultExt = ".sql" };
        if (dlg.ShowDialog() == true)
            File.WriteAllText(dlg.FileName, doc.EditorText);
    }

    private void About_Click(object sender, RoutedEventArgs e)
        => MessageBox.Show(this,
            "Qplus 0.2.0\nA lightweight SQL Server + Oracle client.\n\n" +
            "Connections and the saved-query library live in a local SQLite catalog under %AppData%\\Qplus.",
            "About Qplus", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Warn(string message) => StatusText.Text = message;
}
