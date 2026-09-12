using System.Data;
using System.Data.Common;
using Qplus.Core.Models;

namespace Qplus.Core.Data;

/// <summary>
/// A connection held open across executions with a transaction on it, for a query tab with
/// auto-commit switched off. Nothing run through it is permanent until <see cref="CommitAsync"/>;
/// <see cref="RollbackAsync"/> — or disposing the session — undoes it.
/// <para>
/// A transaction starts with the first execution after the session opens or last settled, so
/// "pending" means something has run since then. A connection runs one command at a time:
/// callers must not overlap calls.
/// </para>
/// </summary>
public sealed class QuerySession : IAsyncDisposable
{
    private readonly IDbEngine _engine;
    private DbConnection? _connection;
    private DbTransaction? _transaction;

    public QuerySession(ConnectionInfo connection)
    {
        Connection = connection;
        _engine = DbEngines.For(connection);
    }

    /// <summary>The connection this session runs against.</summary>
    public ConnectionInfo Connection { get; }

    /// <summary>Whether a transaction is open, holding work not yet committed or rolled back.</summary>
    public bool HasPendingTransaction => _transaction is not null;

    /// <summary>Runs <paramref name="sql"/> inside the session's transaction, opening one if need be.</summary>
    public async Task<QueryExecutionResult> ExecuteAsync(string sql, CancellationToken ct)
    {
        var result = await QueryRunner.RunAsync(async r =>
        {
            await EnsureTransactionAsync(ct);
            await QueryRunner.RunBatchesAsync(_engine, _connection!, _transaction, sql, r, ct);
        });

        // A transaction can end without Commit or Rollback: a COMMIT or ROLLBACK in the SQL itself,
        // an error severe enough for the server to abort it, or the connection dropping. ADO.NET
        // shows the first two by detaching the transaction from its connection.
        if (_transaction is not null
            && (_transaction.Connection is null || _connection?.State != ConnectionState.Open))
        {
            await EndTransactionAsync();
            result.Messages.Add(
                "The transaction was ended on the server — by a COMMIT or ROLLBACK in the SQL, or an " +
                "error that aborted it — so Commit and Rollback no longer apply to it.");
        }

        return result;
    }

    /// <summary>Makes everything run since the transaction began permanent.</summary>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_transaction is null) return;
        try { await _transaction.CommitAsync(ct); }
        finally { await EndTransactionAsync(); }
    }

    /// <summary>Undoes everything run since the transaction began.</summary>
    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_transaction is null) return;
        try { await _transaction.RollbackAsync(ct); }
        finally { await EndTransactionAsync(); }
    }

    /// <summary>Rolls back anything still pending, then closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            // Explicitly, rather than trusting the provider to do it as the connection goes back
            // to the pool: a pooled connection can otherwise keep the transaction (and its locks).
            try { await _transaction.RollbackAsync(); }
            catch { /* the connection may already be gone, and the transaction with it */ }
            await EndTransactionAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private async Task EnsureTransactionAsync(CancellationToken ct)
    {
        if (_connection is { State: not ConnectionState.Open } broken)
        {
            // A dropped connection can't be reused, and any transaction on it went with it.
            var lostWork = _transaction is not null;
            await EndTransactionAsync();
            await broken.DisposeAsync();
            _connection = null;

            if (lostWork)
                throw new InvalidOperationException(
                    "The connection was lost, and the open transaction with it — none of its changes " +
                    "were kept. Run the SQL again to start a new transaction.");
        }

        if (_connection is null)
        {
            var conn = _engine.CreateConnection(_engine.BuildConnectionString(Connection));
            try { await conn.OpenAsync(ct); }
            catch { await conn.DisposeAsync(); throw; }
            _connection = conn;
        }

        _transaction ??= await _connection.BeginTransactionAsync(ct);
    }

    private async Task EndTransactionAsync()
    {
        if (_transaction is null) return;
        await _transaction.DisposeAsync();
        _transaction = null;
    }
}
