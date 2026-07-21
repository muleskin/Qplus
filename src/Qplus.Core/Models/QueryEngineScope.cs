namespace Qplus.Core.Models;

/// <summary>Which database engines a saved query is valid for.</summary>
public enum QueryEngineScope
{
    /// <summary>Runs against either engine.</summary>
    Any = 0,

    /// <summary>SQL Server dialect only.</summary>
    SqlServerOnly = 1,

    /// <summary>Oracle dialect only.</summary>
    OracleOnly = 2,
}

public static class QueryEngineScopeExtensions
{
    public static string ToLabel(this QueryEngineScope scope) => scope switch
    {
        QueryEngineScope.SqlServerOnly => "SQL Server only",
        QueryEngineScope.OracleOnly => "Oracle only",
        _ => "Either",
    };

    /// <summary>True when a query with this scope can run against the given engine.</summary>
    public static bool AppliesTo(this QueryEngineScope scope, DbEngineKind engine) => scope switch
    {
        QueryEngineScope.SqlServerOnly => engine == DbEngineKind.SqlServer,
        QueryEngineScope.OracleOnly => engine == DbEngineKind.Oracle,
        _ => true,
    };

    /// <summary>The natural scope for a connection's engine.</summary>
    public static QueryEngineScope FromEngine(DbEngineKind engine) => engine switch
    {
        DbEngineKind.SqlServer => QueryEngineScope.SqlServerOnly,
        DbEngineKind.Oracle => QueryEngineScope.OracleOnly,
        _ => QueryEngineScope.Any,
    };
}
