using System.Collections.Concurrent;
using Qplus.Core.Data;
using Qplus.Core.Models;

namespace Qplus.Core.Completion;

/// <summary>A table or view available for completion.</summary>
public sealed record DbObjectInfo(string Schema, string Name, bool IsView)
{
    public string Qualified => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

/// <summary>
/// Caches per-connection schema metadata for editor completion: the full table/view list
/// (one round trip) and column lists per table (loaded on demand).
/// </summary>
public sealed class SchemaCache
{
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<DbObjectInfo>>> _objects = new();
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<string>>> _columns = new();

    /// <summary>Objects for a connection, loading them if this is the first request.</summary>
    public Task<IReadOnlyList<DbObjectInfo>> GetObjectsAsync(ConnectionInfo info)
        => _objects.GetOrAdd(info.Id, _ => LoadObjectsAsync(info));

    /// <summary>Objects if already cached, else null — never blocks or starts a load.</summary>
    public IReadOnlyList<DbObjectInfo>? PeekObjects(ConnectionInfo info)
        => _objects.TryGetValue(info.Id, out var t) && t.IsCompletedSuccessfully ? t.Result : null;

    /// <summary>Starts loading in the background if not already cached.</summary>
    public void Prewarm(ConnectionInfo info) => _ = GetObjectsAsync(info);

    public Task<IReadOnlyList<string>> GetColumnsAsync(ConnectionInfo info, string schema, string table)
    {
        var key = $"{info.Id}|{schema}|{table}".ToLowerInvariant();
        return _columns.GetOrAdd(key, _ => LoadColumnsAsync(info, schema, table, key));
    }

    public IReadOnlyList<string>? PeekColumns(ConnectionInfo info, string schema, string table)
    {
        var key = $"{info.Id}|{schema}|{table}".ToLowerInvariant();
        return _columns.TryGetValue(key, out var t) && t.IsCompletedSuccessfully ? t.Result : null;
    }

    /// <summary>Drops everything cached for a connection (call after schema changes).</summary>
    public void Invalidate(string connectionId)
    {
        _objects.TryRemove(connectionId, out _);
        foreach (var key in _columns.Keys.Where(k => k.StartsWith(connectionId.ToLowerInvariant() + "|")).ToList())
            _columns.TryRemove(key, out _);
    }

    private async Task<IReadOnlyList<DbObjectInfo>> LoadObjectsAsync(ConnectionInfo info)
    {
        var list = new List<DbObjectInfo>();
        try
        {
            var engine = DbEngines.For(info);
            await using var conn = engine.CreateConnection(engine.BuildConnectionString(info));
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = engine.ListAllObjectsSql();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var schema = r.IsDBNull(0) ? "" : r.GetString(0);
                var name = r.IsDBNull(1) ? "" : r.GetString(1);
                var type = r.IsDBNull(2) ? "" : r.GetString(2);
                if (name.Length > 0)
                    list.Add(new DbObjectInfo(schema, name, type.Equals("VIEW", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch
        {
            // Don't cache a failure — let the next request retry.
            _objects.TryRemove(info.Id, out _);
        }
        return list;
    }

    private async Task<IReadOnlyList<string>> LoadColumnsAsync(
        ConnectionInfo info, string schema, string table, string key)
    {
        var list = new List<string>();
        try
        {
            var engine = DbEngines.For(info);
            await using var conn = engine.CreateConnection(engine.BuildConnectionString(info));
            await conn.OpenAsync();
            var cols = await engine.GetColumnsAsync(conn, schema, table, CancellationToken.None);
            list.AddRange(cols.Select(c => c.Name));
        }
        catch
        {
            _columns.TryRemove(key, out _);
        }
        return list;
    }
}
