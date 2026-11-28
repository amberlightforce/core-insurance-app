namespace CoreIns.Modules.Work.Persistence;

/// <summary>A work queue (<c>wrk.queue</c>): global to the deployment, not per legal entity.</summary>
internal sealed class QueueRow
{
    public required string Code { get; set; }

    public required string Title { get; set; }

    /// <summary>Comma separated roles that may see and claim from the queue.</summary>
    public required string Roles { get; set; }
}

/// <summary>An activity pattern (<c>wrk.activity_pattern</c>); one row per code, <see cref="Version"/> counts the edits.</summary>
internal sealed class PatternRow
{
    public required string Code { get; set; }

    public required string Version { get; set; }

    public required string Title { get; set; }

    public required string Category { get; set; }

    public required string Priority { get; set; }

    public required string QueueCode { get; set; }

    public required string SlaPolicy { get; set; }

    public int TargetMinutes { get; set; }

    public int EscalationMinutes { get; set; }

    public bool GateBlocking { get; set; }

    /// <summary>Comma separated module codes that may raise the pattern.</summary>
    public required string RaisingModules { get; set; }

    public string? SuggestedAssignee { get; set; }

    /// <summary>JSON object {band: percent}; null for none.</summary>
    public string? AmountBandPercent { get; set; }

    public bool Active { get; set; } = true;
}
