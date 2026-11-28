using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Claims.Commands;

/// <summary>
/// The statutory clocks of a third-party MTPL exposure (BO-CLM-05, D-REG-04, D7), run by CMP through <see cref="IComplianceClockService"/>.
/// <c>clm.exposure.statutory_clocks</c> holds <c>NOT_TRACKED</c> (none) or <c>CLOCKS:&lt;offerId&gt;,&lt;assessmentId&gt;</c>, the CMP instance ids.
/// The payment (10 days from proven offer delivery) and repair clocks start with the statutory offer (BO-CLM-06), not here.
/// </summary>
internal sealed class ExposureClocks(IComplianceClockService clocks, IComplianceClockDefService definitions)
{
    public const string NotTracked = "NOT_TRACKED";
    public const string Prefix = "CLOCKS:";

    /// <summary>Clock codes started at exposure creation, in the order stored.</summary>
    public static readonly string[] StartCodes = ["CLM_MTPL_OFFER", "CLM_MTPL_ASSESSMENT"];

    private static readonly string[] Live = ["RUNNING", "WARNED", "PAUSED"];

    /// <summary>True for a third-party MTPL exposure: MTPL_PD or MTPL_BI of a claimant other than the insured.</summary>
    public static bool Applies(ExposureKind kind, bool thirdParty) => thirdParty && kind is ExposureKind.MtplPd or ExposureKind.MtplBi;

    public static IReadOnlyList<Guid> Ids(string statutoryClocks) =>
        statutoryClocks.StartsWith(Prefix, StringComparison.Ordinal)
            ? [.. statutoryClocks[Prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse)]
            : [];

    /// <summary>Fail closed (D-USR-19): true when a clock this exposure needs has a value that is not Verified or SecondarySource (OQ-008).</summary>
    public async Task<bool> HasUnconfirmedValueAsync(CancellationToken cancellationToken)
    {
        var page = await definitions.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return StartCodes.Any(code => page.Items.SingleOrDefault(d => d.Code.Value == code) is not { LegalStatus: "VERIFIED" or "SECONDARY_SOURCE" });
    }

    /// <summary>The payment and repair clocks start with the statutory offer (BO-CLM-06).</summary>
    public const string PaymentCode = "CLM_MTPL_PAYMENT";
    public const string RepairCode = "CLM_MTPL_REPAIR";
    public const string OfferCode = "CLM_MTPL_OFFER";

    /// <summary>The legal status text of a clock definition (UNVERIFIED when the definition is missing). Fail closed (D-USR-19).</summary>
    public async Task<string> LegalStatusAsync(string code, CancellationToken cancellationToken)
    {
        var page = await definitions.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return page.Items.SingleOrDefault(d => d.Code.Value == code)?.LegalStatus ?? "UNVERIFIED";
    }

    public static bool IsConfirmed(string legalStatus) => legalStatus is "VERIFIED" or "SECONDARY_SOURCE";

    /// <summary>Starts one clock of the exposure from <paramref name="start"/> (key makes a retry find the same instance); returns its CMP instance id.</summary>
    public async Task<Guid> StartOneAsync(string code, ExposureId exposureId, Instant start, string key, string reason, CancellationToken cancellationToken)
    {
        var started = await clocks.StartAsync(
            new ClockStartRequest { ClockCode = ClockCode.Parse(code), SubjectRef = Subject(exposureId), Key = key, StartTime = start, Reason = reason },
            CommandOptions.New(), cancellationToken).ConfigureAwait(false);
        return started.InstanceId;
    }

    /// <summary>Stops the live clock of <paramref name="code"/> among <paramref name="statutoryClocks"/> as met at <paramref name="at"/>; false when none is live.</summary>
    public async Task<bool> StopLiveAsync(string statutoryClocks, string code, Instant at, CancellationToken cancellationToken)
    {
        var state = (await StatesAsync(statutoryClocks, cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => s.ClockCode.Value == code && AnyLive([s]));
        if (state is null)
        {
            return false;
        }

        await clocks.StopAsync(new ClockStopRequest { InstanceId = state.InstanceId, Outcome = ClockStopRequest.OutcomeValue.Met, StopTime = at }, CommandOptions.New(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Cancels one clock instance when it is still live (repair in kind replaces the payment obligation).</summary>
    public async Task CancelOneAsync(Guid instanceId, string reason, CancellationToken cancellationToken)
    {
        var response = await clocks.GetAsync(instanceId.ToString(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.Instances.Any(i => AnyLive([i])))
        {
            await clocks.CancelAsync(new ClockCancelRequest { InstanceId = instanceId, Reason = reason }, CommandOptions.New(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts the offer and assessment clocks from <paramref name="start"/>; returns the value for <c>statutory_clocks</c>.</summary>
    public async Task<string> StartAsync(ExposureId exposureId, Instant start, string key, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        foreach (var code in StartCodes)
        {
            var started = await clocks.StartAsync(
                new ClockStartRequest
                {
                    ClockCode = ClockCode.Parse(code),
                    SubjectRef = Subject(exposureId),
                    Key = key,
                    StartTime = start,
                    Reason = "Third-party MTPL exposure opened",
                }, CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            ids.Add(started.InstanceId);
        }

        return Prefix + string.Join(',', ids);
    }

    /// <summary>The current state of each clock of <paramref name="statutoryClocks"/> (empty when not tracked).</summary>
    public async Task<IReadOnlyList<ClockInstanceView>> StatesAsync(string statutoryClocks, CancellationToken cancellationToken)
    {
        var views = new List<ClockInstanceView>();
        foreach (var id in Ids(statutoryClocks))
        {
            var response = await clocks.GetAsync(id.ToString(), cancellationToken: cancellationToken).ConfigureAwait(false);
            views.AddRange(response.Instances);
        }

        return views;
    }

    /// <summary>True when a clock still counts down or is paused: such a clock blocks the exposure close (D-CON-04).</summary>
    public static bool AnyLive(IEnumerable<ClockInstanceView> states) => states.Any(s => Live.Contains(s.Status.ToString().ToUpperInvariant(), StringComparer.Ordinal));

    /// <summary>Cancels the live clocks of a closed exposure. TODO(hardening): a CMP failure after the CLM commit is swallowed; a sweep should reconcile.</summary>
    public async Task CancelLiveAsync(string statutoryClocks, string reason, CancellationToken cancellationToken)
    {
        foreach (var state in await StatesAsync(statutoryClocks, cancellationToken).ConfigureAwait(false))
        {
            if (!AnyLive([state]))
            {
                continue;
            }

            try
            {
                await clocks.CancelAsync(new ClockCancelRequest { InstanceId = state.InstanceId, Reason = reason }, CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException)
            {
                // TODO(hardening): reconcile a clock that CMP refused to cancel.
            }
        }
    }

    /// <summary>Resumes paused clocks of a reopened exposure; true when clocks were cancelled at close and need a fresh start.</summary>
    public async Task<bool> ResumeAsync(string statutoryClocks, string reason, CancellationToken cancellationToken)
    {
        var restart = false;
        foreach (var state in await StatesAsync(statutoryClocks, cancellationToken).ConfigureAwait(false))
        {
            switch (state.Status.ToString().ToUpperInvariant())
            {
                case "PAUSED":
                    await clocks.ResumeAsync(new ClockResumeRequest { InstanceId = state.InstanceId, Reason = reason }, CommandOptions.New(), cancellationToken).ConfigureAwait(false);
                    break;
                case "CANCELLED":
                    restart = true;
                    break;
            }
        }

        return restart;
    }

    /// <summary>
    /// The clock part of reopening an exposure, shared by exposure reopen and claim reopen (BO-CLM-01/02/05): paused clocks resume;
    /// clocks cancelled at close are started afresh from <paramref name="restartFrom"/> (key per reopen) once their values are
    /// usable. Returns the <c>statutory_clocks</c> value to store (unchanged when nothing restarts), or MTPL-CLOCKS-REQUIRED in Production
    /// when the clock values are not legally confirmed.
    /// </summary>
    public async Task<Result<string>> ReopenAsync(
        ExposureId exposureId, string statutoryClocks, Instant restartFrom, string key, string reason, bool production, CancellationToken cancellationToken)
    {
        if (!statutoryClocks.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return statutoryClocks;
        }

        if (!await ResumeAsync(statutoryClocks, reason, cancellationToken).ConfigureAwait(false))
        {
            return statutoryClocks;
        }

        if (production && await HasUnconfirmedValueAsync(cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.CLM, "MTPL-CLOCKS-REQUIRED", "Third-party MTPL exposures need statutory clocks whose values are legally confirmed.");
        }

        return await StartAsync(exposureId, restartFrom, key, cancellationToken).ConfigureAwait(false);
    }

    private static ObjectRef Subject(ExposureId exposureId) => new(ModuleCode.CLM, "Exposure", exposureId.Value.ToString());
}
