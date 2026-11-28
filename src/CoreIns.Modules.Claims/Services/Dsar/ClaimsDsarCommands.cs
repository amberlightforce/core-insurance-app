using System.Text.Json;
using System.Text.Json.Serialization;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.MigrationImport;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Dsar;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Claims.Services.Dsar;

/// <summary><c>clm.Dsar.export</c>: the claim data package of a party, for the CMP fan-out.</summary>
internal sealed record DsarExport(DsarExportRequest Request) : ICommand<DsarExportResponse>;

/// <summary><c>clm.Dsar.rectify</c>: CLM holds party ids only, so a rectification restricts the claim file while PTY corrects the data.</summary>
internal sealed record DsarRectify(DsarRectifyRequest Request) : ICommand<DsarRectifyResponse>;

/// <summary><c>clm.Dsar.restrict</c>: restricts the party's claim records (one restriction row per class).</summary>
internal sealed record DsarRestrict(DsarRestrictRequest Request) : ICommand<DsarRestrictResponse>;

/// <summary><c>clm.Dsar.purge</c>: the erase of the participant; regulated claim records are restricted, not destroyed.</summary>
internal sealed record DsarPurge(DsarPurgeRequest Request) : ICommand<DsarPurgeResponse>;

/// <summary>Shared reading of the DSAR route bodies and the serialization of the participant's result (route == engine).</summary>
internal static class DsarRoutes
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static bool TryParty(JsonElement? party, out PartyId id)
    {
        id = default;
        var text = party switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString(),
            { ValueKind: JsonValueKind.Object } o when o.TryGetProperty("partyId", out var p) && p.ValueKind == JsonValueKind.String => p.GetString(),
            _ => null,
        };
        if (!Guid.TryParse(text, out var guid) || guid == Guid.Empty)
        {
            return false;
        }

        id = new PartyId(guid);
        return true;
    }

    public static string? Text(JsonElement? scope, string name) => scope is { } s ? ImportJson.Text(s, name) : null;

    public static DsarContext Context(JsonElement? scope, RequestContext context) =>
        new(Guid.TryParse(Text(scope, "caseId"), out var caseId) ? caseId : Guid.NewGuid(), context.Actor.ToString(), Text(scope, "approvedBy"));

    public static JsonElement Result(DsarModuleResult result, object? extra = null) =>
        JsonSerializer.SerializeToElement(new { module = result.Module, outcomes = result.Outcomes, exportPayload = result.ExportPayload, extra }, Json);

    public static DomainError InvalidParty() => FnolAssessment.Invalid("party", "PARTY", "party must be a party id (string or { partyId }).");
}

internal sealed class DsarExportHandler(IEnumerable<IDsarParticipant> participants, RequestContext context) : ICommandHandler<DsarExport, DsarExportResponse>
{
    public async Task<Result<DsarExportResponse>> HandleAsync(DsarExport command, CancellationToken cancellationToken)
    {
        if (!DsarRoutes.TryParty(command.Request.Party, out var party))
        {
            return DsarRoutes.InvalidParty();
        }

        var participant = participants.OfType<ClaimsDsarParticipant>().Single();
        var result = await participant.ExportAsync(SubjectRef.ForParty(party), DsarRoutes.Context(null, context), cancellationToken).ConfigureAwait(false);
        return new DsarExportResponse { ClaimDataPackage = DsarRoutes.Result(result) };
    }
}

internal sealed class DsarRectifyHandler(IEnumerable<IDsarParticipant> participants) : ICommandHandler<DsarRectify, DsarRectifyResponse>
{
    public async Task<Result<DsarRectifyResponse>> HandleAsync(DsarRectify command, CancellationToken cancellationToken)
    {
        if (!DsarRoutes.TryParty(command.Request.Party, out var party))
        {
            return DsarRoutes.InvalidParty();
        }

        // GDPR Art 18(1)(a): the records are restricted while the accuracy is verified. The corrections themselves belong to PTY (CLM keeps party ids).
        var participant = participants.OfType<ClaimsDsarParticipant>().Single();
        var result = await participant.RestrictAsync(SubjectRef.ForParty(party), "RECTIFICATION_REQUEST", string.Empty, null, cancellationToken).ConfigureAwait(false);
        return new DsarRectifyResponse
        {
            Result = DsarRoutes.Result(result, new { corrections = "NOT_APPLIED", detail = "CLM holds party identifiers only; correct the personal data in PTY. The claim file is restricted meanwhile." }),
        };
    }
}

internal sealed class DsarRestrictHandler(IEnumerable<IDsarParticipant> participants) : ICommandHandler<DsarRestrict, DsarRestrictResponse>
{
    public async Task<Result<DsarRestrictResponse>> HandleAsync(DsarRestrict command, CancellationToken cancellationToken)
    {
        if (!DsarRoutes.TryParty(command.Request.Party, out var party))
        {
            return DsarRoutes.InvalidParty();
        }

        var scope = command.Request.Scope;
        DateOnly? end = null;
        if (DsarRoutes.Text(scope, "retentionEnd") is { } text)
        {
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            {
                return FnolAssessment.Invalid("scope.retentionEnd", "DATE", "retentionEnd must be a date (yyyy-MM-dd).");
            }

            end = parsed;
        }

        var participant = participants.OfType<ClaimsDsarParticipant>().Single();
        var result = await participant.RestrictAsync(
            SubjectRef.ForParty(party), DsarRoutes.Text(scope, "reason") ?? "DSAR_RESTRICTION", DsarRoutes.Text(scope, "legalBasis") ?? string.Empty, end, cancellationToken)
            .ConfigureAwait(false);
        return new DsarRestrictResponse { Result = DsarRoutes.Result(result) };
    }
}

internal sealed class DsarPurgeHandler(IEnumerable<IDsarParticipant> participants, RequestContext context) : ICommandHandler<DsarPurge, DsarPurgeResponse>
{
    public async Task<Result<DsarPurgeResponse>> HandleAsync(DsarPurge command, CancellationToken cancellationToken)
    {
        if (!DsarRoutes.TryParty(command.Request.Party, out var party))
        {
            return DsarRoutes.InvalidParty();
        }

        var participant = participants.OfType<ClaimsDsarParticipant>().Single();
        var result = await participant.EraseAsync(SubjectRef.ForParty(party), DsarRoutes.Context(command.Request.Scope, context), cancellationToken).ConfigureAwait(false);
        return new DsarPurgeResponse { Result = DsarRoutes.Result(result) };
    }
}
