namespace Qplus.Core.Models;

/// <summary>A reusable SQL snippet stored in the local SQLite catalog and syncable to a central server.</summary>
public sealed class SavedQuery
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Free-form comma/space separated tags used to boost search matches.</summary>
    public string Tags { get; set; } = "";

    /// <summary>
    /// Folder the query is filed under. Empty means uncategorised. A forward slash nests,
    /// e.g. "Reporting/Monthly", so the library can show a hierarchy without a second table.
    /// </summary>
    public string Folder { get; set; } = "";

    public string Sql { get; set; } = "";

    /// <summary>Which engines this query is valid for.</summary>
    public QueryEngineScope Scope { get; set; } = QueryEngineScope.Any;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Soft-delete tombstone. Deleted rows are kept so the deletion can propagate during sync;
    /// without this a delete on one machine would be resurrected by the other side.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>Text used by the searchable dropdown to match against.</summary>
    public string SearchBlob => $"{Name} {Tags} {Folder} {Scope.ToLabel()}".ToLowerInvariant();

    /// <summary>What the saved-query drop-down shows: "Folder / Name · scope".</summary>
    public string DisplayLabel
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(Folder) ? Name : $"{Folder} / {Name}";
            return Scope == QueryEngineScope.Any ? label : $"{label}  ·  {Scope.ToLabel()}";
        }
    }

    public override string ToString() => DisplayLabel;
}
