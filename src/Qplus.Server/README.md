# Qplus Query Server

Central store for the shared saved-query library. Qplus clients sync against it in both
directions: local changes are uploaded, server changes are downloaded.

It is a small ASP.NET Core service with a SQLite file behind it. It holds only query text —
**no database credentials and no connection details ever leave the client.**

## Quick start

A `Makefile` automates everything below. Run `make` on its own to list the targets.

```bash
make publish            # single self-contained binary -> ./out/Qplus.Server
sudo make service       # install to /opt/qplus, write the unit, enable and start
make status             # unit state plus a health check
make logs               # follow the journal
```

Override any setting on the command line:

```bash
make publish RID=linux-arm64
sudo make install PORT=8080 API_KEY=$(openssl rand -hex 24)
```

The rest of this document explains what those targets do, and how to do the same by hand.

## Configuration

All settings come from environment variables, so nothing needs editing to deploy.

| Variable | Default | Purpose |
|---|---|---|
| `QPLUS_DB` | `/var/lib/qplus/queries.db` | SQLite file (created on first run) |
| `QPLUS_API_KEY` | *(none)* | Shared secret clients must send as `X-Api-Key`. **Set this.** |
| `QPLUS_URLS` | `http://0.0.0.0:5080` | Listen address |

With no `QPLUS_API_KEY`, the server accepts requests from anyone who can reach it and logs a
warning at startup.

## Build

Publish a self-contained binary — the target machine then needs no .NET installed:

```bash
dotnet publish src/Qplus.Server/Qplus.Server.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o ./out
```

Copy `./out/Qplus.Server` to the server — that one ~48 MB file is everything
(the `.pdb` and `.json` alongside it are not needed to run).

`IncludeNativeLibrariesForSelfExtract` matters: without it the native SQLite library
(`libe_sqlite3.so`) is written next to the binary and must be copied too.

For Raspberry Pi or ARM hosts use `-r linux-arm64`.

## Run it as a service

```bash
sudo useradd --system --no-create-home qplus
sudo mkdir -p /var/lib/qplus /opt/qplus
sudo cp out/Qplus.Server /opt/qplus/
sudo chown -R qplus:qplus /var/lib/qplus /opt/qplus
```

`/etc/systemd/system/qplus.service`:

```ini
[Unit]
Description=Qplus query server
After=network.target

[Service]
Type=simple
User=qplus
ExecStart=/opt/qplus/Qplus.Server
Environment=QPLUS_DB=/var/lib/qplus/queries.db
Environment=QPLUS_URLS=http://127.0.0.1:5080
Environment=QPLUS_API_KEY=change-me
Restart=on-failure
RestartSec=5
# Hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true
ReadWritePaths=/var/lib/qplus

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now qplus
sudo systemctl status qplus
```

## Put TLS in front of it

Bind the service to localhost (as above) and terminate TLS in nginx, so the API key is
never sent in clear text:

```nginx
server {
    listen 443 ssl;
    server_name oillie.cloud;

    ssl_certificate     /etc/letsencrypt/live/oillie.cloud/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/oillie.cloud/privkey.pem;

    location /api/ {
        proxy_pass http://127.0.0.1:5080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    }
}
```

Certificates via `sudo certbot --nginx -d oillie.cloud`.

## Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY out/ .
ENV QPLUS_DB=/data/queries.db
ENV QPLUS_URLS=http://0.0.0.0:5080
VOLUME /data
EXPOSE 5080
ENTRYPOINT ["./Qplus.Server"]
```

```bash
docker run -d --name qplus -p 5080:5080 \
  -v qplus-data:/data -e QPLUS_API_KEY=change-me qplus-server
```

## API

### `GET /api/v1/health`

```json
{ "service": "qplus-query-server", "version": "0.3.0", "queries": 42,
  "serverTimeUtc": "2026-07-21T15:18:10Z" }
```

### `POST /api/v1/sync`

Push and pull in one round trip.

```json
{
  "clientId": "WORKSTATION-1-a1b2c3d4",
  "sinceRev": 128,
  "queries": [
    { "id": "…", "name": "Rig list", "tags": "rigs ops",
      "sql": "SELECT * FROM rig", "engineScope": 2,
      "createdUtc": "…", "updatedUtc": "…", "isDeleted": false }
  ]
}
```

Response:

```json
{ "serverRev": 131, "serverTimeUtc": "…", "queries": [ … ],
  "accepted": 1, "rejected": 0 }
```

`engineScope`: `0` either · `1` SQL Server only · `2` Oracle only.

## How sync works

- **Pull** is driven by `rev`, a counter the *server* assigns on every write. Clients store
  the `serverRev` they last saw and ask for anything above it.
  It is deliberately **not** a timestamp: a row can be written to the server *after* a
  client's watermark while carrying an *older* client timestamp, so a timestamp filter would
  never deliver that row. (This was a real bug, caught by the conflict test.)
- **Push** is driven by a local timestamp — rows edited since the last successful upload.
- **Conflicts** on the same `id` resolve by last-writer-wins on `updatedUtc`.
- **Deletes** travel as tombstones (`isDeleted`), so a deletion propagates instead of being
  resurrected by another client that still holds the row.

## Backup

The whole library is one SQLite file:

```bash
sqlite3 /var/lib/qplus/queries.db ".backup '/backup/queries-$(date +%F).db'"
```
