using CoreIns.Modules.Underwriting.Commands;
using CoreIns.Modules.Underwriting.Contracts;
using CoreIns.Modules.Underwriting.Contracts.Api;
using CoreIns.Modules.Underwriting.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Telemetry;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Paging;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Underwriting.Services;

/// <summary><c>uw.Contingency.list</c> (REQ-UW-006, -245, -246): contingencies by term or job, with status, due date and the customer's text.</summary>
internal sealed class ContingencyQueries(ContingencyStore store, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private static readonly string[] Statuses = ["Open", "Satisfied", "Waived", "ConsequenceApplied"];

    public async Task<Result<ContingencyListPage>> ListAsync(
        string? termRef, string? policyRef, string? filters, string? audience, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (policyRef is not null)
        {
            // TODO(hardening): resolve a policy to its terms through POL; until then callers ask by term.
            return DomainError.Of(ModuleCode.UW, "NOT-AVAILABLE", "Listing by policyRef is not available yet; use termRef.");
        }

        Guid? term = null;
        if (termRef is not null)
        {
            if (!Guid.TryParse(termRef, out var parsed))
            {
                return DomainError.Of(ModuleCode.UW, "VALIDATION", "termRef must be a uuid.");
            }

            term = parsed;
        }

        var customer = false;
        if (audience is not null)
        {
            switch (audience.ToUpperInvariant())
            {
                case "CUSTOMER":
                    customer = true;
                    break;
                case "STAFF":
                    break;
                default:
                    return DomainError.Of(ModuleCode.UW, "VALIDATION", "audience must be STAFF or CUSTOMER.");
            }
        }

        var pairs = RegisterFilterParser.Split(filters);
        if (pairs is null)
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "filters must be key=value pairs separated by commas.");
        }

        Guid? job = null;
        string? status = null, owner = null, type = null;
        bool? overdue = null;
        foreach (var (key, value) in pairs)
        {
            switch (key)
            {
                case "jobRef" when Guid.TryParse(value, out var j):
                    job = j;
                    break;
                case "status" when Statuses.Contains(value, StringComparer.Ordinal):
                    status = value;
                    break;
                case "overdue" when bool.TryParse(value, out var o):
                    overdue = o;
                    break;
                case "owner" when value.Length is > 0 and <= 200:
                    owner = value;
                    break;
                case "type" when value.Length is > 0 and <= 64:
                    type = value;
                    break;
                default:
                    return DomainError.Of(ModuleCode.UW, "VALIDATION", $"Unknown or malformed filter '{key}'; use jobRef, status, overdue, owner or type.");
            }
        }

        if (!KeysetPaging.TryTake(limit, 50, out var size) || !TryDecode(cursor, out var after))
        {
            return DomainError.Of(ModuleCode.UW, "VALIDATION", "cursor is malformed or limit is outside 1..200.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var rows = await store.ListAsync(legalEntity, new ContingencyFilter(term, job, status, overdue, owner, type), after, size + 1, store.Today, cancellationToken).ConfigureAwait(false);
        var (page, next) = KeysetPaging.Slice(rows, size, last => KeysetPaging.Encode(KeysetPaging.Part(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc).Ticks), KeysetPaging.Part(last.ContingencyId)));
        var today = store.Today;
        return new ContingencyListPage
        {
            Items = [.. page.Select(r => customer ? ForCustomer(ContingencySupport.Item(r, today)) : ContingencySupport.Item(r, today))],
            NextCursor = next,
            Limit = size,
        };
    }

    /// <summary>What a customer or producer sees: the customer text in place of the internal description, no owner.</summary>
    private static ContingencyListItem ForCustomer(ContingencyListItem item) =>
        item with { Description = item.CustomerText ?? item.Description, Owner = string.Empty, ConsequenceCode = null };

    private static bool TryDecode(string? cursor, out (DateTime CreatedAt, Guid Id)? after)
    {
        after = null;
        if (!KeysetPaging.TryDecode(cursor, 2, out var parts))
        {
            return false;
        }

        if (parts is null)
        {
            return true;
        }

        if (!KeysetPaging.TryLong(parts[0], out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[1], "D", out var id))
        {
            return false;
        }

        after = (new DateTime(ticks, DateTimeKind.Utc), id);
        return true;
    }
}

/// <summary><c>uw.Contingency.list</c> in-process for CHN and POL (D-ARC-16).</summary>
internal sealed class UnderwritingContingencyService(ContingencyQueries queries) : IUnderwritingContingencyService
{
    public async Task<ContingencyListPage> ListAsync(
        string? cursor = null, int? limit = null, string? termRef = null, string? policyRef = null, string? filters = null, string? audience = null,
        CancellationToken cancellationToken = default)
    {
        var result = await queries.ListAsync(termRef, policyRef, filters, audience, cursor, limit, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : throw new DomainException(result.Error);
    }
}

/// <summary>
/// One due-date pass: runs <see cref="SweepContingencies"/> for the stamp's legal entity as the service actor <c>uw-contingency-sweeper</c>,
/// in its own scope. The command marks each contingency once, so a repeated or concurrent pass is safe.
/// </summary>
internal sealed partial class ContingencySweepRunner(IServiceScopeFactory scopes, IOptions<StampOptions> stamp, ILogger<ContingencySweepRunner> logger)
{
    public const string ActorId = "uw-contingency-sweeper";

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        // TODO(hardening): one pass per legal entity once a catalogue of entities is available; today the stamp names the single one.
        var legalEntity = stamp.Value.LegalEntity is { Length: > 0 } code
            ? LegalEntityCode.Parse(code)
            : throw new InvalidOperationException("Stamp:LegalEntity is not configured.");
        var jurisdiction = Jurisdiction.Parse(stamp.Value.Country ?? throw new InvalidOperationException("Stamp:Country is not configured."));

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
            context.Actor = ActorRef.Service(ActorId);
            context.LegalEntity = legalEntity;
            context.Jurisdiction = jurisdiction;
            context.Origin = EventOrigin.Live;
            context.ConfigurationHash = await scope.ServiceProvider.GetRequiredService<IConfigurationResolver>().CurrentHashAsync(null, cancellationToken).ConfigureAwait(false);

            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<SweepContingencies, int>>();
            var result = await handler.HandleAsync(new SweepContingencies(), cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                LogFailed(logger, result.Error!.ToString());
                return 0;
            }

            if (result.Value > 0)
            {
                LogMarked(logger, result.Value);
            }

            return result.Value;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The contingency sweep failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The contingency sweep marked {Count} contingencies overdue.")]
    private static partial void LogMarked(ILogger logger, int count);
}

/// <summary>The hosted job: runs <see cref="ContingencySweepRunner"/> every <c>Underwriting:ContingencySweepIntervalSeconds</c> seconds (worker role only).</summary>
internal sealed partial class ContingencySweeper(ContingencySweepRunner runner, IOptions<UnderwritingOptions> options, ILogger<ContingencySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.ContingencySweepIntervalSeconds)));
        do
        {
            try
            {
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                CoreInsMetrics.SweeperSucceeded("uw-contingency");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
                CoreInsMetrics.SweeperFailed("uw-contingency");
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The contingency sweep failed; it runs again at the next tick.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}
