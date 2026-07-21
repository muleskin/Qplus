namespace Qplus.Core.Admin;

/// <summary>Current state of a database user/login.</summary>
public sealed class UserDetails
{
    public string Name { get; set; } = "";
    public string DefaultTablespace { get; set; } = "";
    public string TemporaryTablespace { get; set; } = "";
    public string AccountStatus { get; set; } = "";
    public string Profile { get; set; } = "";

    public bool IsLocked { get; set; }
    public bool IsPasswordExpired { get; set; }

    /// <summary>Oracle: IDENTIFIED EXTERNALLY. SQL Server: a Windows login.</summary>
    public bool IsExternalAuth { get; set; }

    public bool EditionsEnabled { get; set; }
}

/// <summary>A role, and whether this user holds it.</summary>
public sealed class RoleGrant
{
    public string Role { get; set; } = "";
    public bool Granted { get; set; }
    public bool AdminOption { get; set; }
    public bool DefaultRole { get; set; }

    // Snapshot of the loaded state, used to work out what actually changed.
    public bool WasGranted { get; set; }
    public bool WasAdminOption { get; set; }
}

/// <summary>A system privilege, and whether this user holds it.</summary>
public sealed class PrivilegeGrant
{
    public string Privilege { get; set; } = "";
    public bool Granted { get; set; }
    public bool AdminOption { get; set; }

    public bool WasGranted { get; set; }
    public bool WasAdminOption { get; set; }
}

/// <summary>A tablespace quota for the user (Oracle).</summary>
public sealed class TablespaceQuota
{
    public string Tablespace { get; set; } = "";

    /// <summary>Quota in bytes; -1 means UNLIMITED; 0 means none.</summary>
    public long MaxBytes { get; set; }

    public long UsedBytes { get; set; }

    public bool Unlimited
    {
        get => MaxBytes < 0;
        set => MaxBytes = value ? -1 : 0;
    }

    public long WasMaxBytes { get; set; }
}

/// <summary>Everything the Edit User dialog shows and can change.</summary>
public sealed class UserEditModel
{
    public UserDetails Details { get; set; } = new();

    /// <summary>Plaintext new password; empty means "leave unchanged".</summary>
    public string NewPassword { get; set; } = "";

    public bool ExpirePassword { get; set; }
    public bool Locked { get; set; }
    public bool ExternalAuth { get; set; }
    public bool EditionsEnabled { get; set; }

    public string DefaultTablespace { get; set; } = "";
    public string TemporaryTablespace { get; set; } = "";

    public List<RoleGrant> Roles { get; set; } = new();
    public List<PrivilegeGrant> Privileges { get; set; } = new();
    public List<TablespaceQuota> Quotas { get; set; } = new();

    /// <summary>True when creating a new user rather than editing an existing one.</summary>
    public bool IsNew { get; set; }
}
