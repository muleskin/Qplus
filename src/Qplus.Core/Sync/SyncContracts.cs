namespace Qplus.Core.Sync;

/// <summary>
/// Wire format for one query. Deliberately a separate DTO from SavedQuery so the server
/// contract can stay stable while the local model evolves (and so the server needs no
/// database-driver dependencies).
/// </summary>
public sealed class QueryDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Sql { get; set; } = "";

    /// <summary>0 = any, 1 = SQL Server only, 2 = Oracle only.</summary>
    public int EngineScope { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Push + pull in a single round trip.</summary>
public sealed class SyncRequest
{
    /// <summary>Identifies the machine, for server-side logging only.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Server revision already held; 0 asks for the whole library.</summary>
    public long SinceRev { get; set; }

    /// <summary>Locally-changed queries to upload.</summary>
    public List<QueryDto> Queries { get; set; } = new();
}

public sealed class SyncResponse
{
    /// <summary>
    /// Highest server revision the client now holds; stored as the next SinceRev.
    /// A server-assigned counter rather than a clock — a row can be written to the server
    /// after a client's watermark while carrying an older client timestamp, so a
    /// timestamp-based filter would silently never deliver it.
    /// </summary>
    public long ServerRev { get; set; }

    /// <summary>The server's clock when it answered — informational only.</summary>
    public DateTime ServerTimeUtc { get; set; }

    /// <summary>Queries changed on the server since the request's watermark.</summary>
    public List<QueryDto> Queries { get; set; } = new();

    /// <summary>How many uploaded rows the server actually accepted (newer than what it held).</summary>
    public int Accepted { get; set; }

    /// <summary>How many uploaded rows were ignored because the server had a newer version.</summary>
    public int Rejected { get; set; }
}

/// <summary>What a sync did, for reporting back to the user.</summary>
public sealed record SyncResult(
    bool Ok,
    int Uploaded,
    int Downloaded,
    int Deleted,
    string Message)
{
    public static SyncResult Failure(string message) => new(false, 0, 0, 0, message);
}
