<img src="src/Qplus.App/Assets/qplus-256.png" alt="Qplus" width="96" align="right">

# Qplus

A lightweight Windows client for **Microsoft SQL Server** and **Oracle**, covering the
day-to-day work you would otherwise split between SQL Server Management Studio and Oracle
SQL Developer — plus an optional Linux server for sharing a query library across a team.

Both databases are reached **natively**. No ODBC DSN, no Oracle client, no `tnsnames.ora`,
and no .NET installation on the target machine.

---

## Features

| | |
|---|---|
| **Connections** | SQL Server and Oracle, saved and testable. Passwords protected with Windows DPAPI. |
| **Object Explorer** | Lazy-loading tree of schemas, tables, views and columns. |
| **Query editor** | Multiple tabs, each with its own connection. SQL highlighting, `F5` to run, `Ctrl+F5` for the selection. |
| **Auto-completion** | Tables after `FROM`/`JOIN`; columns after `WHERE`/`SELECT`/`ON`, scoped to the statement. Alias- and schema-aware. |
| **Results** | Grid or text output, multiple result sets, a Messages pane that captures `PRINT`, and CSV export. |
| **Table details** | Columns, Data (editable), Constraints, Indexes, Triggers, Dependencies, Grants, Statistics and generated DDL. |
| **Schema tools** | Visual table designer and clone-table, both producing a script you review before running. |
| **User management** | Full editor for account settings, granted roles, system privileges and quotas. |
| **Query sync** | Share the saved-query library through a central server, in both directions. |
| **Query encryption** | AES-256 + HMAC, applied client-side so the server stores ciphertext only. |
| **Themes** | Dark and light, remembered between sessions. |

---

## Getting started

Grab `Qplus.exe` and run it. There is no installer and nothing is written to Program Files
or the registry.

**Requirements:** 64-bit Windows 10 (1607) or later. Nothing else — the .NET runtime and
both database drivers are inside the executable.

### Build it yourself

```bash
dotnet build Qplus.slnx

# single self-contained executable -> publish/Qplus.exe
dotnet publish src/Qplus.App/Qplus.App.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Requires the .NET 8 SDK (or newer — the projects target `net8.0`).

---

## Repository layout

```
src/Qplus.Core/        engine abstraction, storage, metadata, sync, crypto
src/Qplus.App/         WPF application (assembly name: Qplus)
src/Qplus.Server/      Linux query-sync server (ASP.NET Core)
tests/Qplus.ReaderTests/   unit + live integration tests
docs/                  user guide, technical documentation, slide deck, feature matrix
```

All dialect-specific behaviour sits behind two interfaces — `IDbEngine` and `IUserAdmin` —
so supporting another database is largely a matter of adding two classes. `Qplus.Core`
contains no UI types, which is what lets the tests exercise the real execution and
metadata paths without a window.

---

## Query sync server

Optional. It keeps the saved-query library in step across machines and stores **query text
only** — no credentials or connection details ever leave the client.

```bash
sudo ./scripts/install-dotnet.sh   # .NET SDK pinned in global.json (first time only)

cd src/Qplus.Server
make publish      # single self-contained linux-x64 binary in ./out
sudo make install # /opt/qplus + systemd unit + /var/lib/qplus
```

See [`src/Qplus.Server/README.md`](src/Qplus.Server/README.md) for configuration, the
systemd unit, nginx TLS termination, Docker and the API reference. `make help` lists every
target.

Sync is a single round trip. Downloads are driven by a server-assigned revision counter
rather than a timestamp — a row can reach the server *after* a client's watermark while
carrying an *older* client timestamp, so a timestamp filter would silently never deliver
it. Conflicts resolve last-writer-wins; deletions travel as tombstones so they propagate
instead of being resurrected.

---

## Query encryption

Saved queries often encode business logic worth protecting. Qplus can encrypt query names,
tags and SQL so that **neither** the local catalog **nor** the central server holds
readable text.

- **AES-256-CBC** with a fresh random IV per record, so identical queries do not produce
  identical ciphertext
- **HMAC-SHA256** in encrypt-then-MAC order, covering the IV, verified in constant time
  *before* any decryption is attempted
- **Separate** 256-bit encryption and MAC keys
- Keys derived with **PBKDF2-HMAC-SHA256**, 210,000 iterations, cached per machine under DPAPI

Encryption happens entirely on the client, so the server holds no key material and cannot
decrypt even in principle. Enable it from **Query ▸ Encryption…**.

> **There is no recovery.** If the passphrase is lost, every encrypted query is unreadable
> on every machine and on the server. That is the direct consequence of the server holding
> no keys.

---

## Tests

```bash
dotnet run --project tests/Qplus.ReaderTests
```

Four suites, each skipping cleanly when its dependency is unavailable, so the offline
checks always run:

| Suite | Needs | Covers |
|---|---|---|
| Reader | nothing (in-memory SQLite) | Multi-result-set handling, unnamed and duplicate columns |
| Analyzer | nothing (pure functions) | Completion context: keywords, aliases, qualifiers, comment/string safety |
| Sync | a running query server | Upload, download, conflict resolution, tombstones, full-sync seeding |
| Crypto | server only for the end-to-end check | Round trip, tamper detection, wrong key, and proof the server DB holds no plaintext |

Credentials come from environment variables, so nothing sensitive is committed:

```
EDMUSER, EDMPASSWD      database credentials
EDMDSN                  SQL Server database
EDMORASERVICE           Oracle service name
QPLUS_TEST_SERVER       query server base URL
QPLUS_TEST_SERVER_DB    path to the server's SQLite file (plaintext scan)
```

The database-backed suites are strictly **read-only**.

---

## Documentation

- [`docs/Qplus User Guide.docx`](docs/) — day-to-day usage
- [`docs/Qplus Technical Documentation.docx`](docs/) — architecture, crypto design, testing, pitfalls
- [`docs/Qplus Feature Overview.pptx`](docs/) — slide deck
- [`docs/Qplus Feature Matrix.docx`](docs/) — how Qplus compares with SSMS and SQL Developer

---

## Known limitations

- Data editing requires a primary key; tables without one open read-only.
- The table designer generates `CREATE TABLE` and `ALTER TABLE ADD` only — it does not diff
  and alter existing column definitions.
- Oracle `DBMS_OUTPUT` is not captured; only warnings arriving via `InfoMessage` are shown.
- Sync conflict resolution is last-writer-wins per row, not a merge.
- Query encryption uses a fixed PBKDF2 salt so every machine derives the same key from the
  same passphrase; passphrase length is what carries the security.
- Windows-only client, published for x64 (ARM64 runs under emulation).
