using Qplus.Core.Completion;
using Qplus.Core.Models;
using Qplus.Core.Storage;

namespace Qplus.App;

/// <summary>
/// Services a query document needs from the main window: the shared connection list
/// and the catalog store. Keeps documents decoupled from the window itself.
/// </summary>
public interface IShell
{
    IReadOnlyList<ConnectionInfo> Connections { get; }
    CatalogStore Store { get; }

    /// <summary>Shared metadata cache backing editor completion.</summary>
    SchemaCache Schema { get; }
}
