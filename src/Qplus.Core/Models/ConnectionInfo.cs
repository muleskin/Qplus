namespace Qplus.Core.Models;

/// <summary>
/// A stored database connection. Passwords are held encrypted at rest (DPAPI) and
/// only decrypted transiently when a connection string is built.
/// </summary>
public sealed class ConnectionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Friendly display name shown in the connection list / object explorer.</summary>
    public string Name { get; set; } = "New Connection";

    public DbEngineKind Engine { get; set; } = DbEngineKind.SqlServer;

    /// <summary>Host name or, for SQL Server, "server\instance".</summary>
    public string Host { get; set; } = "";

    /// <summary>Port. 0 means "use the provider default / not specified".</summary>
    public int Port { get; set; }

    /// <summary>SQL Server: initial catalog. Oracle: service name (or SID when <see cref="OracleUseSid"/>).</summary>
    public string Database { get; set; } = "";

    /// <summary>Oracle only: treat <see cref="Database"/> as a SID rather than a service name.</summary>
    public bool OracleUseSid { get; set; }

    /// <summary>SQL Server only: use Windows / integrated authentication instead of a username/password.</summary>
    public bool IntegratedSecurity { get; set; }

    public string Username { get; set; } = "";

    /// <summary>DPAPI-protected (CurrentUser) password, Base64 encoded. Never the plaintext.</summary>
    public string EncryptedPassword { get; set; } = "";

    /// <summary>SQL Server only: TrustServerCertificate for encrypted connections without a validated cert.</summary>
    public bool TrustServerCertificate { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedUtc { get; set; }

    public ConnectionInfo Clone() => (ConnectionInfo)MemberwiseClone();

    /// <summary>Shown in combo boxes / selection boxes that fall back to ToString.</summary>
    public override string ToString() => Name;
}
