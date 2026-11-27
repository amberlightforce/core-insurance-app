using System.Text.Json.Nodes;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Npgsql;

namespace CoreIns.Platform.Approvals;

/// <summary>
/// <c>plt.ApprovalType.register</c> (BO-PLT-02): the administrator-owned catalogue of approval types (description, default
/// referral role and default authority type). It is documentation and defaults for the owning modules and the approvals
/// inbox; it does not grant authority and does not change who may decide (SoD stays in <see cref="DecideApprovalHandler"/>).
/// Registering a type again updates it (version + 1).
/// TODO(hardening): <see cref="RequestApprovalHandler"/> does not yet refuse unregistered types, and an owning module's own
/// defaults are not yet read from this table.
/// </summary>
internal sealed record RegisterApprovalType(ApprovalTypeRegisterRequest Request) : ICommand<ApprovalTypeRegisterResponse>;

internal sealed class RegisterApprovalTypeValidator : AbstractValidator<RegisterApprovalType>
{
    public RegisterApprovalTypeValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Type).NotEmpty().Matches(@"^[A-Z]{2,3}\.[A-Za-z0-9_]{2,64}$").When(c => c.Request is not null);
        RuleFor(c => c.Request.Description).NotEmpty().MaximumLength(512).When(c => c.Request is not null);
        RuleFor(c => c.Request.DefaultReferralRole).MaximumLength(128).When(c => c.Request is not null);
        RuleFor(c => c.Request.DefaultAuthorityType).MaximumLength(128).When(c => c.Request is not null);
    }
}

internal sealed class RegisterApprovalTypeHandler(DbSession session, RequestContext context, IClock clock, IAuthorityTypeRegistry registry)
    : ICommandHandler<RegisterApprovalType, ApprovalTypeRegisterResponse>
{
    public async Task<Result<ApprovalTypeRegisterResponse>> HandleAsync(RegisterApprovalType command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request.DefaultAuthorityType is { } authority && (!AuthorityTypeCode.TryParse(authority, out var code) || !registry.TryGet(code, out _)))
        {
            return DomainError.Of(ModuleCode.PLT, PlatformErrors.UnknownAuthorityType, $"Authority type {authority} is not registered.");
        }

        var connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var sql = new NpgsqlCommand(
            """
            INSERT INTO plt.approval_type AS t (approval_type, description, default_referral_role, default_authority_type, version, registered_by_kind, registered_by_id, updated_at)
            VALUES (@type, @description, @role, @authority, 1, @kind, @actor, @now)
            ON CONFLICT (approval_type) DO UPDATE SET
              description = EXCLUDED.description, default_referral_role = EXCLUDED.default_referral_role,
              default_authority_type = EXCLUDED.default_authority_type, version = t.version + 1,
              registered_by_kind = EXCLUDED.registered_by_kind, registered_by_id = EXCLUDED.registered_by_id, updated_at = EXCLUDED.updated_at
            RETURNING version
            """,
            connection, session.Transaction);
        sql.Parameters.AddWithValue("type", request.Type);
        sql.Parameters.AddWithValue("description", request.Description);
        sql.Parameters.AddWithValue("role", (object?)request.DefaultReferralRole ?? DBNull.Value);
        sql.Parameters.AddWithValue("authority", (object?)request.DefaultAuthorityType ?? DBNull.Value);
        sql.Parameters.AddWithValue("kind", context.Actor.KindCode);
        sql.Parameters.AddWithValue("actor", context.Actor.Id);
        sql.Parameters.AddWithValue("now", clock.Now.ToUtcDateTime());
        var version = Convert.ToInt32(await sql.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        return new ApprovalTypeRegisterResponse
        {
            Type = request.Type, Description = request.Description, DefaultReferralRole = request.DefaultReferralRole,
            DefaultAuthorityType = request.DefaultAuthorityType, Version = version,
        };
    }
}

internal sealed class RegisterApprovalTypeAuditor : ICommandAuditor<RegisterApprovalType, ApprovalTypeRegisterResponse>
{
    public CommandAuditFacts Describe(RegisterApprovalType command, Result<ApprovalTypeRegisterResponse>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.PLT, "ApprovalType", command.Request?.Type ?? "unknown"),
        Changes = result is { IsSuccess: true } ok
            ? [new AuditChange("version", null, JsonValue.Create(ok.Value.Version)), new AuditChange("description", null, JsonValue.Create(ok.Value.Description))]
            : [],
        BusinessKeys = command.Request is null ? BusinessKeys.Empty : BusinessKeys.Empty.With("approvalType", command.Request.Type),
    };
}
