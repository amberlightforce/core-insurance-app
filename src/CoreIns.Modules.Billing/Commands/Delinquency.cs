using System.Text.Json;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Modules.Billing.Queries;
using CoreIns.Modules.Billing.Services;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Billing.Commands;

/// <summary>A command on one delinquency case (for the audit record).</summary>
internal interface IDelinquencyCommand
{
    /// <summary>The case, when the command names it.</summary>
    Guid? CaseId { get; }
}

/// <summary><c>bil.Delinquency.sweep</c> (internal, scheduled): opens cases for invoices overdue past the grace period; returns the number opened.</summary>
internal sealed record SweepDelinquency : ICommand<int>, IDelinquencyCommand
{
    public Guid? CaseId => null;
}

/// <summary><c>bil.Delinquency.open</c> (internal): opens a case for a term, e.g. when a SEPA return (AM04, MS03) leaves arrears and re-presentation failed. Returns 1 when a case is open afterwards.</summary>
internal sealed record OpenDelinquency(PolicyTermId TermId, DelinquencyTrigger Trigger) : ICommand<int>, IDelinquencyCommand
{
    public Guid? CaseId => null;
}

/// <summary><c>bil.Delinquency.noticeDelivered</c> (internal, from <c>doc.DocumentDelivered</c>).</summary>
internal sealed record NoticeDelivered(DocumentId DocumentId, BusinessDate NotificationDate, string Medium, string ProofRef) : ICommand<int>, IDelinquencyCommand
{
    public Guid? CaseId => null;
}

/// <summary><c>bil.Delinquency.noticeFailed</c> (internal, from <c>doc.DeliveryFailed</c>).</summary>
internal sealed record NoticeFailed(DocumentId DocumentId, string Reason) : ICommand<int>, IDelinquencyCommand
{
    public Guid? CaseId => null;
}

/// <summary><c>bil.Delinquency.clockElapsed</c> (internal, from <c>cmp.ClockElapsed</c> of BIL_NONPAY_NOTICE).</summary>
internal sealed record NonpayClockElapsed(Guid ClockInstanceId) : ICommand<int>, IDelinquencyCommand
{
    public Guid? CaseId => null;
}

internal sealed record RecordDelinquencyContact(DelinquencyRecordContactRequest Request) : ICommand<DelinquencyRecordContactResponse>, IDelinquencyCommand
{
    public Guid? CaseId => DelinquencyData.CaseIdOf(Request.Data);
}

internal sealed record ArrangeDelinquency(DelinquencyArrangeRequest Request) : ICommand<DelinquencyArrangeResponse>, IDelinquencyCommand
{
    public Guid? CaseId => DelinquencyData.CaseIdOf(Request.Data);
}

internal sealed record ReleaseDelinquencyHold(DelinquencyReleaseHoldRequest Request) : ICommand<DelinquencyReleaseHoldResponse>, IDelinquencyCommand
{
    public Guid? CaseId => DelinquencyData.CaseIdOf(Request.Data);
}

/// <summary>
/// The free-form <c>data</c> of the delinquency operations (the PRD gives the inputs as "process, data"): <c>delinquencyId</c> always;
/// recordContact takes <c>channel</c>, <c>outcome</c>, <c>note</c>; arrange takes <c>promiseDate</c> (yyyy-MM-dd) and <c>note</c>;
/// releaseHold takes <c>reason</c> and <c>release</c> (true lifts the hold or arrangement and resumes the clock; absent or false
/// places the hold).
/// </summary>
internal static class DelinquencyData
{
    public static Guid? CaseIdOf(JsonElement? data) => Guid.TryParse(Text(data, "delinquencyId"), out var id) ? id : null;

    public static string? Text(JsonElement? data, string name) =>
        data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool Flag(JsonElement? data, string name) =>
        data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static DomainError Invalid(string detail) => DomainError.Of(ModuleCode.BIL, "VALIDATION", detail);
}

internal sealed class SweepDelinquencyHandler(NonpayService nonpay) : ICommandHandler<SweepDelinquency, int>
{
    public async Task<Result<int>> HandleAsync(SweepDelinquency command, CancellationToken cancellationToken) =>
        await nonpay.SweepAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class OpenDelinquencyHandler(NonpayService nonpay) : ICommandHandler<OpenDelinquency, int>
{
    public async Task<Result<int>> HandleAsync(OpenDelinquency command, CancellationToken cancellationToken) =>
        await nonpay.OpenAsync(command.TermId, command.Trigger, cancellationToken).ConfigureAwait(false) is null ? 0 : 1;
}

internal sealed class NoticeDeliveredHandler(NonpayService nonpay) : ICommandHandler<NoticeDelivered, int>
{
    public async Task<Result<int>> HandleAsync(NoticeDelivered command, CancellationToken cancellationToken)
    {
        await nonpay.NoticeDeliveredAsync(command.DocumentId, command.NotificationDate, command.Medium, command.ProofRef, cancellationToken).ConfigureAwait(false);
        return 1;
    }
}

internal sealed class NoticeFailedHandler(NonpayService nonpay) : ICommandHandler<NoticeFailed, int>
{
    public async Task<Result<int>> HandleAsync(NoticeFailed command, CancellationToken cancellationToken)
    {
        await nonpay.NoticeFailedAsync(command.DocumentId, command.Reason, cancellationToken).ConfigureAwait(false);
        return 1;
    }
}

internal sealed class NonpayClockElapsedHandler(NonpayService nonpay) : ICommandHandler<NonpayClockElapsed, int>
{
    public async Task<Result<int>> HandleAsync(NonpayClockElapsed command, CancellationToken cancellationToken)
    {
        await nonpay.ClockElapsedAsync(command.ClockInstanceId, cancellationToken).ConfigureAwait(false);
        return 1;
    }
}

internal sealed class RecordDelinquencyContactHandler(NonpayService nonpay, DelinquencyReader reader) : ICommandHandler<RecordDelinquencyContact, DelinquencyRecordContactResponse>
{
    public async Task<Result<DelinquencyRecordContactResponse>> HandleAsync(RecordDelinquencyContact command, CancellationToken cancellationToken)
    {
        var data = command.Request.Data;
        if (command.CaseId is not { } id)
        {
            return DelinquencyData.Invalid("data.delinquencyId is required.");
        }

        var channel = DelinquencyData.Text(data, "channel");
        var outcome = DelinquencyData.Text(data, "outcome");
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(outcome))
        {
            return DelinquencyData.Invalid("data.channel and data.outcome are required.");
        }

        var row = await nonpay.RecordContactAsync(id, channel, outcome, DelinquencyData.Text(data, "note"), cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new DelinquencyRecordContactResponse { Process = await reader.ViewAsync(row.Value, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class ArrangeDelinquencyHandler(NonpayService nonpay, DelinquencyReader reader) : ICommandHandler<ArrangeDelinquency, DelinquencyArrangeResponse>
{
    public async Task<Result<DelinquencyArrangeResponse>> HandleAsync(ArrangeDelinquency command, CancellationToken cancellationToken)
    {
        var data = command.Request.Data;
        if (command.CaseId is not { } id)
        {
            return DelinquencyData.Invalid("data.delinquencyId is required.");
        }

        if (!BusinessDate.TryParse(DelinquencyData.Text(data, "promiseDate"), out var promise))
        {
            return DelinquencyData.Invalid("data.promiseDate (yyyy-MM-dd) is required.");
        }

        var row = await nonpay.ArrangeAsync(id, promise, DelinquencyData.Text(data, "note"), cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new DelinquencyArrangeResponse { Process = await reader.ViewAsync(row.Value, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class ReleaseDelinquencyHoldHandler(NonpayService nonpay, DelinquencyReader reader) : ICommandHandler<ReleaseDelinquencyHold, DelinquencyReleaseHoldResponse>
{
    public async Task<Result<DelinquencyReleaseHoldResponse>> HandleAsync(ReleaseDelinquencyHold command, CancellationToken cancellationToken)
    {
        var data = command.Request.Data;
        if (command.CaseId is not { } id)
        {
            return DelinquencyData.Invalid("data.delinquencyId is required.");
        }

        var row = await nonpay.HoldAsync(id, DelinquencyData.Flag(data, "release"), DelinquencyData.Text(data, "reason"), cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new DelinquencyReleaseHoldResponse { Process = await reader.ViewAsync(row.Value, cancellationToken).ConfigureAwait(false) };
    }
}

internal sealed class DelinquencyAuditor<TCommand, TResult> : ICommandAuditor<TCommand, TResult>
    where TCommand : IDelinquencyCommand
{
    public CommandAuditFacts Describe(TCommand command, Result<TResult>? result) => new()
    {
        ObjectRef = command.CaseId is { } id ? NonpayService.SubjectOf(id) : null,
        BusinessKeys = command.CaseId is { } key ? BusinessKeys.Empty.With("processId", key.ToString()) : BusinessKeys.Empty,
    };
}
