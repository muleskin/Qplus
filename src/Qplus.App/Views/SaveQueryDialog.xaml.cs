using System.Windows;
using System.Windows.Controls;
using Qplus.Core.Models;

namespace Qplus.App.Views;

public partial class SaveQueryDialog : Window
{
    private sealed record ScopeItem(QueryEngineScope Scope, string Label)
    {
        public override string ToString() => Label;
    }

    public string QueryName => NameBox.Text.Trim();
    public string Tags => TagsBox.Text.Trim();

    /// <summary>Which engines the saved query is valid for.</summary>
    public QueryEngineScope Scope =>
        (ScopeBox.SelectedItem as ScopeItem)?.Scope ?? QueryEngineScope.Any;

    /// <param name="suggestedScope">
    /// Pre-selection — usually the active connection's engine, since a query written against
    /// one engine most often belongs to it.
    /// </param>
    public SaveQueryDialog(
        string suggestedName = "",
        string tags = "",
        QueryEngineScope suggestedScope = QueryEngineScope.Any)
    {
        InitializeComponent();

        NameBox.Text = suggestedName;
        TagsBox.Text = tags;

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
