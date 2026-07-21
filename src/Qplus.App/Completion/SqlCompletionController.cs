using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using Qplus.Core.Completion;
using Qplus.Core.Models;

namespace Qplus.App.Completion;

/// <summary>
/// Drives IntelliSense-style completion in a SQL editor: table/view names after FROM/JOIN,
/// and column names after WHERE/SELECT/ON (scoped to the tables in the statement's FROM clause).
/// </summary>
public sealed class SqlCompletionController
{
    private readonly TextEditor _editor;
    private readonly Func<ConnectionInfo?> _connection;
    private readonly SchemaCache _cache;
    private CompletionWindow? _window;

    public SqlCompletionController(TextEditor editor, Func<ConnectionInfo?> connection, SchemaCache cache)
    {
        _editor = editor;
        _connection = connection;
        _cache = cache;

        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Space forces the popup, like SSMS.
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = ShowAsync();
        }
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1) return;
        var ch = e.Text[0];

        // '.' always re-triggers (alias./schema.). Letters only open a new popup;
        // if one is already open AvalonEdit narrows it as you keep typing.
        if (ch == '.')
        {
            _window?.Close();
            _ = ShowAsync();
        }
        else if ((char.IsLetter(ch) || ch == '_') && _window is null)
        {
            _ = ShowAsync();
        }
    }

    private async Task ShowAsync()
    {
        var conn = _connection();
        if (conn is null) return;

        var ctx = SqlContextAnalyzer.Analyze(_editor.Text, _editor.CaretOffset);
        if (ctx.Kind == SqlCompletionKind.None) return;

        var items = ctx.Kind == SqlCompletionKind.Tables
            ? await BuildTableItemsAsync(conn, ctx)
            : await BuildColumnItemsAsync(conn, ctx);

        if (items.Count == 0) return;

        // The caret may have moved while metadata loaded — only show if still valid.
        var caret = _editor.CaretOffset;
        if (caret < ctx.PrefixStart || _window is not null) return;

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = ctx.PrefixStart,
            EndOffset = caret,
        };
        foreach (var item in items) window.CompletionList.CompletionData.Add(item);

        var typed = _editor.Document.GetText(ctx.PrefixStart, caret - ctx.PrefixStart);
        if (typed.Length > 0) window.CompletionList.SelectItem(typed);

        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
    }

    // ---- Item builders ---------------------------------------------------

    private async Task<List<SqlCompletionData>> BuildTableItemsAsync(ConnectionInfo conn, SqlCompletionContext ctx)
    {
        var objects = await _cache.GetObjectsAsync(conn);
        var defaultSchema = DefaultSchema(conn);

        // "schema." typed -> only that schema's objects, inserted unqualified.
        if (!string.IsNullOrEmpty(ctx.Qualifier))
        {
            return objects
                .Where(o => string.Equals(o.Schema, ctx.Qualifier, StringComparison.OrdinalIgnoreCase))
                .Select(o => new SqlCompletionData(o.Name, $"{(o.IsView ? "View" : "Table")}  {o.Qualified}"))
                .ToList();
        }

        return objects
            .Select(o =>
            {
                // Objects in the default schema insert bare; others stay schema-qualified.
                var insert = string.Equals(o.Schema, defaultSchema, StringComparison.OrdinalIgnoreCase)
                    ? o.Name
                    : o.Qualified;
                return new SqlCompletionData(insert, $"{(o.IsView ? "View" : "Table")}  {o.Qualified}");
            })
            .ToList();
    }

    private async Task<List<SqlCompletionData>> BuildColumnItemsAsync(ConnectionInfo conn, SqlCompletionContext ctx)
    {
        var scope = ctx.Tables;

        // "alias." narrows to that one table.
        if (!string.IsNullOrEmpty(ctx.Qualifier))
        {
            var matched = scope.Where(t => t.Matches(ctx.Qualifier)).ToList();
            if (matched.Count > 0) scope = matched;
        }

        if (scope.Count == 0) return new List<SqlCompletionData>();

        var objects = await _cache.GetObjectsAsync(conn);
        var items = new List<SqlCompletionData>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in scope)
        {
            // Resolve the schema when the query didn't qualify the table.
            var schema = table.Schema;
            if (string.IsNullOrEmpty(schema))
            {
                schema = objects.FirstOrDefault(o =>
                    string.Equals(o.Name, table.Name, StringComparison.OrdinalIgnoreCase))?.Schema ?? "";
            }
            if (string.IsNullOrEmpty(schema)) continue;

            var columns = await _cache.GetColumnsAsync(conn, schema, table.Name);
            foreach (var col in columns)
            {
                // With several tables in scope, prefix duplicates so they stay distinguishable.
                if (!seen.Add(col)) continue;
                items.Add(new SqlCompletionData(col, $"Column  {schema}.{table.Name}.{col}"));
            }
        }

        return items;
    }

    private static string DefaultSchema(ConnectionInfo conn) =>
        conn.Engine == DbEngineKind.SqlServer ? "dbo" : conn.Username.ToUpperInvariant();
}
