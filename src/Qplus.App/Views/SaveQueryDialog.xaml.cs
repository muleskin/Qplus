using System.Windows;
using Qplus.Core.Models;

namespace Qplus.App.Views;

public partial class SaveQueryDialog : Window
{
    private sealed record ScopeItem(QueryEngineScope Scope, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Shown at the top of the folder list to mean "no folder".</summary>
    private const string NoFolder = "(none)";

    public string QueryName => NameBox.Text.Trim();
    public string Tags => TagsBox.Text.Trim();

    /// <summary>Which engines the saved query is valid for.</summary>
    public QueryEngineScope Scope =>
        (ScopeBox.SelectedItem as ScopeItem)?.Scope ?? QueryEngineScope.Any;

    /// <summary>
    /// Chosen folder, or empty for uncategorised. The combo is editable, so this is either
    /// an existing folder the user picked or a new one they typed.
    /// </summary>
    public string Folder
    {
        get
        {
            var text = (FolderBox.Text ?? "").Trim().Trim('/');
            return string.Equals(text, NoFolder, StringComparison.OrdinalIgnoreCase) ? "" : text;
        }
    }

    /// <param name="existingFolders">Folders already in use, offered in the drop-down.</param>
    /// <param name="suggestedScope">
    /// Pre-selection — usually the active connection's engine, since a query written against
    /// one engine most often belongs to it.
    /// </param>
    public SaveQueryDialog(
        string suggestedName = "",
        string tags = "",
        QueryEngineScope suggestedScope = QueryEngineScope.Any,
        IEnumerable<string>? existingFolders = null,
        string currentFolder = "")
    {
        InitializeComponent();

        NameBox.Text = suggestedName;
        TagsBox.Text = tags;

        var folders = new List<string> { NoFolder };
        folders.AddRange((existingFolders ?? Enumerable.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        FolderBox.ItemsSource = folders;
        FolderBox.Text = string.IsNullOrWhiteSpace(currentFolder) ? "" : currentFolder;

        var items = new[]
        {
            new ScopeItem(QueryEngineScope.Any, "Either — portable SQL"),
            new ScopeItem(QueryEngineScope.SqlServerOnly, "SQL Server only"),
            new ScopeItem(QueryEngineScope.OracleOnly, "Oracle only"),
        };
        ScopeBox.ItemsSource = items;
        ScopeBox.SelectedItem = items.First(i => i.Scope == suggestedScope);
        ScopeBox.SelectionChanged += (_, _) => UpdateHint();
        UpdateHint();

        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void UpdateHint()
    {
        ScopeHint.Text = Scope switch
        {
            QueryEngineScope.SqlServerOnly => "Uses T-SQL syntax — will be flagged when browsing on an Oracle connection.",
            QueryEngineScope.OracleOnly => "Uses Oracle syntax — will be flagged when browsing on a SQL Server connection.",
            _ => "Standard SQL that runs on both engines.",
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(QueryName))
        {
            MessageBox.Show(this, "Please enter a name.", "Save query",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
