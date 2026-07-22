using Qplus.Core.Models;
using Qplus.Core.Storage;
using Qplus.Core.Sync;

namespace Qplus.ReaderTests;

/// <summary>
/// Two-way sync tests against a real running query server. Two independent local catalogs
/// stand in for two machines sharing one server.
///
/// Set QPLUS_TEST_SERVER (e.g. http://127.0.0.1:5099) to enable; skips cleanly otherwise.
/// </summary>
public static class SyncTests
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    public static async Task<int> RunAsync()
    {
        _failures = 0;
        Console.WriteLine();
        Console.WriteLine("--- query sync (live server) ---");

        var server = Environment.GetEnvironmentVariable("QPLUS_TEST_SERVER");
        if (string.IsNullOrWhiteSpace(server))
        {
            Console.WriteLine("SKIP  QPLUS_TEST_SERVER not set — skipping sync tests.");
            return 0;
        }

        var dir = Path.Combine(Path.GetTempPath(), "qplus-sync-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            // Two "machines", each with its own catalog, pointed at the same server.
            var storeA = new CatalogStore(Path.Combine(dir, "a.db"));
            var storeB = new CatalogStore(Path.Combine(dir, "b.db"));
            var a = new QuerySyncService(storeA) { ServerUrl = server };
            var b = new QuerySyncService(storeB) { ServerUrl = server };
            var ct = CancellationToken.None;

            // Reachability
            var (ok, msg) = await a.TestAsync(null, null, ct);
            Check("server reachable", ok, msg);
            if (!ok) return _failures;

            // ---- A creates a query, pushes it -------------------------------
            var id = Guid.NewGuid().ToString("N");
            storeA.UpsertSavedQuery(new SavedQuery
            {
                Id = id, Name = "Rig list " + id[..6], Tags = "rigs ops",
                Sql = "SELECT * FROM rig", Scope = QueryEngineScope.OracleOnly,
            });

            var r1 = await a.SyncAsync(full: false, ct);
            Check("A uploads new query", r1.Ok && r1.Uploaded >= 1, r1.Message);

            // ---- B pulls it -------------------------------------------------
            var r2 = await b.SyncAsync(full: false, ct);
            Check("B downloads it", r2.Ok && r2.Downloaded >= 1, r2.Message);

            var onB = storeB.GetSavedQuery(id);
            Check("B has the query", onB is not null, onB is null ? "missing" : onB.Name);
            Check("engine scope survives the round trip",
                onB?.Scope == QueryEngineScope.OracleOnly, onB?.Scope.ToString() ?? "n/a");
            Check("SQL text survives", onB?.Sql == "SELECT * FROM rig", onB?.Sql ?? "n/a");

            // ---- B edits, A picks the edit up --------------------------------
            onB!.Name = "Rig list (edited)";
            onB.Sql = "SELECT rig_id, name FROM rig";
            storeB.UpsertSavedQuery(onB);

            await b.SyncAsync(full: false, ct);
            var r3 = await a.SyncAsync(full: false, ct);
            var onA = storeA.GetSavedQuery(id);
            Check("A receives B's edit",
                r3.Ok && onA?.Name == "Rig list (edited)" && onA.Sql.Contains("rig_id"),
                onA?.Name ?? "n/a");

            // ---- Idempotence: a second sync changes nothing -------------------
            var r4 = await a.SyncAsync(full: false, ct);
            Check("re-syncing with no changes is a no-op",
                r4.Ok && r4.Uploaded == 0 && r4.Downloaded == 0, r4.Message);

            // ---- Conflict: both edit, newer wins ------------------------------
            var conflictId = Guid.NewGuid().ToString("N");
            storeA.UpsertSavedQuery(new SavedQuery { Id = conflictId, Name = "Conflict", Sql = "SELECT 1" });
            await a.SyncAsync(full: false, ct);
            await b.SyncAsync(full: false, ct);

            var aCopy = storeA.GetSavedQuery(conflictId)!;
            aCopy.Sql = "SELECT 'from A'";
            storeA.UpsertSavedQuery(aCopy);

            await Task.Delay(1100, ct);           // ensure B's edit is unambiguously later

            var bCopy = storeB.GetSavedQuery(conflictId)!;
            bCopy.Sql = "SELECT 'from B'";
            storeB.UpsertSavedQuery(bCopy);

            await a.SyncAsync(full: false, ct);   // A pushes its older edit
            await b.SyncAsync(full: false, ct);   // B pushes its newer edit
            await a.SyncAsync(full: false, ct);   // A pulls the winner

            var resolved = storeA.GetSavedQuery(conflictId);
            Check("last writer wins on conflict",
                resolved?.Sql == "SELECT 'from B'", resolved?.Sql ?? "n/a");

            // ---- Deletion propagates via tombstone ----------------------------
            storeB.DeleteSavedQuery(id);
            Check("delete hides it locally",
                storeB.GetSavedQueries().All(q => q.Id != id));

            await b.SyncAsync(full: false, ct);
            var r5 = await a.SyncAsync(full: false, ct);
            Check("deletion propagates to A",
                r5.Ok && storeA.GetSavedQueries().All(q => q.Id != id),
                $"removed={r5.Deleted}");

            // A deleted row must stay deleted rather than being resurrected next sync.
            var r6 = await a.SyncAsync(full: false, ct);
            Check("deleted query is not resurrected",
                storeA.GetSavedQueries().All(q => q.Id != id), r6.Message);

            // ---- Full sync seeds a brand-new machine --------------------------
            var storeC = new CatalogStore(Path.Combine(dir, "c.db"));
            var c = new QuerySyncService(storeC) { ServerUrl = server };
            var r7 = await c.SyncAsync(full: true, ct);
            Check("full sync seeds a new machine",
                r7.Ok && storeC.GetSavedQueries().Count > 0,
                $"{storeC.GetSavedQueries().Count} live queries");
            Check("tombstones are not shown as live queries on the new machine",
                storeC.GetSavedQueries().All(q => q.Id != id));

            // ---- library management: tombstones, restore, status ---------------
            var mgmtId = Guid.NewGuid().ToString("N");
            storeA.UpsertSavedQuery(new SavedQuery
            {
                Id = mgmtId, Name = "Managed " + mgmtId[..6], Tags = "mgmt",
                Sql = "SELECT 1", Scope = QueryEngineScope.Any,
            });

            Check("new query reports as not yet synced",
                SavedQueryStatus.For(storeA.GetSavedQuery(mgmtId)!, a.LastSyncUtc)
                    == SavedQuerySyncState.PendingUpload);

            await a.SyncAsync(full: false, ct);
            Check("after syncing it reports as synced",
                SavedQueryStatus.For(storeA.GetSavedQuery(mgmtId)!, a.LastSyncUtc)
                    == SavedQuerySyncState.Synced);

            storeA.DeleteSavedQuery(mgmtId);
            Check("deleted query appears in the deleted list",
                storeA.GetDeletedSavedQueries().Any(q => q.Id == mgmtId));
            Check("deleted query is out of the live list",
                storeA.GetSavedQueries().All(q => q.Id != mgmtId));

            Check("restore brings it back",
                storeA.RestoreSavedQuery(mgmtId)
                && storeA.GetSavedQueries().Any(q => q.Id == mgmtId));
            Check("restoring twice is a no-op", !storeA.RestoreSavedQuery(mgmtId));

            // A restore must win over the deletion on the other machine.
            await a.SyncAsync(full: false, ct);
            await b.SyncAsync(full: false, ct);
            Check("restore propagates to the other machine",
                storeB.GetSavedQueries().Any(q => q.Id == mgmtId));

            // ---- folders --------------------------------------------------------
            var folderId = Guid.NewGuid().ToString("N");
            storeA.UpsertSavedQuery(new SavedQuery
            {
                Id = folderId, Name = "Foldered " + folderId[..6],
                Folder = "Reporting/Monthly", Tags = "rpt",
                Sql = "SELECT 1", Scope = QueryEngineScope.Any,
            });

            Check("folder is stored and read back",
                storeA.GetSavedQuery(folderId)?.Folder == "Reporting/Monthly",
                storeA.GetSavedQuery(folderId)?.Folder ?? "n/a");

            Check("folder appears in the folder list",
                storeA.GetFolders().Contains("Reporting/Monthly"),
                string.Join(", ", storeA.GetFolders()));

            await a.SyncAsync(full: false, ct);
            await b.SyncAsync(full: false, ct);
            Check("folder survives the sync round trip",
                storeB.GetSavedQuery(folderId)?.Folder == "Reporting/Monthly",
                storeB.GetSavedQuery(folderId)?.Folder ?? "not received");

            // Moving between folders must propagate like any other edit.
            var moved = storeB.GetSavedQuery(folderId)!;
            moved.Folder = "Archive";
            storeB.UpsertSavedQuery(moved);
            await b.SyncAsync(full: false, ct);
            await a.SyncAsync(full: false, ct);
            Check("moving to another folder propagates",
                storeA.GetSavedQuery(folderId)?.Folder == "Archive",
                storeA.GetSavedQuery(folderId)?.Folder ?? "n/a");

            // Clearing the folder must propagate too, not be treated as "no change".
            var cleared = storeA.GetSavedQuery(folderId)!;
            cleared.Folder = "";
            storeA.UpsertSavedQuery(cleared);
            await a.SyncAsync(full: false, ct);
            await b.SyncAsync(full: false, ct);
            Check("clearing the folder propagates",
                storeB.GetSavedQuery(folderId)?.Folder == "",
                storeB.GetSavedQuery(folderId)?.Folder ?? "n/a");
            Check("empty folder is not offered in the folder list",
                !storeB.GetFolders().Contains(""));

            return _failures;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
