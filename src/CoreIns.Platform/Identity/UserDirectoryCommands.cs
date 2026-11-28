using System.Text.Json.Nodes;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authorization;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Platform.Identity;

/// <summary>Error names of the user directory (PLT-ERR-*).</summary>
internal static class UserErrors
{
    public const string Exists = "USER-EXISTS";
    public const string Suspended = "USER-SUSPENDED";
    public const string Disabled = "USER-DISABLED";
    public const string UnknownRole = "UNKNOWN-ROLE";
    public const string AssignmentExists = "ASSIGNMENT-EXISTS";
    public const string Sod = "SOD";
    public const string InvalidTransition = "INVALID-TRANSITION";
    public const string ApprovalPending = "APPROVAL-STALE";

    public static DomainError Of(string name, string detail) => DomainError.Of(ModuleCode.PLT, name, detail);

    public static DomainError NotFound(string what) => DomainError.Of(ModuleCode.PLT, PlatformErrors.NotFound, $"The {what} does not exist.");
}

/// <summary>Shared pieces of the user directory commands.</summary>
internal static class UserDirectory
{
    /// <summary>The approval type and authority type of a role grant (second approver, REQ-PLT-076).</summary>
    public const string RoleGrantType = "PLT.ROLE_GRANT";

    public static string LegalEntityOf(RequestContext context) =>
        context.LegalEntity?.Value ?? throw new InvalidOperationException("The request context has no legal entity.");

    public static ObjectRef UserRef(Guid userId) => ObjectRef.For(ModuleCode.PLT, "User", UserId.From(userId));

    /// <summary>The approval subject of "this role for this user in this scope": stable across the request and the execution.</summary>
    public static ObjectRef GrantSubject(Guid userId, string role, string? scope) =>
        new(ModuleCode.PLT, "RoleAssignment", CanonicalJson.Hash(new JsonObject { ["u"] = userId.ToString("D"), ["r"] = role, ["s"] = scope }).Value[..40]);

    /// <summary>The content the second approver sees and the executor re-checks: everything that changes what the grant does.</summary>
    public static Sha256Hash GrantHash(Guid userId, string role, string? scope, Instant? validFrom, Instant? validTo) => CanonicalJson.Hash(new JsonObject
    {
        ["userId"] = userId.ToString("D"),
        ["role"] = role,
        ["scope"] = scope,
        ["validFrom"] = validFrom?.ToString(),
        ["validTo"] = validTo?.ToString(),
    });

    public static async Task<Result<UserRow>> LoadAsync(DbSession session, RequestContext context, Guid userId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await UserDirectoryStore.FindAsync(connection, session.Transaction, LegalEntityOf(context), userId, cancellationToken).ConfigureAwait(false);
        return row is null ? UserErrors.NotFound("user") : row;
    }

    public static CommandAuditFacts Facts(Guid? userId, params AuditChange[] changes) => new()
    {
        ObjectRef = userId is { } id ? UserRef(id) : null,
        Changes = changes,
        BusinessKeys = userId is { } key ? BusinessKeys.Empty.With("userId", key.ToString("D")) : BusinessKeys.Empty,
    };

    public static AuditChange Change(string field, string? before, string? after) =>
        new(field, before is null ? null : JsonValue.Create(before), after is null ? null : JsonValue.Create(after));
}

// ---- plt.User.create ---------------------------------------------------------------------------------------------

internal sealed record CreateUser(UserCreateRequest Request) : ICommand<UserCreateResponse>;

internal sealed class CreateUserValidator : AbstractValidator<CreateUser>
{
    public CreateUserValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.EntraOid).NotEmpty().MaximumLength(128).When(c => c.Request is not null);
        RuleFor(c => c.Request.DisplayName).NotEmpty().MaximumLength(200).When(c => c.Request is not null);
        RuleFor(c => c.Request.Email).MaximumLength(320).When(c => c.Request is not null);
    }
}

internal sealed class CreateUserHandler(DbSession session, RequestContext context, IClock clock) : ICommandHandler<CreateUser, UserCreateResponse>
{
    public async Task<Result<UserCreateResponse>> HandleAsync(CreateUser command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.Now;
        var row = new UserRow
        {
            UserId = UserId.New().Value,
            LegalEntity = UserDirectory.LegalEntityOf(context),
            EntraOid = request.EntraOid.Trim(),
            DisplayName = request.DisplayName.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Language = UserDirectoryStore.LanguageCode(request.Language ?? UserLanguage.El),
            Status = UserDirectoryStore.Active,
            CreatedBy = context.Actor.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await UserDirectoryStore.TryInsertAsync(connection, session.Transaction, row, cancellationToken).ConfigureAwait(false)
            ? new UserCreateResponse { User = row.ToView([]) }
            : UserErrors.Of(UserErrors.Exists, "A user entry for this Entra identity already exists.");
    }
}

internal sealed class CreateUserAuditor : ICommandAuditor<CreateUser, UserCreateResponse>
{
    public CommandAuditFacts Describe(CreateUser command, Result<UserCreateResponse>? result) => result is { IsSuccess: true } ok
        ? UserDirectory.Facts(ok.Value.User.UserId.Value, UserDirectory.Change("status", null, UserDirectoryStore.Active), UserDirectory.Change("entraOid", null, ok.Value.User.EntraOid))
        : UserDirectory.Facts(null);
}

// ---- plt.User.update ---------------------------------------------------------------------------------------------

internal sealed record UpdateUser(Guid UserId, UserUpdateRequest Request) : ICommand<UserUpdateResponse>;

internal sealed class UpdateUserValidator : AbstractValidator<UpdateUser>
{
    public UpdateUserValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.DisplayName).NotEmpty().MaximumLength(200).When(c => c.Request?.DisplayName is not null);
        RuleFor(c => c.Request.Email).MaximumLength(320).When(c => c.Request is not null);
        RuleFor(c => c.Request).Must(r => r.DisplayName is not null || r.Email is not null || r.Language is not null)
            .WithMessage("Nothing to change.").When(c => c.Request is not null);
    }
}

internal sealed class UpdateUserHandler(DbSession session, RequestContext context, IClock clock) : ICommandHandler<UpdateUser, UserUpdateResponse>
{
    public async Task<Result<UserUpdateResponse>> HandleAsync(UpdateUser command, CancellationToken cancellationToken)
    {
        var loaded = await UserDirectory.LoadAsync(session, context, command.UserId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var user = loaded.Value;
        if (user.Status == UserDirectoryStore.Disabled)
        {
            return UserErrors.Of(UserErrors.InvalidTransition, "A disabled user entry is final and cannot be changed.");
        }

        var request = command.Request;
        var email = string.IsNullOrWhiteSpace(request.Email) ? user.Email : request.Email.Trim();
        var updated = user with
        {
            DisplayName = request.DisplayName?.Trim() ?? user.DisplayName,
            Email = email,
            Language = request.Language is { } language ? UserDirectoryStore.LanguageCode(language) : user.Language,
            UpdatedAt = clock.Now,
        };
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await UserDirectoryStore.UpdateProfileAsync(connection, session.Transaction, user.UserId, updated.DisplayName, updated.Email, updated.Language, updated.UpdatedAt, cancellationToken)
            .ConfigureAwait(false);
        return new UserUpdateResponse { User = updated.ToView() };
    }
}

internal sealed class UpdateUserAuditor : ICommandAuditor<UpdateUser, UserUpdateResponse>
{
    public CommandAuditFacts Describe(UpdateUser command, Result<UserUpdateResponse>? result) => result is { IsSuccess: true } ok
        ? UserDirectory.Facts(command.UserId,
            UserDirectory.Change("displayName", null, ok.Value.User.DisplayName), UserDirectory.Change("language", null, ok.Value.User.Language.ToString()))
        : UserDirectory.Facts(command.UserId);
}

// ---- plt.User.suspend / reactivate / disable ---------------------------------------------------------------------

internal sealed record SuspendUser(UserSuspendRequest Request) : ICommand<UserSuspendResponse>;

internal sealed record ReactivateUser(UserReactivateRequest Request) : ICommand<UserReactivateResponse>;

internal sealed record DisableUser(UserDisableRequest Request) : ICommand<UserDisableResponse>;

internal sealed class SuspendUserValidator : AbstractValidator<SuspendUser>
{
    public SuspendUserValidator() => RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024);
}

internal sealed class ReactivateUserValidator : AbstractValidator<ReactivateUser>
{
    public ReactivateUserValidator() => RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024);
}

internal sealed class DisableUserValidator : AbstractValidator<DisableUser>
{
    public DisableUserValidator() => RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024);
}

/// <summary>
/// The status machine of a directory entry: Active ⇄ Suspended, and Active/Suspended → Disabled (final). An administrator never
/// changes their own status (SoD). A suspended or disabled user is refused with 403 on the next request (<see cref="UserProvisioningMiddleware"/>).
/// </summary>
internal sealed class UserStatusChange(DbSession session, RequestContext context, IClock clock)
{
    public async Task<Result<UserView>> ChangeAsync(Guid userId, string[] from, string to, string reason, CancellationToken cancellationToken)
    {
        var loaded = await UserDirectory.LoadAsync(session, context, userId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var user = loaded.Value;
        if (context.Actor.Kind == ActorKind.User && string.Equals(user.EntraOid, context.Actor.Id, StringComparison.Ordinal))
        {
            return UserErrors.Of(UserErrors.Sod, "You cannot change the status of your own user entry.");
        }

        var now = clock.Now;
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!from.Contains(user.Status, StringComparer.Ordinal)
            || !await UserDirectoryStore.TrySetStatusAsync(connection, session.Transaction, userId, from, to, reason, now, cancellationToken).ConfigureAwait(false))
        {
            return UserErrors.Of(UserErrors.InvalidTransition, $"A {user.Status} user cannot become {to}.");
        }

        context.Reason = reason;
        return (user with { Status = to, UpdatedAt = now }).ToView();
    }
}

internal sealed class SuspendUserHandler(UserStatusChange change) : ICommandHandler<SuspendUser, UserSuspendResponse>
{
    public async Task<Result<UserSuspendResponse>> HandleAsync(SuspendUser command, CancellationToken cancellationToken)
    {
        var result = await change.ChangeAsync(command.Request.UserId.Value, [UserDirectoryStore.Active], UserDirectoryStore.Suspended, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? new UserSuspendResponse { User = result.Value } : result.Error;
    }
}

internal sealed class ReactivateUserHandler(UserStatusChange change) : ICommandHandler<ReactivateUser, UserReactivateResponse>
{
    public async Task<Result<UserReactivateResponse>> HandleAsync(ReactivateUser command, CancellationToken cancellationToken)
    {
        var result = await change.ChangeAsync(command.Request.UserId.Value, [UserDirectoryStore.Suspended], UserDirectoryStore.Active, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? new UserReactivateResponse { User = result.Value } : result.Error;
    }
}

internal sealed class DisableUserHandler(UserStatusChange change) : ICommandHandler<DisableUser, UserDisableResponse>
{
    public async Task<Result<UserDisableResponse>> HandleAsync(DisableUser command, CancellationToken cancellationToken)
    {
        var result = await change.ChangeAsync(
            command.Request.UserId.Value, [UserDirectoryStore.Active, UserDirectoryStore.Suspended], UserDirectoryStore.Disabled, command.Request.Reason, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? new UserDisableResponse { User = result.Value } : result.Error;
    }
}

internal sealed class SuspendUserAuditor : ICommandAuditor<SuspendUser, UserSuspendResponse>
{
    public CommandAuditFacts Describe(SuspendUser command, Result<UserSuspendResponse>? result) => UserDirectory.Facts(
        command.Request?.UserId.Value, result is { IsSuccess: true } ? UserDirectory.Change("status", UserDirectoryStore.Active, UserDirectoryStore.Suspended) : UserDirectory.Change("status", null, null));
}

internal sealed class ReactivateUserAuditor : ICommandAuditor<ReactivateUser, UserReactivateResponse>
{
    public CommandAuditFacts Describe(ReactivateUser command, Result<UserReactivateResponse>? result) => UserDirectory.Facts(
        command.Request?.UserId.Value, result is { IsSuccess: true } ? UserDirectory.Change("status", UserDirectoryStore.Suspended, UserDirectoryStore.Active) : UserDirectory.Change("status", null, null));
}

internal sealed class DisableUserAuditor : ICommandAuditor<DisableUser, UserDisableResponse>
{
    public CommandAuditFacts Describe(DisableUser command, Result<UserDisableResponse>? result) => UserDirectory.Facts(
        command.Request?.UserId.Value, result is { IsSuccess: true } ? UserDirectory.Change("status", null, UserDirectoryStore.Disabled) : UserDirectory.Change("status", null, null));
}

// ---- plt.UserProfile.setLanguage ---------------------------------------------------------------------------------

internal sealed record SetUserLanguage(UserProfileSetLanguageRequest Request) : ICommand<UserProfileSetLanguageResponse>;

internal sealed class SetUserLanguageValidator : AbstractValidator<SetUserLanguage>
{
    public SetUserLanguageValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Source).MaximumLength(64).When(c => c.Request is not null);
    }
}

/// <summary>The signed-in user's own language choice. The profile is provisioned on the first call if the sign-in middleware did not yet.</summary>
internal sealed class SetUserLanguageHandler(DbSession session, RequestContext context, IClock clock) : ICommandHandler<SetUserLanguage, UserProfileSetLanguageResponse>
{
    public async Task<Result<UserProfileSetLanguageResponse>> HandleAsync(SetUserLanguage command, CancellationToken cancellationToken)
    {
        if (context.Actor.Kind != ActorKind.User)
        {
            return UserErrors.Of(UserErrors.Sod, "Only a signed-in person has a language preference.");
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var user = await UserDirectoryStore.FindByOidAsync(connection, session.Transaction, UserDirectory.LegalEntityOf(context), context.Actor.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return UserErrors.NotFound("user entry of the signed-in user");
        }

        var language = UserDirectoryStore.LanguageCode(command.Request.UiLanguage);
        await UserDirectoryStore.UpdateProfileAsync(connection, session.Transaction, user.UserId, user.DisplayName, user.Email, language, clock.Now, cancellationToken).ConfigureAwait(false);
        return new UserProfileSetLanguageResponse
        {
            SavedPreferences = new UserPreferences { UiLanguage = command.Request.UiLanguage, Source = command.Request.Source },
        };
    }
}

internal sealed class SetUserLanguageAuditor : ICommandAuditor<SetUserLanguage, UserProfileSetLanguageResponse>
{
    public CommandAuditFacts Describe(SetUserLanguage command, Result<UserProfileSetLanguageResponse>? result) => result is { IsSuccess: true } ok
        ? new CommandAuditFacts { Changes = [UserDirectory.Change("language", null, ok.Value.SavedPreferences.UiLanguage.ToString())] }
        : new CommandAuditFacts();
}

// ---- plt.RoleAssignment.grant / revoke ---------------------------------------------------------------------------

internal sealed record GrantRole(RoleAssignmentGrantRequest Request) : ICommand<RoleAssignmentGrantResponse>;

internal sealed record RevokeRole(RoleAssignmentRevokeRequest Request) : ICommand<RoleAssignmentRevokeResponse>;

internal sealed class GrantRoleValidator : AbstractValidator<GrantRole>
{
    public GrantRoleValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Role).NotEmpty().MaximumLength(128).When(c => c.Request is not null);
        RuleFor(c => c.Request.Scope).MaximumLength(128).When(c => c.Request is not null);
        RuleFor(c => c.Request).Must(r => r.ValidTo is null || r.ValidFrom is null || r.ValidTo.Value.CompareTo(r.ValidFrom.Value) > 0)
            .WithMessage("validTo must be after validFrom.").When(c => c.Request is not null);
    }
}

/// <summary>
/// <c>plt.RoleAssignment.grant</c>: a grant needs a second approver (REQ-PLT-076, BO-PLT-02). The first call (no
/// <c>approvalRequestId</c>) requests approval of exactly this content (<c>PLT.ROLE_GRANT</c>) and returns PendingApproval; a
/// second administrator decides it in the approvals inbox; the same request repeated with the approval id verifies the approval
/// for this content and records the assignment. A user never receives a role from themselves (SoD), and the approval's own
/// rules (maker is not checker) apply on top.
/// Entra app roles in the token stay authoritative: an assignment is recorded and audited, it does not change the roles of the token.
/// TODO(hardening): reconcile assignments with the token roles once the user decides the identity ownership (SURVEY-NOTES D), and
/// sync valid-from/valid-to into an access-review sweeper.
/// </summary>
internal sealed class GrantRoleHandler(
    DbSession session, RequestContext context, IClock clock, IPlatformApprovalService approvals, IOptionsMonitor<PermissionOptions> permissions)
    : ICommandHandler<GrantRole, RoleAssignmentGrantResponse>
{
    public async Task<Result<RoleAssignmentGrantResponse>> HandleAsync(GrantRole command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var loaded = await UserDirectory.LoadAsync(session, context, request.UserId.Value, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var user = loaded.Value;
        if (context.Actor.Kind == ActorKind.User && string.Equals(user.EntraOid, context.Actor.Id, StringComparison.Ordinal))
        {
            return UserErrors.Of(UserErrors.Sod, "You cannot grant a role to yourself.");
        }

        if (user.Status != UserDirectoryStore.Active)
        {
            return UserErrors.Of(UserErrors.InvalidTransition, $"A {user.Status} user cannot receive a role.");
        }

        var role = request.Role.Trim();
        if (!KnownRoles(permissions.CurrentValue).Contains(role))
        {
            return UserErrors.Of(UserErrors.UnknownRole, $"{role} is not an application role.");
        }

        var scope = string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope.Trim();
        var hash = UserDirectory.GrantHash(user.UserId, role, scope, request.ValidFrom, request.ValidTo);
        var subject = UserDirectory.GrantSubject(user.UserId, role, scope);
        try
        {
            return request.ApprovalRequestId is null
                ? await RequestAsync(user, role, scope, request, hash, subject, cancellationToken).ConfigureAwait(false)
                : await ExecuteAsync(user, role, scope, request, request.ApprovalRequestId.Value.Value, hash, subject, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }
    }

    private async Task<Result<RoleAssignmentGrantResponse>> RequestAsync(
        UserRow user, string role, string? scope, RoleAssignmentGrantRequest request, Sha256Hash hash, ObjectRef subject, CancellationToken cancellationToken)
    {
        var approval = await approvals.RequestAsync(
            new ApprovalRequestRequest
            {
                Type = UserDirectory.RoleGrantType,
                ObjectRef = subject,
                PayloadHash = hash,
                Authority = new ApprovalAuthority { Type = UserDirectory.RoleGrantType },
                ReferralRole = PlatformAdminRole,
                Reason = $"Grant {role} to {user.DisplayName}",
                Diff = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    userId = user.UserId, user = user.DisplayName, role, scope, validFrom = request.ValidFrom?.ToString(), validTo = request.ValidTo?.ToString(),
                }),
            },
            CommandOptions.New(), cancellationToken).ConfigureAwait(false);
        return new RoleAssignmentGrantResponse
        {
            Status = RoleAssignmentGrantResponse.StatusValue.PendingApproval,
            ApprovalRequestId = ApprovalRequestId.From(approval.Request.RequestId),
        };
    }

    private async Task<Result<RoleAssignmentGrantResponse>> ExecuteAsync(
        UserRow user, string role, string? scope, RoleAssignmentGrantRequest request, Guid approvalId, Sha256Hash hash, ObjectRef subject, CancellationToken cancellationToken)
    {
        var verified = await approvals.VerifyForExecutionAsync(
            new ApprovalVerifyForExecutionRequest { RequestId = approvalId, Hash = hash, Type = UserDirectory.RoleGrantType, ObjectRef = subject }, cancellationToken).ConfigureAwait(false);
        if (!verified.Ok)
        {
            return UserErrors.Of(UserErrors.ApprovalPending, $"The role grant is {verified.Status}; it needs an approval by a second administrator first.");
        }

        var approval = await approvals.GetAsync(approvalId.ToString("D"), cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var row = new RoleAssignmentRow
        {
            AssignmentId = Guid.CreateVersion7(),
            UserId = user.UserId,
            LegalEntity = user.LegalEntity,
            Role = role,
            Scope = scope,
            ValidFrom = request.ValidFrom,
            ValidTo = request.ValidTo,
            Status = "Active",
            GrantedBy = approval.Request.Maker.Id,
            ApprovedBy = approval.Decision?.Checker.Id,
            ApprovalRequestId = approvalId,
            GrantedAt = now,
        };
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await UserDirectoryStore.TryInsertAssignmentAsync(connection, session.Transaction, row, cancellationToken).ConfigureAwait(false)
            ? new RoleAssignmentGrantResponse
            {
                Status = RoleAssignmentGrantResponse.StatusValue.Granted,
                ApprovalRequestId = ApprovalRequestId.From(approvalId),
                Assignment = row.ToView(),
            }
            : UserErrors.Of(UserErrors.AssignmentExists, "The user already holds this role in this scope, or this approval was already used.");
    }

    private const string PlatformAdminRole = "Platform.Admin";

    internal static HashSet<string> KnownRoles(PermissionOptions options) =>
        [.. options.Grants.Values.SelectMany(roles => roles)];
}

internal sealed class GrantRoleAuditor : ICommandAuditor<GrantRole, RoleAssignmentGrantResponse>
{
    public CommandAuditFacts Describe(GrantRole command, Result<RoleAssignmentGrantResponse>? result) => UserDirectory.Facts(
        command.Request?.UserId.Value,
        UserDirectory.Change("roleGrant", null, result is { IsSuccess: true } ok ? $"{command.Request!.Role}:{ok.Value.Status}" : null));
}

internal sealed class RevokeRoleHandler(DbSession session, RequestContext context, IClock clock) : ICommandHandler<RevokeRole, RoleAssignmentRevokeResponse>
{
    public async Task<Result<RoleAssignmentRevokeResponse>> HandleAsync(RevokeRole command, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var legalEntity = UserDirectory.LegalEntityOf(context);
        var assignment = await UserDirectoryStore.FindAssignmentAsync(connection, session.Transaction, legalEntity, command.Request.AssignmentId, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            return UserErrors.NotFound("role assignment");
        }

        var user = await UserDirectoryStore.FindAsync(connection, session.Transaction, legalEntity, assignment.UserId, cancellationToken).ConfigureAwait(false);
        if (context.Actor.Kind == ActorKind.User && user is not null && string.Equals(user.EntraOid, context.Actor.Id, StringComparison.Ordinal))
        {
            return UserErrors.Of(UserErrors.Sod, "You cannot revoke your own role assignment.");
        }

        var now = clock.Now;
        if (!await UserDirectoryStore.TryRevokeAssignmentAsync(connection, session.Transaction, assignment.AssignmentId, context.Actor.Id, command.Request.Reason, now, cancellationToken).ConfigureAwait(false))
        {
            return UserErrors.Of(UserErrors.InvalidTransition, "The role assignment is already revoked.");
        }

        context.Reason = command.Request.Reason;
        return new RoleAssignmentRevokeResponse { Assignment = (assignment with { Status = "Revoked", RevokedAt = now, RevokedBy = context.Actor.Id }).ToView() };
    }
}

internal sealed class RevokeRoleValidator : AbstractValidator<RevokeRole>
{
    public RevokeRoleValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Reason).NotEmpty().MaximumLength(1024).When(c => c.Request is not null);
    }
}

internal sealed class RevokeRoleAuditor : ICommandAuditor<RevokeRole, RoleAssignmentRevokeResponse>
{
    public CommandAuditFacts Describe(RevokeRole command, Result<RoleAssignmentRevokeResponse>? result) => result is { IsSuccess: true } ok
        ? UserDirectory.Facts(ok.Value.Assignment.UserId.Value, UserDirectory.Change("roleRevoke", ok.Value.Assignment.Role, "Revoked"))
        : UserDirectory.Facts(null);
}

// ---- reads -------------------------------------------------------------------------------------------------------

/// <summary>plt.User.get, plt.User.search and plt.UserProfile.getPreferences. Every read is scoped to the request's legal entity.</summary>
internal sealed class UserDirectoryQueries(DbSession session, RequestContext context)
{
    public async Task<Result<UserGetResponse>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var user = await UserDirectoryStore.FindAsync(connection, session.Transaction, UserDirectory.LegalEntityOf(context), userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return UserErrors.NotFound("user");
        }

        var assignments = await UserDirectoryStore.AssignmentsOfAsync(connection, session.Transaction, userId, cancellationToken).ConfigureAwait(false);
        return new UserGetResponse { User = user.ToView([.. assignments.Select(a => a.ToView())]) };
    }

    public async Task<Result<UserSearchPage>> SearchAsync(string? q, UserStatus? status, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var offset = 0;
        if (cursor is not null && !int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset))
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "cursor is malformed.");
        }

        if (limit is < 1 or > 200)
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.Validation, "limit is outside 1..200.");
        }

        var size = limit ?? 25;
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await UserDirectoryStore.SearchAsync(
            connection, session.Transaction, UserDirectory.LegalEntityOf(context), q, status?.ToString(), offset, size + 1, cancellationToken).ConfigureAwait(false);
        return new UserSearchPage
        {
            Items = [.. rows.Take(size).Select(r => new UserSearchItem { User = r.ToView() })],
            NextCursor = rows.Count > size ? (offset + size).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            Limit = size,
        };
    }

    public async Task<Result<UserProfileGetPreferencesResponse>> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var user = context.Actor.Kind == ActorKind.User
            ? await UserDirectoryStore.FindByOidAsync(connection, session.Transaction, UserDirectory.LegalEntityOf(context), context.Actor.Id, cancellationToken).ConfigureAwait(false)
            : null;
        return user is null
            ? UserErrors.NotFound("user entry of the signed-in user")
            : new UserProfileGetPreferencesResponse { SavedPreferences = new UserPreferences { UiLanguage = user.ToView().Language } };
    }
}
