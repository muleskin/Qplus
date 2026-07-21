using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Qplus.Core.Models;
using Qplus.Core.Storage;

namespace Qplus.Core.Sync;

/// <summary>
/// Two-way sync of the saved-query library against a central server.
///
/// Strategy: push everything changed locally since the last watermark, then apply everything
/// the server changed since that same watermark. Conflicts on the same id resolve by
/// last-writer-wins on UpdatedUtc. Deletions travel as tombstones so they propagate instead
/// of being resurrected by the other side.
/// </summary>
public sealed class QuerySyncService
{
    public const string ServerUrlKey = "sync.serverUrl";
    public const string ApiKeyKey = "sync.apiKey";
    /// <summary>Highest server revision pulled — decides what to download.</summary>
    public const string RevKey = "sync.lastRev";

    /// <summary>Local clock watermark — decides what to upload.</summary>
    public const string PushedKey = "sync.lastPushUtc";

    public const string ClientIdKey = "sync.clientId";

    public const string DefaultServerUrl = "https://oillie.cloud";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CatalogStore _store;
    private readonly HttpClient _http;

    public QuerySyncService(CatalogStore store, HttpClient? http = null)
    {
        _store = store;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ---- settings ----------------------------------------------------------

    public string ServerUrl
    {
        get
        {
            var v = _store.GetSetting(ServerUrlKey);
            return string.IsNullOrWhiteSpace(v) ? DefaultServerUrl : v.Trim();
        }
        set => _store.SetSetting(ServerUrlKey, string.IsNullOrWhiteSpace(value) ? DefaultServerUrl : value.Trim());
    }

    public string ApiKey
    {
        get => _store.GetSetting(ApiKeyKey) ?? "";
        set => _store.SetSetting(ApiKeyKey, value ?? "");
    }

    /// <summary>Highest server revision this machine has pulled.</summary>
    public long LastRev =>
        long.TryParse(_store.GetSetting(RevKey), out var v) ? v : 0;

    /// <summary>When this machine last uploaded; local rows newer than this are pushed.</summary>
    public DateTime? LastSyncUtc
    {
        get
        {
            var v = _store.GetSetting(PushedKey);
            return DateTime.TryParse(v, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d : null;
        }
    }

    /// <summary>Stable per-machine id, generated on first use (server-side logging only).</summary>
    public string ClientId
    {
        get
        {
            var v = _store.GetSetting(ClientIdKey);
            if (!string.IsNullOrWhiteSpace(v)) return v;
            var id = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];
            _store.SetSetting(ClientIdKey, id);
            return id;
        }
    }

    // ---- operations --------------------------------------------------------

    /// <summary>Checks the server is reachable and speaking the expected protocol.</summary>
    public async Task<(bool ok, string message)> TestAsync(string? overrideUrl, string? overrideKey, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(overrideUrl) ? ServerUrl : overrideUrl.Trim();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Combine(url, "/api/v1/health"));
            ApplyKey(req, overrideKey ?? ApiKey);
            using var resp = await _http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
                return (false, Describe(resp.StatusCode));

            var body = await resp.Content.ReadAsStringAsync(ct);
            return (true, $"Connected to {url}. {body.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, Friendly(ex, url));
        }
    }

    /// <summary>
    /// Runs a full two-way sync. <paramref name="full"/> ignores the watermark and exchanges
    /// the entire library, which is how you seed a new machine or recover from a reset.
    /// </summary>
    public async Task<SyncResult> SyncAsync(bool full, CancellationToken ct)
    {
        var url = ServerUrl;

        // Two independent watermarks: a server revision decides what to pull, a local
        // timestamp decides what to push. They measure different clocks and must not be mixed.
        var sinceRev = full ? 0 : LastRev;
        var pushSince = full ? null : LastSyncUtc;

        // Capture the cut-off before reading, so edits made during the sync are picked
        // up next time rather than being skipped.
        var pushCutoff = DateTime.UtcNow;

        var outgoing = _store.GetSavedQueriesChangedSince(pushSince);
        var request = new SyncRequest
        {
            ClientId = ClientId,
            SinceRev = sinceRev,
            Queries = outgoing.Select(ToDto).ToList(),
        };

        SyncResponse? response;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Combine(url, "/api/v1/sync"))
            {
                Content = JsonContent.Create(request, options: Json),
            };
            ApplyKey(req, ApiKey);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return SyncResult.Failure($"Sync failed: {Describe(resp.StatusCode)}");

            response = await resp.Content.ReadFromJsonAsync<SyncResponse>(Json, ct);
            if (response is null) return SyncResult.Failure("Sync failed: the server returned an empty response.");
        }
        catch (Exception ex)
        {
            return SyncResult.Failure(Friendly(ex, url));
        }

        // Apply the server's changes, last-writer-wins.
        var downloaded = 0;
        var deleted = 0;
        foreach (var dto in response.Queries)
        {
            var incoming = FromDto(dto);
            var local = _store.GetSavedQuery(incoming.Id);

            // Only take the server's copy if it is genuinely newer than ours.
            if (local is not null && local.UpdatedUtc >= incoming.UpdatedUtc) continue;

            _store.UpsertSavedQueryPreservingTimestamp(incoming);
            if (incoming.IsDeleted) deleted++; else downloaded++;
        }

        _store.SetSetting(RevKey, response.ServerRev.ToString());
        _store.SetSetting(PushedKey, pushCutoff.ToString("o"));

        var msg = $"Sync complete — uploaded {response.Accepted}, downloaded {downloaded}"
                + (deleted > 0 ? $", removed {deleted}" : "")
                + (response.Rejected > 0 ? $" ({response.Rejected} skipped: server had newer)" : "");
        return new SyncResult(true, response.Accepted, downloaded, deleted, msg);
    }

    /// <summary>Clears both watermarks so the next sync exchanges everything.</summary>
    public void ResetWatermark()
    {
        _store.SetSetting(RevKey, "0");
        _store.SetSetting(PushedKey, "");
    }

    // ---- helpers -----------------------------------------------------------

    private void ApplyKey(HttpRequestMessage req, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key)) req.Headers.TryAddWithoutValidation("X-Api-Key", key.Trim());
    }

    private static string Combine(string baseUrl, string path)
    {
        var b = baseUrl.Trim().TrimEnd('/');
        if (!b.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !b.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            b = "https://" + b;
        return b + path;
    }

    private static string Describe(System.Net.HttpStatusCode code) => code switch
    {
        System.Net.HttpStatusCode.Unauthorized => "the server rejected the API key (401).",
        System.Net.HttpStatusCode.Forbidden => "access denied by the server (403).",
        System.Net.HttpStatusCode.NotFound => "the sync endpoint was not found (404) — check the server address.",
        _ => $"the server returned {(int)code} {code}.",
    };

    private static string Friendly(Exception ex, string url) => ex switch
    {
        TaskCanceledException => $"Timed out contacting {url}.",
        HttpRequestException => $"Could not reach {url}. Check the address and your network.",
        _ => $"Sync failed: {ex.Message}",
    };

    internal static QueryDto ToDto(SavedQuery q) => new()
    {
        Id = q.Id, Name = q.Name, Tags = q.Tags, Sql = q.Sql,
        EngineScope = (int)q.Scope,
        CreatedUtc = q.CreatedUtc, UpdatedUtc = q.UpdatedUtc, IsDeleted = q.IsDeleted,
    };

    internal static SavedQuery FromDto(QueryDto d) => new()
    {
        Id = d.Id, Name = d.Name, Tags = d.Tags, Sql = d.Sql,
        Scope = (QueryEngineScope)d.EngineScope,
        CreatedUtc = d.CreatedUtc, UpdatedUtc = d.UpdatedUtc, IsDeleted = d.IsDeleted,
    };
}
