using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Qplus.Core.Models;
using Qplus.Core.Storage;
using Qplus.Core.Sync;

namespace Qplus.App.Views;

/// <summary>
/// Manages the saved-query library — the one that grows as colleagues sync their own
/// queries in. Search, retag, re-scope, delete in bulk, restore what was deleted, and
/// import or export SQL files.
/// </summary>
public partial class QueryLibraryDialog : Window
{
    /// <summary>Row shape for the grid: the query plus the display-only columns.</summary>
    public sealed class Row
    {
        public required SavedQuery Query { get; init; }
        public string Name => Query.Name;
        public string Tags => Query.Tags;
        public string ScopeLabel => Query.Scope.ToLabel();
        public string UpdatedLocal => Query.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public required string StatusLabel { get; init; }
        public string Sql => Query.Sql;
    }

    private sealed record ScopeChoice(QueryEngineScope? Scope, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly CatalogStore _store;
    private readonly QuerySyncService _sync;

    private List<Row> _rows = new();
    private ICollectionView? _view;

    /// <summary>SQL the caller should open in a new editor tab; null if nothing was opened.</summary>
    public string? SqlToOpen { get; private set; }
    public string? TitleToOpen { get; private set; }

    /// <summary>True when anything changed, so the caller reloads its own list.</summary>
    public bool Changed { get; private set; }

    public QueryLibraryDialog(CatalogStore store, QuerySyncService sync)
    {
        // Assign before InitializeComponent: control events declared in XAML can fire while
        // the tree is being built, and those handlers read these fields.
        _store = store;
        _sync = sync;

        InitializeComponent();

        ScopeFilter.ItemsSource = new[]
        {
            new ScopeChoice(null, "All"),
            new ScopeChoice(QueryEngineScope.Any, "Either"),
            new ScopeChoice(QueryEngineScope.SqlServerOnly, "SQL Server only"),
            new ScopeChoice(QueryEngineScope.OracleOnly, "Oracle only"),
        };
        ScopeFilter.SelectedIndex = 0;

        Reload();
    }

    // ================= loading =================

    private bool ShowingDeleted => ShowDeletedBox.IsChecked == true;

    private void Reload()
    {
        var watermark = _sync.LastSyncUtc;

        var source = ShowingDeleted ? _store.GetDeletedSavedQueries() : _store.GetSavedQueries();
        _rows = source
            .Select(q => new Row { Query = q, StatusLabel = SavedQueryStatus.For(q, watermark).ToLabel() })
            .ToList();

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = MatchesFilter;
        Grid.ItemsSource = _view;

        // Deleted rows can only be restored; live rows can be everything else.
        RestoreButton.Visibility = ShowingDeleted ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = !ShowingDeleted;
        PropsButton.IsEnabled = !ShowingDeleted;
        DuplicateButton.IsEnabled = !ShowingDeleted;
        DeleteButton.IsEnabled = !ShowingDeleted;

        UpdateStatus();
    }

    private bool MatchesFilter(object item)
    {
        if (item is not Row row) return false;

        if (ScopeFilter.SelectedItem is ScopeChoice { Scope: { } wanted } && row.Query.Scope != wanted)
            return false;

        var term = SearchBox.Text.Trim();
        if (term.Length == 0) return true;

        return row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Tags.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Sql.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _view?.Refresh();
        UpdateStatus();
    }

    private void ShowDeleted_Changed(object sender, RoutedEventArgs e) => Reload();

    private void UpdateStatus()
    {
        // Can be reached from a XAML-declared event before the window has finished building.
        if (StatusText is null) return;

        var shown = _view?.Cast<Row>().Count() ?? 0;
        var pending = _rows.Count(r => r.StatusLabel != "Synced");
        var last = _sync.LastSyncUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never";

        StatusText.Text = ShowingDeleted
            ? $"{shown} deleted query(s) — these disappear for good once every machine has synced."
            : $"{shown} of {_rows.Count} shown · {pending} not yet synced · last sync {last} · server {_sync.ServerUrl}";
    }

    // ================= selection =================

    private List<Row> Selected => Grid.SelectedItems.OfType<Row>().ToList();
    private Row? Current => Grid.SelectedItem as Row;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => PreviewBox.Text = Current?.Sql ?? "";

    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!ShowingDeleted) Open_Click(sender, e);
    }

    private bool RequireSelection(out List<Row> rows)
    {
        rows = Selected;
        if (rows.Count > 0) return true;
        StatusText.Text = "Select one or more queries first.";
        return false;
    }

    // ================= actions =================

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } row) { StatusText.Text = "Select a query to open."; return; }
        SqlToOpen = row.Sql;
        TitleToOpen = row.Name;
        DialogResult = true;
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } row) { StatusText.Text = "Select a query to edit."; return; }

        var dlg = new SaveQueryDialog(row.Query.Name, row.Query.Tags, row.Query.Scope) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        row.Query.Name = dlg.QueryName;
        row.Query.Tags = dlg.Tags;
        row.Query.Scope = dlg.Scope;
        _store.UpsertSavedQuery(row.Query);

        Changed = true;
        Reload();
        StatusText.Text = $"Updated '{row.Query.Name}'.";
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } row) { StatusText.Text = "Select a query to duplicate."; return; }

        var copy = new SavedQuery
        {
            Name = UniqueName(row.Query.Name + " (copy)"),
            Tags = row.Query.Tags,
            Sql = row.Query.Sql,
            Scope = row.Query.Scope,
        };
        _store.UpsertSavedQuery(copy);

        Changed = true;
        Reload();
        StatusText.Text = $"Created '{copy.Name}'.";
    }

    private string UniqueName(string candidate)
    {
        var existing = _store.GetSavedQueries().Select(q => q.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(candidate)) return candidate;
        for (var i = 2; ; i++)
        {
            var next = $"{candidate} {i}";
            if (!existing.Contains(next)) return next;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var rows)) return;

        var names = string.Join("\n  ", rows.Take(10).Select(r => r.Name));
        if (rows.Count > 10) names += $"\n  … and {rows.Count - 10} more";

        var answer = MessageBox.Show(this,
            $"Delete {rows.Count} query(s)?\n\n  {names}\n\n" +
            "They are kept until the next sync so they can be restored, then removed everywhere.",
            "Delete queries", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        foreach (var r in rows) _store.DeleteSavedQuery(r.Query.Id);

        Changed = true;
        Reload();
        StatusText.Text = $"Deleted {rows.Count} query(s). Sync to remove them everywhere.";
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var rows)) return;

        var restored = rows.Count(r => _store.RestoreSavedQuery(r.Query.Id));

        Changed = true;
        Reload();
        StatusText.Text = $"Restored {restored} query(s). Sync to bring them back everywhere.";
    }

    // ---- bulk scope -------------------------------------------------------

    private void ScopeAny_Click(object sender, RoutedEventArgs e) => SetScope(QueryEngineScope.Any);
    private void ScopeSql_Click(object sender, RoutedEventArgs e) => SetScope(QueryEngineScope.SqlServerOnly);
    private void ScopeOracle_Click(object sender, RoutedEventArgs e) => SetScope(QueryEngineScope.OracleOnly);

    private void SetScope(QueryEngineScope scope)
    {
        if (ShowingDeleted) { StatusText.Text = "Restore a query before changing it."; return; }
        if (!RequireSelection(out var rows)) return;

        foreach (var r in rows)
        {
            r.Query.Scope = scope;
            _store.UpsertSavedQuery(r.Query);
        }

        Changed = true;
        Reload();
        StatusText.Text = $"Set {rows.Count} query(s) to “{scope.ToLabel()}”.";
    }

    // ---- import / export --------------------------------------------------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(out var rows)) return;

        var dlg = new SaveFileDialog
        {
            Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
            DefaultExt = ".sql",
            FileName = rows.Count == 1 ? SafeFileName(rows[0].Name) + ".sql" : "qplus-queries.sql",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.AppendLine($"-- ===== {r.Name} =====");
            if (!string.IsNullOrWhiteSpace(r.Tags)) sb.AppendLine($"-- tags: {r.Tags}");
            sb.AppendLine($"-- runs against: {r.ScopeLabel}");
            sb.AppendLine(r.Sql.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        File.WriteAllText(dlg.FileName, sb.ToString());
        StatusText.Text = $"Exported {rows.Count} query(s) to {dlg.FileName}";
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;

        var added = 0;
        foreach (var path in dlg.FileNames)
        {
            try
            {
                var sql = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(sql)) continue;

                _store.UpsertSavedQuery(new SavedQuery
                {
                    Name = UniqueName(Path.GetFileNameWithoutExtension(path)),
                    Sql = sql,
                    Tags = "imported",
                    Scope = QueryEngineScope.Any,
                });
                added++;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not import {Path.GetFileName(path)}:\n{ex.Message}",
                    "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        if (added > 0) { Changed = true; Reload(); }
        StatusText.Text = $"Imported {added} file(s). Review the scope, then sync to share them.";
    }

    // ---- sync -------------------------------------------------------------

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = $"Syncing with {_sync.ServerUrl}…";
        var result = await _sync.SyncAsync(full: false, CancellationToken.None);

        Changed = true;
        Reload();
        StatusText.Text = result.Message;

        if (!result.Ok)
            MessageBox.Show(this, result.Message, "Query sync",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = Changed;
}
