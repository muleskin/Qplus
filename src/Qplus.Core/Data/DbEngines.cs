using Qplus.Core.Models;

namespace Qplus.Core.Data;

/// <summary>Resolves the <see cref="IDbEngine"/> implementation for an engine kind.</summary>
public static class DbEngines
{
    private static readonly SqlServerEngine Sql = new();
    private static readonly OracleEngine Oracle = new();

    public static IDbEngine For(DbEngineKind kind) => kind switch
    {
        DbEngineKind.SqlServer => Sql,
        DbEngineKind.Oracle => Oracle,
        _ => throw new NotSupportedException($"Unsupported engine: {kind}"),
    };

    public static IDbEngine For(ConnectionInfo info) => For(info.Engine);

    public static IReadOnlyList<IDbEngine> All => new IDbEngine[] { Sql, Oracle };
}
