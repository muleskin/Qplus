using System.Data.Common;
using Qplus.Core.Models;

namespace Qplus.Core.Admin;

/// <summary>
/// Engine-specific user administration: reading a user's current state and generating
/// the DDL for pending changes. Kept separate from IDbEngine so query execution and
/// administration stay independent concerns.
/// </summary>
public interface IUserAdmin
{
    DbEngineKind Engine { get; }

    /// <summary>Whether this engine has tablespaces/quotas (Oracle) — drives UI visibility.</summary>
    bool SupportsTablespaces { get; }

    Task<UserDetails?> GetUserAsync(DbConnection open, string user, CancellationToken ct);

    /// <summary>Tablespace names for the default/temporary pickers (empty when unsupported).</summary>
    Task<IReadOnlyList<string>> ListTablespacesAsync(DbConnection open, CancellationToken ct);

    /// <summary>Every role, flagged with whether this user holds it.</summary>
    Task<IReadOnlyList<RoleGrant>> GetRolesAsync(DbConnection open, string user, CancellationToken ct);

    /// <summary>Every system privilege, flagged with whether this user holds it.</summary>
    Task<IReadOnlyList<PrivilegeGrant>> GetPrivilegesAsync(DbConnection open, string user, CancellationToken ct);

    /// <summary>Per-tablespace quotas (empty when unsupported).</summary>
    Task<IReadOnlyList<TablespaceQuota>> GetQuotasAsync(DbConnection open, string user, CancellationToken ct);

    /// <summary>
    /// The DDL that applies the model's pending changes. Returns an empty string when
    /// nothing has changed. Always shown to the user before it runs.
    /// </summary>
    string BuildAlterScript(UserEditModel model);
}
