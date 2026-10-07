using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Party.Services;

/// <summary>
/// The in-process contract <see cref="IPartyPartyService"/> (D-ARC-16) other modules call. Commands run through the same
/// pipeline as HTTP with the caller's <see cref="CommandOptions"/> (idempotency, dry-run); a failure is thrown as a
/// <see cref="DomainException"/> carrying the PTY-ERR code. Merge, unmerge and update arrive with their work packages
/// (W6-PTY merge, W2-PTY-01 update) and fail with PTY-ERR-NOT-AVAILABLE until then.
/// </summary>
internal sealed class PartyPartyService(
    RequestContext context,
    IClock clock,
    ICommandHandler<CreateParty, PartyCreateResponse> create,
    ICommandHandler<RevealParty, PartyGetResponse> reveal,
    PartyReader reader,
    PartySearch search,
    PartyProtection protection) : IPartyPartyService
{
    public async Task<PartyCreateResponse> CreateAsync(PartyCreateRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (context.Use(options.IdempotencyKey, options.DryRun))
        {
            return Unwrap(await create.HandleAsync(new CreateParty(request), cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<PartyGetResponse> GetAsync(
        string id, ValidAt? validAt = null, Instant? knownAt = null, string? partyNumber = null, string? profile = null, string? revealPurpose = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.Now;
        var valid = validAt is { Date: { } date } ? date
            : validAt is { Instant: { } instant } ? new BusinessDate(DateOnly.FromDateTime(instant.ToUtcDateTime()))
            : new BusinessDate(DateOnly.FromDateTime(now.ToUtcDateTime()));
        var at = new TimePoint(valid, knownAt ?? now);
        Guid? partyId = Guid.TryParse(id, out var guid) ? guid : null;
        var number = partyId is null ? partyNumber ?? id : partyNumber;
        if (revealPurpose is not null)
        {
            var reason = context.Reason;
            context.Reason = revealPurpose;
            try
            {
                return Unwrap(await reveal.HandleAsync(new RevealParty(partyId, number, at, revealPurpose), cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                context.Reason = reason;
            }
        }

        var view = await reader.GetAsync(protection.Current(context), context.LegalEntity!.Value, partyId, number, at, revealP2: false, cancellationToken)
            .ConfigureAwait(false);
        return view is null ? throw new DomainException(DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party does not exist.")) : new PartyGetResponse { Party = view };
    }

    public async Task<PartySearchPage> SearchAsync(
        ValidAt? validAt = null, string? cursor = null, int? limit = null, string? criteria = null, string? name = null, string? identifierScheme = null,
        string? identifierValue = null, string? partyNumber = null, CancellationToken cancellationToken = default)
    {
        var offset = PartySearch.DecodeCursor(cursor) ?? throw new DomainException(DomainError.Of(ModuleCode.PTY, "VALIDATION", "The cursor is malformed."));
        return Unwrap(await search.SearchAsync(
            new PartySearchCriteria(criteria, name, identifierScheme, identifierValue, partyNumber, Math.Clamp(limit ?? 25, 1, 200), offset), cancellationToken)
            .ConfigureAwait(false));
    }

    public Task<PartyMergeResponse> MergeAsync(PartyMergeRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw NotAvailable("pty.Party.merge");

    public Task<PartyUnmergeResponse> UnmergeAsync(PartyUnmergeRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw NotAvailable("pty.Party.unmerge");

    public Task<PartyUpdateResponse> UpdateAsync(string id, PartyUpdateRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw NotAvailable("pty.Party.update");

    internal static DomainException NotAvailable(string operation) =>
        new(DomainError.Of(ModuleCode.PTY, "NOT-AVAILABLE", $"{operation} is not built yet (SL-0 implements create, get and search)."));

    private static T Unwrap<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new DomainException(result.Error);
}

/// <summary>The in-process contract <see cref="IPartyProducerCodeService"/>: <c>pty.ProducerCode.validate</c> for POL's bind gate.</summary>
internal sealed class PartyProducerCodeService(IntermediaryQueries queries, IClock clock) : IPartyProducerCodeService
{
    public Task<ProducerCodeValidateResponse> ValidateAsync(ProducerCodeValidateRequest request, ValidAt? validAt = null, CancellationToken cancellationToken = default)
    {
        var date = validAt is { Date: { } d } ? d
            : validAt is { Instant: { } i } ? new BusinessDate(DateOnly.FromDateTime(i.ToUtcDateTime()))
            : new BusinessDate(DateOnly.FromDateTime(clock.Now.ToUtcDateTime()));
        return queries.ValidateAsync(request, date, cancellationToken);
    }
}
