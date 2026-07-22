namespace Qplus.Core.Models;

/// <summary>Where a saved query stands relative to the central server.</summary>
public enum SavedQuerySyncState
{
    /// <summary>Edited (or created) since the last successful upload — the server does not have this version.</summary>
    PendingUpload,

    /// <summary>Unchanged since the last successful upload.</summary>
    Synced,

    /// <summary>Deleted locally; the tombstone still has to reach the server.</summary>
    PendingDelete,
}

public static class SavedQueryStatus
{
    /// <summary>
    /// Works out whether a query still has to be uploaded, by comparing its edit time with
    /// the watermark of the last successful push. A null watermark means this machine has
    /// never synced, so everything is pending.
    /// </summary>
    public static SavedQuerySyncState For(SavedQuery query, DateTime? lastPushUtc)
    {
        var pending = lastPushUtc is null || query.UpdatedUtc > lastPushUtc.Value;

        if (query.IsDeleted)
            return pending ? SavedQuerySyncState.PendingDelete : SavedQuerySyncState.Synced;

        return pending ? SavedQuerySyncState.PendingUpload : SavedQuerySyncState.Synced;
    }

    public static string ToLabel(this SavedQuerySyncState state) => state switch
    {
        SavedQuerySyncState.PendingUpload => "Not yet synced",
        SavedQuerySyncState.PendingDelete => "Delete pending",
        _ => "Synced",
    };
}
