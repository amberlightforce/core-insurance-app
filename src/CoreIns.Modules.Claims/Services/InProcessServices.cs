using CoreIns.Modules.Claims.Api;
using CoreIns.Modules.Claims.Commands;
using CoreIns.Modules.Claims.Contracts;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Claims.Services;

/// <summary>Shared plumbing of the in-process contracts: commands run through the pipeline with the caller's options.</summary>
internal static class InProcess
{
    public static async Task<T> RunAsync<TCommand, T>(RequestContext context, ICommandHandler<TCommand, T> handler, TCommand command, CommandOptions options, CancellationToken cancellationToken)
        where TCommand : ICommand<T>
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false));
        }
    }

    public static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new DomainException(result.Error!);

    public static DomainException NotAvailable(string operation) =>
        new(DomainError.Of(ModuleCode.CLM, "NOT-AVAILABLE", $"{operation} is not built yet (SL2-CLM-CORE builds FNOL submit/validate/get, claim get/search/close and exposure create)."));
}

/// <summary>The in-process contract <see cref="IClaimsFnolService"/>: submit and validate; saveDraft is CLM-ERR-NOT-AVAILABLE (REQ-CLM-039, later).</summary>
internal sealed class ClaimsFnolService(RequestContext context, ICommandHandler<SubmitFnol, FnolSubmitResponse> submit, FnolValidation validation) : IClaimsFnolService
{
    public Task<FnolSubmitResponse> SubmitAsync(FnolSubmitRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        InProcess.RunAsync(context, submit, new SubmitFnol(request), options, cancellationToken);

    public Task<FnolValidateResponse> ValidateAsync(FnolValidateRequest request, CancellationToken cancellationToken = default) =>
        validation.ValidateAsync(request, cancellationToken);

    public Task<FnolSaveDraftResponse> SaveDraftAsync(FnolSaveDraftRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw InProcess.NotAvailable("clm.Fnol.saveDraft");
}

/// <summary>The in-process contract <see cref="IClaimsClaimService"/>: get and search.</summary>
internal sealed class ClaimsClaimService(RequestContext context, ClaimProtection protection, ClaimReader reader) : IClaimsClaimService
{
    public async Task<ClaimGetResponse> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var view = Guid.TryParse(id, out var claimId)
            ? await reader.GetAsync(protection.Current(context), context.LegalEntity!.Value.Value, new ClaimId(claimId), cancellationToken).ConfigureAwait(false)
            : null;
        return view is null ? throw new DomainException(ClaimSupport.NotFound("claim")) : new ClaimGetResponse { Claim = view };
    }

    public async Task<ClaimSearchPage> SearchAsync(string? cursor = null, int? limit = null, string? claimNumber = null, CancellationToken cancellationToken = default) =>
        InProcess.Unwrap(await ClaimSearch.RunAsync(reader, protection.Current(context), new ClaimSearchCriteria { ClaimNumber = claimNumber }, limit, cursor, cancellationToken)
            .ConfigureAwait(false));

    public async Task<ClaimSearchPage> SearchByCriteriaAsync(ClaimSearchCriteria request, string? cursor = null, int? limit = null, CancellationToken cancellationToken = default) =>
        InProcess.Unwrap(await ClaimSearch.RunAsync(reader, protection.Current(context), request, limit, cursor, cancellationToken).ConfigureAwait(false));
}
