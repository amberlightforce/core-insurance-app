using System.Text.Json;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Domain;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Work.Queries;

/// <summary><c>wrk.Pattern.list</c> (BO-WRK-03): the queues and patterns in force, inactive patterns included.</summary>
internal sealed class PatternCatalogueReader(WorkCatalogue catalogue)
{
    public async Task<Result<PatternCatalogue>> ListAsync(CancellationToken cancellationToken) =>
        ToDto(await catalogue.GetAsync(cancellationToken).ConfigureAwait(false));

    internal static PatternCatalogue ToDto(CatalogueSnapshot snapshot) => new()
    {
        Queues = [.. snapshot.Queues.Select(q => new WorkQueueDefinition { Code = q.Code, Title = q.Title, Roles = q.Roles })],
        Patterns = [.. snapshot.Patterns.Select(p => new ActivityPatternDefinition
        {
            Code = p.Code, Version = p.Version, Title = p.Title, Category = p.Category, Priority = p.Priority, Queue = p.Queue, SlaPolicy = p.SlaPolicy,
            TargetMinutes = p.TargetMinutes, EscalationMinutes = p.EscalationMinutes, GateBlocking = p.GateBlocking, RaisingModules = p.RaisingModules,
            SuggestedAssignee = p.SuggestedAssignee,
            AmountBandMultipliers = p.AmountBandPercent is null ? null : JsonSerializer.SerializeToElement(p.AmountBandPercent, SharedKernelJson.Options),
            Active = p.Active,
        })],
    };
}
