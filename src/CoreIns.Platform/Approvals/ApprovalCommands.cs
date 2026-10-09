using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Platform.Approvals;

/// <summary>Error names of the maker-checker service (PLT-ERR-*, declared in contracts/openapi/plt.yaml).</summary>
internal static class ApprovalErrors
{
    public const string SelfApproval = "SELF-APPROVAL";
    public const string EditorCannotApprove = "EDITOR-CANNOT-APPROVE";
    public const string CheckerMustBeHuman = "CHECKER-MUST-BE-HUMAN";
    public const string Stale = "APPROVAL-STALE";
    public const string HashMismatch = "APPROVAL-HASH-MISMATCH";
    public const string SubjectMismatch = "APPROVAL-SUBJECT-MISMATCH";
    public const string OwnerDecided = "OWNER-DECIDED";

    public static DomainError Of(string name, string detail) => DomainError.Of(ModuleCode.PLT, name, detail);
}

/// <summary>
/// <c>plt.Approval.request</c> (REQ-PLT-004, -114, -117): the calling actor (the maker) submits a subject bound to its
/// content hash, naming the authority a checker must hold and the role whose inbox receives it.
/// </summary>
internal sealed record RequestApproval(ApprovalRequestRequest Request) : ICommand<ApprovalRequestResponse>;

internal sealed class RequestApprovalValidator : AbstractValidator<RequestApproval>
{
    public RequestApprovalValidator(IAuthorityTypeRegistry registry)
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Type).NotEmpty().MaximumLength(128).When(c => c.Request is not null);
        RuleFor(c => c.Request.ReferralRole).NotEmpty().MaximumLength(128).When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).MaximumLength(1024).When(c => c.Request is not null);
        RuleFor(c => c.Request.Authority).NotNull().When(c => c.Request is not null);
        RuleFor(c => c.Request.Authority.Type).NotEmpty().When(c => c.Request?.Authority is not null);
        RuleFor(c => c.Request.Authority)
            .Must(a => ApprovalDimensions.Problem(registry, a) is null)
            .WithErrorCode("AUTHORITY-DIMENSIONS")
            .WithMessage(c => ApprovalDimensions.Problem(registry, c.Request.Authority) ?? string.Empty)
            .When(c => c.Request?.Authority is { Type.Length: > 0 } a && AuthorityTypeCode.TryParse(a.Type, out var t) && registry.TryGet(t, out _));
        RuleFor(c => c.Request.Diff)
            .Must(d => d is null || d.Value.ValueKind == JsonValueKind.Object)
            .WithMessage("diff must be a JSON object.")
            .When(c => c.Request is not null);
    }
}

/// <summary>Turns the contract's <see cref="ApprovalAuthority"/> into <see cref="IAuthorityService"/> dimensions.</summary>
internal static class ApprovalDimensions
{
    /// <summary>The money dimension's name in every registered type that has one (PRD-14 AuthorityType dimensions).</summary>
    public const string Amount = "amount";

    /// <summary>Null when the authority matches its registered type's dimensions, otherwise the problem.</summary>
    public static string? Problem(IAuthorityTypeRegistry registry, ApprovalAuthority authority)
    {
        if (!AuthorityTypeCode.TryParse(authority.Type, out var code) || !registry.TryGet(code, out var definition))
        {
            return $"Authority type {authority.Type} is not registered.";
        }

        if (authority.Amount is not null && !definition.Dimensions.Any(d => d.Name == Amount && d.Kind == DimensionKind.Money))
        {
            return $"Authority type {authority.Type} has no money dimension '{Amount}'.";
        }

        foreach (var name in (authority.Codes ?? new Dictionary<string, string>()).Keys)
        {
            if (!definition.Dimensions.Any(d => d.Name == name && d.Kind == DimensionKind.Code))
            {
                return $"Authority type {authority.Type} has no code dimension '{name}'.";
            }
        }

        return null;
    }

    public static Dictionary<string, DimensionValue> Of(Money? amount, IReadOnlyDictionary<string, string> codes)
    {
        var dimensions = new Dictionary<string, DimensionValue>(StringComparer.Ordinal);
        if (amount is { } money)
        {
            dimensions[Amount] = DimensionValue.Of(money);
        }

        foreach (var (name, value) in codes)
        {
            dimensions[name] = DimensionValue.OfCodes(value);
        }

        return dimensions;
    }
}

/// <summary>
/// Creates the request in the caller's transaction. A pending request of the same type for the same subject is
/// superseded (Withdrawn, <c>ApprovalDecided</c> WITHDRAWN): its maker and editors become editors of the new one and may
/// not decide it (REQ-PLT-115). Re-submitting the same content hash returns the pending request unchanged.
/// </summary>
internal sealed class RequestApprovalHandler(
    DbSession session, RequestContext context, IClock clock, IEventPublisher events, IAuthorityTypeRegistry registry)
    : ICommandHandler<RequestApproval, ApprovalRequestResponse>
{
    public async Task<Result<ApprovalRequestResponse>> HandleAsync(RequestApproval command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (!AuthorityTypeCode.TryParse(request.Authority.Type, out var authorityType) || !registry.TryGet(authorityType, out _))
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.UnknownAuthorityType, $"Authority type {request.Authority.Type} is not registered.");
        }

        var legalEntity = context.LegalEntity?.Value ?? throw new InvalidOperationException("The request context has no legal entity.");
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = session.Transaction ?? throw new InvalidOperationException("plt.Approval.request runs inside the command transaction.");
        var now = clock.Now;
        var maker = context.Actor;

        var editors = new List<string>();
        var existing = await ApprovalStore.LockPendingAsync(connection, transaction, legalEntity, request.Type, request.ObjectRef, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.PayloadHash == request.PayloadHash)
            {
                return new ApprovalRequestResponse { Request = existing.ToView() };
            }

            if (!await ApprovalStore.TryWithdrawAsync(connection, transaction, existing.RequestId, existing.Version, now, cancellationToken).ConfigureAwait(false))
            {
                return ApprovalErrors.Of(ApprovalErrors.Stale, "The subject's pending request changed meanwhile; retry.");
            }

            ApprovalEvents.PublishDecided(events, existing, ApprovalStore.Withdrawn, null, now);
            editors.AddRange(existing.Editors);
            editors.AddRange(existing.MakerKeys);
        }

        var row = new ApprovalRow
        {
            RequestId = ApprovalRequestId.New().Value,
            LegalEntity = legalEntity,
            ApprovalType = request.Type,
            ObjectRef = request.ObjectRef,
            PayloadHash = request.PayloadHash,
            Status = ApprovalStatus.PendingApproval,
            Maker = maker,
            MakerOnBehalfOf = context.OnBehalfOf,
            Editors = [.. editors.Where(e => e != ApprovalStore.ActorKey(maker)).Distinct(StringComparer.Ordinal)],
            AuthorityType = authorityType.Value,
            AuthorityAmount = request.Authority.Amount,
            AuthorityCodes = request.Authority.Codes ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ReferralRole = request.ReferralRole,
            Reason = request.Reason,
            DiffJson = request.Diff?.GetRawText(),
            Supersedes = existing?.RequestId,
            RequestedAt = now,
            Version = 1,
        };
        // A STALE result here (lost race for the subject) can leave the withdrawal of the earlier request and its
        // ApprovalDecided event staged in the caller's transaction: an in-process caller that gets PLT-ERR-APPROVAL-STALE
        // must roll its transaction back (or call RequestAsync inside a savepoint it rolls back to), never commit.
        if (!await ApprovalStore.TryInsertAsync(connection, transaction, row, cancellationToken).ConfigureAwait(false))
        {
            return ApprovalErrors.Of(ApprovalErrors.Stale, "Another approval request for the subject was created meanwhile; retry.");
        }

        return new ApprovalRequestResponse { Request = row.ToView() };
    }
}

internal sealed class RequestApprovalAuditor : ICommandAuditor<RequestApproval, ApprovalRequestResponse>
{
    public CommandAuditFacts Describe(RequestApproval command, Result<ApprovalRequestResponse>? result) => result is { IsSuccess: true } ok
        ? new CommandAuditFacts
        {
            ObjectRef = ObjectRef.For(ModuleCode.PLT, ApprovalEvents.AggregateType, new ApprovalRequestId(ok.Value.Request.RequestId)),
            Changes =
            [
                new AuditChange("status", null, JsonValue.Create(nameof(ApprovalStatus.PendingApproval))),
                new AuditChange("subject", null, JsonValue.Create(command.Request.ObjectRef.ToString())),
                new AuditChange("payloadHash", null, JsonValue.Create(command.Request.PayloadHash.Value)),
            ],
            BusinessKeys = BusinessKeys.Empty.With("requestId", ok.Value.Request.RequestId.ToString()),
        }
        : new CommandAuditFacts { ObjectRef = command.Request?.ObjectRef };
}

/// <summary>
/// <c>plt.Approval.decide</c> (REQ-PLT-114, -115, -117, -121): the calling actor (the checker) approves or rejects the
/// content it saw. Checks in order: human checker; request exists in the caller's legal entity; still pending with
/// the hash the checker saw (else STALE); checker is not the maker, the maker's principal or an editor; checker holds
/// the request's authority (<see cref="IAuthorityService"/>: deny → AUTHORITY-DENIED, refer → AUTHORITY-REFERRAL-REQUIRED);
/// then a conditional update decides exactly once and <c>ApprovalDecided</c> is staged in the same transaction.
/// </summary>
internal sealed record DecideApproval(ApprovalDecideRequest Request) : ICommand<ApprovalDecideResponse>;

internal sealed class DecideApprovalValidator : AbstractValidator<DecideApproval>
{
    public DecideApprovalValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.RequestId).NotEmpty().When(c => c.Request is not null);
        RuleFor(c => c.Request.Comment).MaximumLength(1024).When(c => c.Request is not null);
        RuleFor(c => c.Request.Comment)
            .Must(comment => !string.IsNullOrWhiteSpace(comment))
            .WithErrorCode("COMMENT-REQUIRED")
            .WithMessage("A rejection needs a comment (REQ-PLT-114).")
            .When(c => c.Request is { Decision: ApprovalDecideRequest.DecisionValue.Reject });
    }
}

internal sealed class DecideApprovalHandler(
    DbSession session, RequestContext context, IClock clock, IEventPublisher events, IAuthorityService authority)
    : ICommandHandler<DecideApproval, ApprovalDecideResponse>
{
    public async Task<Result<ApprovalDecideResponse>> HandleAsync(DecideApproval command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var checker = context.Actor;
        if (checker.Kind != ActorKind.User)
        {
            return ApprovalErrors.Of(ApprovalErrors.CheckerMustBeHuman, "Only a person can decide an approval request (REQ-PLT-121).");
        }

        var legalEntity = context.LegalEntity?.Value ?? throw new InvalidOperationException("The request context has no legal entity.");
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = session.Transaction ?? throw new InvalidOperationException("plt.Approval.decide runs inside the command transaction.");
        var row = await ApprovalStore.FindAsync(connection, transaction, legalEntity, request.RequestId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, "The approval request does not exist.");
        }

        if (row.Status != ApprovalStatus.PendingApproval)
        {
            return ApprovalErrors.Of(ApprovalErrors.Stale, $"The approval request is {row.Status}; reload it.");
        }

        if (row.PayloadHash != request.PayloadHash)
        {
            return ApprovalErrors.Of(ApprovalErrors.Stale, "The content changed after you reviewed it; reload the request and review the new content.");
        }

        // The checker, or the person the checker acts for, must be neither the maker nor the maker's principal (an AI
        // agent's or service's delegating person): four eyes across delegation (REQ-PLT-115, SL2-PLT review D2).
        var checkerKeys = context.OnBehalfOf is { } checkerPrincipal
            ? new[] { ApprovalStore.ActorKey(checker), ApprovalStore.ActorKey(checkerPrincipal) }
            : [ApprovalStore.ActorKey(checker)];
        if (checkerKeys.Any(k => row.MakerKeys.Contains(k, StringComparer.Ordinal)))
        {
            return ApprovalErrors.Of(ApprovalErrors.SelfApproval, "The maker cannot decide their own request (REQ-PLT-115).");
        }

        if (checkerKeys.Any(k => row.Editors.Contains(k, StringComparer.Ordinal)))
        {
            return ApprovalErrors.Of(ApprovalErrors.EditorCannotApprove, "An editor of the content cannot decide it (REQ-PLT-115).");
        }

        var now = clock.Now;
        var check = await authority.CheckAsync(
            new Authority.AuthorityCheckRequest(
                checker,
                context.Roles,
                AuthorityTypeCode.Parse(row.AuthorityType),
                ApprovalDimensions.Of(row.AuthorityAmount, row.AuthorityCodes),
                row.ObjectRef,
                now),
            cancellationToken).ConfigureAwait(false);
        context.AuthorityChecks.Add(check);
        if (check.Decision != AuthorityDecision.Allow)
        {
            var name = check.Decision == AuthorityDecision.Deny ? PlatformErrors.AuthorityDenied : PlatformErrors.AuthorityReferral;
            return DomainError.Of(ModuleCode.PLT, name, $"{row.AuthorityType}: {check.ReasonCode}") with
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["authorityCheckId"] = check.CheckId.ToString(),
                    ["reasonCode"] = check.ReasonCode,
                    ["referralTargets"] = string.Join(",", check.ReferralTargets.Select(t => $"{t.Kind}:{t.Id}")),
                },
            };
        }

        var decision = request.Decision == ApprovalDecideRequest.DecisionValue.Approve ? ApprovalStore.Approved : ApprovalStore.Rejected;
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment;
        if (!await ApprovalStore.TryDecideAsync(
                connection, transaction, row.RequestId, row.Version, request.PayloadHash, decision, checker, comment, check.CheckId.Value, now, cancellationToken)
            .ConfigureAwait(false))
        {
            return ApprovalErrors.Of(ApprovalErrors.Stale, "The approval request was decided or changed meanwhile; reload it.");
        }

        var decided = row with
        {
            Status = decision == ApprovalStore.Approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected,
            Decision = decision,
            Checker = checker,
            Comment = comment,
            AuthorityCheckId = check.CheckId.Value,
            DecidedAt = now,
            Version = row.Version + 1,
        };
        ApprovalEvents.PublishDecided(events, decided, decision, checker, now);
        return new ApprovalDecideResponse { Request = decided.ToView(), Decision = decided.ToDecision()! };
    }
}

internal sealed class DecideApprovalAuditor : ICommandAuditor<DecideApproval, ApprovalDecideResponse>
{
    public CommandAuditFacts Describe(DecideApproval command, Result<ApprovalDecideResponse>? result) => new()
    {
        ObjectRef = command.Request is null ? null : ObjectRef.For(ModuleCode.PLT, ApprovalEvents.AggregateType, new ApprovalRequestId(command.Request.RequestId)),
        Changes = result is { IsSuccess: true } ok
            ?
            [
                new AuditChange("status", JsonValue.Create(nameof(ApprovalStatus.PendingApproval)), JsonValue.Create(ok.Value.Request.Status.ToString())),
                new AuditChange("subject", null, JsonValue.Create(ok.Value.Request.ObjectRef.ToString())),
                new AuditChange("payloadHash", null, JsonValue.Create(ok.Value.Request.PayloadHash.Value)),
            ]
            : [],
        BusinessKeys = command.Request is null ? BusinessKeys.Empty : BusinessKeys.Empty.With("requestId", command.Request.RequestId.ToString()),
    };
}

/// <summary>The <c>ApprovalDecided</c> event (PRD-14 §8.1, REQ-PLT-116), staged in the outbox of the deciding transaction.</summary>
internal static class ApprovalEvents
{
    public const string AggregateType = "ApprovalRequest";

    public static void PublishDecided(IEventPublisher events, ApprovalRow row, string decision, ActorRef? checker, Instant decidedAt) =>
        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ApprovalDecidedV1.Descriptor),
            AggregateType,
            row.RequestId.ToString(),
            new ApprovalDecidedV1
            {
                RequestId = row.RequestId,
                Decision = decision.ToUpperInvariant(),

                // checkerUserId is the person's directory object id (Entra oid); other actor ids are not user ids.
                CheckerUserId = checker is { Kind: ActorKind.User } c && Guid.TryParse(c.Id, out var oid) && oid != Guid.Empty ? UserId.From(oid) : null,
                DecidedAt = decidedAt,
                ApprovalType = row.ApprovalType,
                ObjectRef = row.ObjectRef,
                PayloadHash = row.PayloadHash,
            },
            BusinessKeys.Empty.With("requestId", row.RequestId.ToString())));
}

/// <summary>Reads of approval requests: <c>plt.Approval.get</c>, the inbox (<c>plt.Approval.list</c>) and <c>verifyForExecution</c>.</summary>
internal sealed class ApprovalQueries(DbSession session, RequestContext context)
{
    public async Task<Result<ApprovalGetResponse>> GetAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var row = await FindAsync(requestId, cancellationToken).ConfigureAwait(false);
        return row is null
            ? DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, "The approval request does not exist.")
            : new ApprovalGetResponse { Request = row.ToView(), Decision = row.ToDecision() };
    }

    public async Task<Result<ApprovalListPage>> ListAsync(ApprovalStatus? status, string? role, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (DecodeCursor(cursor) is not { } offset || limit is < 1 or > 200)
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "cursor is malformed or limit is outside 1..200.");
        }

        var roles = role is null ? context.Roles : context.Roles.Where(r => string.Equals(r, role, StringComparison.Ordinal)).ToList();
        var size = limit ?? 25;
        IReadOnlyList<ApprovalRow> rows = [];
        if (roles.Count > 0)
        {
            var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            rows = await ApprovalStore.ListAsync(
                connection, session.Transaction, LegalEntity, ApprovalStore.StatusCode(status ?? ApprovalStatus.PendingApproval), roles, context.Actor, offset, size,
                cancellationToken).ConfigureAwait(false);
        }

        return new ApprovalListPage
        {
            Items = [.. rows.Take(size).Select(r => new ApprovalListItem { Request = r.ToView(), Decision = r.ToDecision() })],
            NextCursor = rows.Count > size ? EncodeCursor(offset + size) : null,
            Limit = size,
        };
    }

    public async Task<ApprovalVerifyForExecutionResponse> VerifyForExecutionAsync(ApprovalVerifyForExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = await FindAsync(request.RequestId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, "The approval request does not exist."));
        if (!string.Equals(row.ApprovalType, request.Type, StringComparison.Ordinal) || row.ObjectRef != request.ObjectRef)
        {
            throw new DomainException(ApprovalErrors.Of(
                ApprovalErrors.SubjectMismatch, "The approval request is of another approval type or for another subject than the one to execute."));
        }

        if (row.PayloadHash != request.Hash)
        {
            throw new DomainException(ApprovalErrors.Of(ApprovalErrors.HashMismatch, "The approved content hash differs from the content to execute (REQ-PLT-117)."));
        }

        // The executing module also compares Authority (type, amount, codes) with the dimensions it computes itself.
        return new ApprovalVerifyForExecutionResponse { Ok = row.Status == ApprovalStatus.Approved, Status = row.Status, Authority = row.ToView().Authority };
    }

    private string LegalEntity => context.LegalEntity?.Value ?? throw new InvalidOperationException("The request context has no legal entity.");

    private async Task<ApprovalRow?> FindAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ApprovalStore.FindAsync(connection, session.Transaction, LegalEntity, requestId, cancellationToken).ConfigureAwait(false);
    }

    private static int? DecodeCursor(string? cursor) =>
        cursor is null ? 0
        : int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset) ? offset
        : null;

    private static string EncodeCursor(int offset) => offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The in-process contract <see cref="IPlatformApprovalService"/> (D-ARC-16). Owning modules call
/// <see cref="RequestAsync"/> inside their own transaction (the scope's <see cref="DbSession"/>), so the request
/// commits or rolls back with their change; failures throw <see cref="DomainException"/> with the PLT-ERR code.
/// </summary>
internal sealed class PlatformApprovalService(
    RequestContext context,
    ICommandHandler<RequestApproval, ApprovalRequestResponse> request,
    ICommandHandler<DecideApproval, ApprovalDecideResponse> decide,
    ICommandHandler<WithdrawApproval, ApprovalWithdrawResponse> withdraw,
    ApprovalQueries queries) : IPlatformApprovalService
{
    public async Task<ApprovalRequestResponse> RequestAsync(ApprovalRequestRequest request1, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await request.HandleAsync(new RequestApproval(request1), cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<ApprovalDecideResponse> DecideAsync(ApprovalDecideRequest request1, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await decide.HandleAsync(new DecideApproval(request1), cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<ApprovalGetResponse> GetAsync(string id, CancellationToken cancellationToken = default) =>
        Guid.TryParse(id, out var requestId)
            ? Unwrap(await queries.GetAsync(requestId, cancellationToken).ConfigureAwait(false))
            : throw new DomainException(DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, "The approval request does not exist."));

    public Task<ApprovalVerifyForExecutionResponse> VerifyForExecutionAsync(ApprovalVerifyForExecutionRequest request1, CancellationToken cancellationToken = default) =>
        queries.VerifyForExecutionAsync(request1, cancellationToken);

    public async Task<ApprovalWithdrawResponse> WithdrawAsync(ApprovalWithdrawRequest request1, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Capture the outer registered owner before the nested PLT command stamps its own identity.
        var callerModule = context.CurrentCommandModule;
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await withdraw.HandleAsync(new WithdrawApproval(request1, callerModule), cancellationToken).ConfigureAwait(false));
        }
    }

    private static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new DomainException(result.Error);
}
