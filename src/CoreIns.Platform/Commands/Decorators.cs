using System.Text.Json;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Idempotency;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Platform.Commands;

/// <summary>
/// Validates the command with every registered FluentValidation validator (ADR §1 validation at the boundary);
/// failures become <c>&lt;MOD&gt;-ERR-VALIDATION</c> with field errors, before any transaction starts.
/// </summary>
internal sealed class ValidationDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner, IEnumerable<IValidator<TCommand>> validators, CommandDescriptor descriptor)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        var failures = new List<FieldError>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(command, cancellationToken).ConfigureAwait(false);
            failures.AddRange(result.Errors.Select(e => new FieldError(e.PropertyName, e.ErrorCode, e.ErrorCode, e.ErrorMessage)));
        }

        if (failures.Count > 0)
        {
            return new DomainError(ErrorCode.For(descriptor.Module, PlatformErrors.Validation), "The command is not valid.") { FieldErrors = failures };
        }

        return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Runs the command in the scope's unit of work: begins (or joins) the transaction, commits on success — which saves
/// every module DbContext, writes the staged outbox rows and audit records, then commits — and rolls back on failure
/// or exception. A dry run computes the full result and rolls back (contract §3.5.3).
/// </summary>
internal sealed class TransactionDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner, DbSession session, RequestContext context, CommandDescriptor descriptor)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        if (context.DryRun && !descriptor.SupportsDryRun)
        {
            return new DomainError(ErrorCode.For(descriptor.Module, PlatformErrors.Validation), $"{descriptor.Operation} does not support dry-run.");
        }

        var transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess && !context.DryRun)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }
}

/// <summary>
/// Command-level idempotency inside the transaction (contract §3.5.3, ADR §2 rule 6): the first execution with a key
/// inserts <c>plt.idempotency_record</c> (scope <c>cmd:&lt;operation&gt;:&lt;actor&gt;</c>) and stores its result in the
/// same transaction as its effects; a retry with the same key and the same command returns the stored result without
/// executing; a different command fails with <c>&lt;MOD&gt;-ERR-IDEMPOTENCY-MISMATCH</c>. A concurrent duplicate waits on
/// the key's unique index until the first commits. Failed executions are not stored (the key can be retried).
/// </summary>
internal sealed class IdempotencyDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner, DbSession session, RequestContext context, IClock clock, CommandDescriptor descriptor)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        if (context.DryRun)
        {
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (context.IdempotencyKey is not { } key)
        {
            return descriptor.RequiresIdempotencyKey
                ? new DomainError(ErrorCode.For(descriptor.Module, PlatformErrors.IdempotencyKeyRequired), $"{descriptor.Operation} needs an Idempotency-Key.")
                : await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        }

        var transaction = session.Transaction ?? throw new InvalidOperationException("The idempotency decorator runs inside the transaction decorator.");
        var scope = $"cmd:{descriptor.Operation}:{context.Actor}";
        var hash = CanonicalJson.HashOf(new { command = typeof(TCommand).FullName, body = command });
        var now = clock.Now;

        if (!await IdempotencyStore.TryBeginAsync(session.Connection, transaction, scope, key, hash, now, cancellationToken).ConfigureAwait(false))
        {
            var existing = await IdempotencyStore.FindAsync(session.Connection, transaction, scope, key, now, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return new DomainError(ErrorCode.For(descriptor.Module, PlatformErrors.IdempotencyInProgress), "The key's record expired; retry.");
            }

            if (!string.Equals(existing.RequestHash, hash.Value, StringComparison.Ordinal))
            {
                return new DomainError(
                    ErrorCode.For(descriptor.Module, PlatformErrors.IdempotencyMismatch),
                    $"Idempotency-Key {key} was used with a different {descriptor.Operation} request.");
            }

            if (!existing.Completed || existing.Body is null)
            {
                return new DomainError(ErrorCode.For(descriptor.Module, PlatformErrors.IdempotencyInProgress), "The original request is still in progress.");
            }

            return Result.Success(JsonSerializer.Deserialize<TResult>(existing.Body, SharedKernelJson.Options)!);
        }

        var result = await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(result.Value, SharedKernelJson.Options);
            await IdempotencyStore.CompleteAsync(session.Connection, transaction, scope, key, 200, "application/json", body, now, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }
}

/// <summary>
/// Audits every execution (ADR §2 rule 9, D-ARC-15): a success is recorded in the same transaction as its effects; a
/// rejection or failure is recorded even though the transaction rolls back. Dry runs are not audited.
/// </summary>
internal sealed class AuditDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IAuditWriter audit,
    RequestContext context,
    IClock clock,
    CommandDescriptor descriptor,
    IEnumerable<ICommandAuditor<TCommand, TResult>> auditors)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        if (!descriptor.Audited || context.DryRun)
        {
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        }

        var occurredAt = clock.Now;
        Result<TResult> result;
        try
        {
            result = await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.AppendRegardlessOfOutcome(Record(command, null, AuditOutcome.Failed, ErrorCodeOf(ex), occurredAt));
            throw;
        }

        if (result.IsSuccess)
        {
            audit.Append(Record(command, result, AuditOutcome.Succeeded, null, occurredAt));
        }
        else
        {
            audit.AppendRegardlessOfOutcome(Record(command, null, AuditOutcome.Rejected, result.Error.Code.Value, occurredAt));
        }

        return result;
    }

    private AuditRecord Record(TCommand command, Result<TResult>? result, AuditOutcome outcome, string? errorCode, Instant occurredAt)
    {
        var facts = auditors.Select(a => a.Describe(command, result)).FirstOrDefault() ?? new CommandAuditFacts();
        var check = context.AuthorityChecks.LastOrDefault(c => c.Decision == AuthorityDecision.Allow) ?? context.AuthorityChecks.LastOrDefault();
        return new AuditRecord
        {
            Actor = context.Actor,
            OnBehalfOf = context.OnBehalfOf,
            Roles = [.. context.Roles],
            AuthorityCheckId = check?.CheckId,
            AuthorityUsed = check is null ? null : $"{check.Type}:{check.Decision}:{check.SourceGrant}",
            Operation = descriptor.Operation,
            Outcome = outcome,
            ErrorCode = errorCode,
            ObjectRef = facts.ObjectRef,
            ObjectNumber = facts.ObjectNumber,
            Changes = facts.Changes,
            Reason = context.Reason,
            Channel = context.Channel,
            CorrelationId = context.CorrelationId,
            CausationId = context.CausationId,
            AiInteractionId = context.AiInteractionId,
            BusinessKeys = facts.BusinessKeys,
            Origin = context.Origin,
            LegalEntity = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."),
            Jurisdiction = context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction."),
            OccurredAt = occurredAt,
        };
    }

    private static string ErrorCodeOf(Exception exception) =>
        CoreInsExceptionHandler.ToError(exception, ModuleCode.PLT).Code.Value;
}

/// <summary>
/// The authority hook (CD-05): asks <see cref="IAuthorityService"/> for each requirement the command declares, records
/// the checks in the request context (the audit record names the one used), and refuses on deny
/// (<c>&lt;MOD&gt;-ERR-AUTHORITY-DENIED</c>) or refer (<c>&lt;MOD&gt;-ERR-AUTHORITY-REFERRAL-REQUIRED</c>, with the referral
/// targets and check id in the error metadata so the module can open a maker-checker request).
/// </summary>
internal sealed class AuthorityDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<ICommandAuthorization<TCommand>> authorizations,
    IAuthorityService authority,
    RequestContext context,
    IClock clock,
    CommandDescriptor descriptor)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        foreach (var requirement in authorizations.SelectMany(a => a.Requirements(command)))
        {
            var check = await authority.CheckAsync(
                new AuthorityCheckRequest(
                    context.Actor, context.Roles, requirement.Type, requirement.Dimensions, requirement.ObjectRef, requirement.ValidAt ?? clock.Now),
                cancellationToken).ConfigureAwait(false);
            context.AuthorityChecks.Add(check);

            if (check.Decision == AuthorityDecision.Deny)
            {
                return new DomainError(ErrorCode.For(descriptor.Module, PlatformErrors.AuthorityDenied), $"{requirement.Type}: {check.ReasonCode}")
                {
                    Metadata = Metadata(check),
                };
            }

            if (check.Decision == AuthorityDecision.Refer)
            {
                return new DomainError(ErrorCode.For(descriptor.Module, PlatformErrors.AuthorityReferral), $"{requirement.Type}: {check.ReasonCode}")
                {
                    Metadata = Metadata(check),
                };
            }
        }

        return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> Metadata(AuthorityCheckResult check) => new(StringComparer.Ordinal)
    {
        ["authorityCheckId"] = check.CheckId.ToString(),
        ["reasonCode"] = check.ReasonCode,
        ["referralTargets"] = string.Join(",", check.ReferralTargets.Select(t => $"{t.Kind}:{t.Id}")),
    };
}
