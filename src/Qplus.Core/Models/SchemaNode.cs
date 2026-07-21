namespace Qplus.Core.Models;

public enum SchemaNodeKind
{
    Server,
    SchemaFolder,   // e.g. a schema / owner
    TablesFolder,
    ViewsFolder,
    Table,
    View,
    ColumnsFolder,
    Column,
}

/// <summary>A node in the object-explorer tree. Children are loaded lazily.</summary>
public sealed class SchemaNode
{
    public required string Name { get; init; }
    public required SchemaNodeKind Kind { get; init; }

    /// <summary>Schema / owner this object belongs to (empty for folders that span schemas).</summary>
    public string Schema { get; init; } = "";

    /// <summary>Extra detail shown to the right of the name (e.g. column type).</summary>
    public string? Detail { get; init; }

    /// <summary>Fully-qualified, quoted identifier usable in SQL (tables/views only).</summary>
    public string? QualifiedName { get; init; }
}
