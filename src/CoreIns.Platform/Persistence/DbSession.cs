using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoreIns.Platform.Persistence;

/// <summary>
/// Work that must happen inside the owning database transaction just before it commits (outbox rows, audit records),
/// or after it rolls back (audit records of a rejected command). Implementations are scoped services.
/// </summary>
public interface ITransactionParticipant
{
    /// <summary>Order of execution before commit (lower first). The audit chain runs last to hold its lock briefly.</summary>
    int Order { get; }

    /// <summary>Writes staged work on the transaction's connection.</summary>
    Task BeforeCommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken);

    /// <summary>Called after a successful commit: staged work is done and can be discarded.</summary>
    Task AfterCommitAsync(CancellationToken cancellationToken);

    /// <summary>Called after a rollback (also when the commit itself failed); staged work is discarded except what must survive (it is written in a new transaction).</summary>
    Task AfterRollbackAsync(DbSession session, CancellationToken cancellationToken);
}

/// <summary>
/// The unit of work of one DI scope: one PostgreSQL connection shared by every module <see cref="DbContext"/> resolved
/// in the scope, and at most one transaction on it. Command handlers, event handlers, the outbox and the audit writer
/// all write through it, so a domain change, its events and its audit record commit or roll back together (ADR §2
/// rules 7 and 9). Nested <see cref="BeginTransactionAsync"/> calls join the open transaction; only the owner commits.
/// Modules still own their schema and DbContext; the shared transaction is a platform concern.
/// </summary>
public sealed class DbSession : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IServiceProvider _services;
    private readonly List<DbContext> _contexts = [];
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private bool _disposed;

    /// <summary>Creates the session for a scope.</summary>
    public DbSession(NpgsqlDataSource dataSource, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(services);
        _dataSource = dataSource;
        _services = services;
    }

    /// <summary>The scope's connection (created on first use; opened by the first transaction or EF operation).</summary>
    public NpgsqlConnection Connection
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connection ??= _dataSource.CreateConnection();
        }
    }

    /// <summary>The open transaction, if any.</summary>
    public NpgsqlTransaction? Transaction => _transaction;

    /// <summary>True while a transaction is open.</summary>
    public bool InTransaction => _transaction is not null;

    /// <summary>Opens the connection if needed and returns it.</summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = Connection;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    /// <summary>
    /// Begins the scope's transaction, or joins the one already open. Dispose the handle without committing to roll back
    /// (an owner) or to leave the decision to the owner (a joined handle).
    /// </summary>
    public async Task<SessionTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            return new SessionTransaction(this, owner: false);
        }

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        _transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        foreach (var context in _contexts)
        {
            await context.Database.UseTransactionAsync(_transaction, cancellationToken).ConfigureAwait(false);
        }

        return new SessionTransaction(this, owner: true);
    }

    /// <summary>Called by the module DbContext factory so the context shares the connection and joins the open transaction.</summary>
    internal void Track(DbContext context)
    {
        _contexts.Add(context);
        if (_transaction is not null)
        {
            context.Database.UseTransaction(_transaction);
        }
    }

    internal async Task CommitAsync(CancellationToken cancellationToken)
    {
        var transaction = _transaction ?? throw new InvalidOperationException("No transaction to commit.");
        try
        {
            foreach (var context in _contexts.Where(c => c.ChangeTracker.HasChanges()))
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var participant in Participants())
            {
                await participant.BeforeCommitAsync(Connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await EndTransactionAsync().ConfigureAwait(false);
        foreach (var participant in Participants())
        {
            await participant.AfterCommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task RollbackAsync(CancellationToken cancellationToken)
    {
        var transaction = _transaction;
        if (transaction is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await EndTransactionAsync().ConfigureAwait(false);
            foreach (var context in _contexts)
            {
                context.ChangeTracker.Clear();
            }
        }

        foreach (var participant in Participants())
        {
            await participant.AfterRollbackAsync(this, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EndTransactionAsync()
    {
        foreach (var context in _contexts)
        {
            await context.Database.UseTransactionAsync(null).ConfigureAwait(false);
        }

        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }

    private IEnumerable<ITransactionParticipant> Participants() =>
        _services.GetServices<ITransactionParticipant>().OrderBy(p => p.Order);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_transaction is not null)
        {
            await _transaction.RollbackAsync().ConfigureAwait(false);
            await EndTransactionAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _disposed = true;
    }
}

/// <summary>A handle on the scope transaction. The owner commits; a joined handle's commit is a no-op.</summary>
public sealed class SessionTransaction : IAsyncDisposable
{
    private readonly DbSession _session;
    private bool _completed;

    internal SessionTransaction(DbSession session, bool owner)
    {
        _session = session;
        IsOwner = owner;
    }

    /// <summary>True when this handle opened the transaction.</summary>
    public bool IsOwner { get; }

    /// <summary>The connection.</summary>
    public NpgsqlConnection Connection => _session.Connection;

    /// <summary>The transaction.</summary>
    public NpgsqlTransaction Transaction => _session.Transaction ?? throw new InvalidOperationException("The transaction has ended.");

    /// <summary>Saves tracked changes, runs the participants (outbox, audit) and commits — only when this handle owns the transaction.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The transaction handle is already completed.");
        }

        _completed = true;
        if (IsOwner)
        {
            await _session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Rolls back (owner only; a joined handle leaves the decision to the owner).</summary>
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        if (IsOwner)
        {
            await _session.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
