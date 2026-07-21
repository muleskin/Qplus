using System.Collections.ObjectModel;
using Qplus.Core.Data;
using Qplus.Core.Models;

namespace Qplus.App.ViewModels;

/// <summary>
/// A lazily-populated object-explorer node. Real children are loaded the first time
/// the node is expanded, using the supplied <see cref="_loader"/>.
/// </summary>
public sealed class ExplorerNode : Observable
{
    private static readonly ExplorerNode Placeholder = new("…", SchemaNodeKind.SchemaFolder);

    private readonly Func<ExplorerNode, Task<IEnumerable<ExplorerNode>>>? _loader;
    private bool _loaded;
    private bool _isExpanded;
    private bool _isLoading;

    public ExplorerNode(
        string header,
        SchemaNodeKind kind,
        Func<ExplorerNode, Task<IEnumerable<ExplorerNode>>>? loader = null,
        string? detail = null)
    {
        Header = header;
        Kind = kind;
        Detail = detail;
        _loader = loader;
        Children = new ObservableCollection<ExplorerNode>();
        if (loader is not null)
            Children.Add(Placeholder); // gives the node an expand arrow before load
    }

    public string Header { get; }
    public string? Detail { get; }
    public SchemaNodeKind Kind { get; }
    public ObservableCollection<ExplorerNode> Children { get; }

    /// <summary>The connection this node belongs to (propagated to children by the loaders).</summary>
    public ConnectionInfo? Connection { get; set; }

    /// <summary>Schema/owner of the node (for tables/views/columns).</summary>
    public string Schema { get; set; } = "";

    /// <summary>Object name for tables/views (used by right-click actions).</summary>
    public string ObjectName { get; set; } = "";

    public string Icon => Kind switch
    {
        SchemaNodeKind.Server => "🖥",
        SchemaNodeKind.SchemaFolder => "📁",
        SchemaNodeKind.TablesFolder => "📋",
        SchemaNodeKind.ViewsFolder => "🔎",
        SchemaNodeKind.Table => "▦",
        SchemaNodeKind.View => "◫",
        SchemaNodeKind.ColumnsFolder => "🗂",
        SchemaNodeKind.Column => "•",
        _ => "•",
    };

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value)
                _ = EnsureChildrenAsync();
        }
    }

    public async Task EnsureChildrenAsync()
    {
        if (_loaded || _loader is null) return;
        _loaded = true;
        IsLoading = true;
        try
        {
            var children = await _loader(this);
            Children.Clear();
            foreach (var child in children)
            {
                child.Connection = Connection;
                Children.Add(child);
            }
            if (Children.Count == 0)
                Children.Add(new ExplorerNode("(empty)", Kind));
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new ExplorerNode("Error: " + ex.Message, Kind));
        }
        finally
        {
            IsLoading = false;
        }
    }
}
