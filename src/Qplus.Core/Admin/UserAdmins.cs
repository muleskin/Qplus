using Qplus.Core.Models;

namespace Qplus.Core.Admin;

/// <summary>Resolves the <see cref="IUserAdmin"/> for an engine.</summary>
public static class UserAdmins
{
    private static readonly SqlServerUserAdmin Sql = new();
    private static readonly OracleUserAdmin Oracle = new();

    public static IUserAdmin For(DbEngineKind kind) => kind switch
    {
        DbEngineKind.SqlServer => Sql,
        DbEngineKind.Oracle => Oracle,
        _ => throw new NotSupportedException($"Unsupported engine: {kind}"),
    };

    public static IUserAdmin For(ConnectionInfo info) => For(info.Engine);
}
