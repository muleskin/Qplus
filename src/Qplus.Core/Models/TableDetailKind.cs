namespace Qplus.Core.Models;

/// <summary>The metadata panes available on the table-details view.</summary>
public enum TableDetailKind
{
    Columns,
    Constraints,
    Indexes,
    Triggers,
    Dependencies,
    Grants,
    Statistics,
}
