using System.Text.RegularExpressions;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Contracts.Common;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;

namespace CoreIns.Modules.Underwriting.Commands;

/// <summary>Wire values of the issue-type enums (the contract's string enums) and the database values.</summary>
internal static class IssueTypeCodes
{
    public static string Db(IssueTypeUpsertRequest.SeverityValue value) => value == IssueTypeUpsertRequest.SeverityValue.Decline ? "DECLINE" : "REFER";

    public static string Db(IssueTypeUpsertRequest.DefaultLaneValue value) => value switch
    {
        IssueTypeUpsertRequest.DefaultLaneValue.StraightThrough => "STRAIGHT_THROUGH",
        IssueTypeUpsertRequest.DefaultLaneValue.Expert => "EXPERT",
        _ => "ASSISTED",
    };

    public static string Db(BlockingPoint value) => value switch
    {
        BlockingPoint.PreQuote => "PRE_QUOTE",
        BlockingPoint.PreBind => "PRE_BIND",
        BlockingPoint.PreIssue => "PRE_ISSUE",
        _ => "NON_BLOCKING",
    };

    public static IssueTypeItem.SeverityValue Severity(string db) => db == "DECLINE" ? IssueTypeItem.SeverityValue.Decline : IssueTypeItem.SeverityValue.Refer;

    public static IssueTypeItem.DefaultLaneValue Lane(string db) => db switch
    {
        "STRAIGHT_THROUGH" => IssueTypeItem.DefaultLaneValue.StraightThrough,
        "EXPERT" => IssueTypeItem.DefaultLaneValue.Expert,
        _ => IssueTypeItem.DefaultLaneValue.Assisted,
    };

    public static BlockingPoint Blocking(string db) => db switch
    {
        "PRE_QUOTE" => BlockingPoint.PreQuote,
        "PRE_BIND" => BlockingPoint.PreBind,
        "PRE_ISSUE" => BlockingPoint.PreIssue,
        _ => BlockingPoint.NonBlocking,
    };
}

/// <summary><c>uw.IssueType.upsert</c>: creates or changes an issue type of the catalogue (BO-UW-02).</summary>
internal sealed record UpsertIssueType(IssueTypeUpsertRequest Request) : ICommand<IssueTypeItem>;

internal static partial class IssueTypeRules
{
    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,63}$")]
    public static partial Regex CodePattern();
}

internal sealed class UpsertIssueTypeValidator : AbstractValidator<UpsertIssueType>
{
    public UpsertIssueTypeValidator()
    {
        RuleFor(c => c.Request.Code).NotEmpty().Must(c => c is not null && IssueTypeRules.CodePattern().IsMatch(c))
            .WithMessage("The code is 2 to 64 characters: capitals, digits and '_', starting with a capital.");
        RuleFor(c => c.Request.NameEl).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.NameEn).NotEmpty().MaximumLength(200);
    }
}

internal sealed class UpsertIssueTypeHandler(IssueTypeStore store) : ICommandHandler<UpsertIssueType, IssueTypeItem>
{
    public async Task<Result<IssueTypeItem>> HandleAsync(UpsertIssueType command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var existing = await store.GetAsync(request.Code, cancellationToken).ConfigureAwait(false);
        if (existing is null && request.RecordVersion is not null)
        {
            return DomainError.Of(ModuleCode.UW, "NOT-FOUND", $"Issue type {request.Code} does not exist; leave recordVersion out to create it.");
        }

        if (existing is not null && request.RecordVersion is null)
        {
            return DomainError.Of(ModuleCode.UW, "DUPLICATE", $"Issue type {request.Code} already exists; send its recordVersion ({existing.RecordVersion}) to change it.");
        }

        if (existing is not null && request.RecordVersion != existing.RecordVersion)
        {
            return DomainError.Of(ModuleCode.UW, "STALE", $"Issue type {request.Code} changed since you read it (recordVersion {existing.RecordVersion}); reload and try again.");
        }

        var row = new IssueTypeRecord
        {
            Code = request.Code, NameEl = request.NameEl.Trim(), NameEn = request.NameEn.Trim(), Severity = IssueTypeCodes.Db(request.Severity),
            DefaultLane = IssueTypeCodes.Db(request.DefaultLane), BlockingPoint = IssueTypeCodes.Db(request.BlockingPoint), Active = request.Active ?? true,
        };
        if (await store.UpsertAsync(row, request.RecordVersion, cancellationToken).ConfigureAwait(false) is null)
        {
            return DomainError.Of(ModuleCode.UW, "STALE", $"Issue type {request.Code} was changed by someone else meanwhile; reload and try again.");
        }

        var stored = await store.GetAsync(request.Code, cancellationToken).ConfigureAwait(false);
        return IssueTypeQueries.Item(stored!);
    }
}

internal sealed class UpsertIssueTypeAuditor : ICommandAuditor<UpsertIssueType, IssueTypeItem>
{
    public CommandAuditFacts Describe(UpsertIssueType command, Result<IssueTypeItem>? result) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.UW, "IssueType", command.Request.Code),
        BusinessKeys = BusinessKeys.Empty.With("issueType", command.Request.Code),
    };
}
