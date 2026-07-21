using Qplus.Server;

var builder = WebApplication.CreateBuilder(args);

// Configuration (env vars win, so container/systemd deployment needs no file edits):
//   QPLUS_DB       path to the SQLite file   (default /var/lib/qplus/queries.db)
//   QPLUS_API_KEY  shared secret clients must send as X-Api-Key (default: none = open)
//   QPLUS_URLS     listen address            (default http://0.0.0.0:5080)
var dbPath = Environment.GetEnvironmentVariable("QPLUS_DB")
             ?? builder.Configuration["Qplus:Db"]
             ?? (OperatingSystem.IsWindows()
                 ? Path.Combine(AppContext.BaseDirectory, "queries.db")
                 : "/var/lib/qplus/queries.db");

var apiKey = Environment.GetEnvironmentVariable("QPLUS_API_KEY")
             ?? builder.Configuration["Qplus:ApiKey"]
             ?? "";

var urls = Environment.GetEnvironmentVariable("QPLUS_URLS")
           ?? builder.Configuration["Qplus:Urls"]
           ?? "http://0.0.0.0:5080";
builder.WebHost.UseUrls(urls);

var store = new QueryStore(dbPath);
builder.Services.AddSingleton(store);

var app = builder.Build();

var log = app.Logger;
log.LogInformation("Qplus query server starting. Database: {Db}", dbPath);
if (string.IsNullOrWhiteSpace(apiKey))
    log.LogWarning("QPLUS_API_KEY is not set — the server will accept requests from anyone who can reach it.");

// Simple shared-secret check. Constant-time compare so the key can't be probed by timing.
bool Authorised(HttpRequest req)
{
    if (string.IsNullOrWhiteSpace(apiKey)) return true;
    var supplied = req.Headers["X-Api-Key"].ToString();
    if (string.IsNullOrEmpty(supplied)) return false;
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(supplied),
        System.Text.Encoding.UTF8.GetBytes(apiKey));
}

app.MapGet("/api/v1/health", (HttpRequest req) =>
{
    if (!Authorised(req)) return Results.Unauthorized();
    return Results.Ok(new
    {
        service = "qplus-query-server",
        version = "0.3.0",
        queries = store.Count(),
        serverTimeUtc = DateTime.UtcNow,
    });
});

app.MapPost("/api/v1/sync", (HttpRequest req, SyncRequest request) =>
{
    if (!Authorised(req)) return Results.Unauthorized();

    var response = store.Sync(request);
    log.LogInformation(
        "sync from {Client}: received {In}, accepted {Accepted}, rejected {Rejected}, returned {Out}",
        string.IsNullOrWhiteSpace(request.ClientId) ? "unknown" : request.ClientId,
        request.Queries.Count, response.Accepted, response.Rejected, response.Queries.Count);

    return Results.Ok(response);
});

app.Run();
