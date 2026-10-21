using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Channels.Contracts.Api;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Platform.Ai;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Channels.Ai;

/// <summary>States of a confirmation ticket.</summary>
internal static class TicketStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";

    public static readonly string[] All = [Pending, Approved, Rejected];
}

/// <summary>A confirmation ticket raised by an AI agent for a state-changing tool call; only a human decides it.</summary>
internal sealed class TicketRow
{
    public Guid TicketId { get; set; }

    public LegalEntityId LegalEntityId { get; set; }

    public string Tool { get; set; } = string.Empty;

    /// <summary>The agent's arguments as sent (jsonb).</summary>
    public string Arguments { get; set; } = "{}";

    public string Status { get; set; } = TicketStatuses.Pending;

    public Guid? InteractionId { get; set; }

    public string RequestedBy { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    public Instant ExpiresAt { get; set; }

    public Instant? DecidedAt { get; set; }

    public string? DecidedBy { get; set; }

    public string? ExecutedCommandRef { get; set; }
}

/// <summary>The MCP tool catalogue of this slice: a small subset of PRD-12 section 9.1.4 (class, whether a human ticket is needed).</summary>
internal static class AgentTools
{
    public const string FeatureId = "AI-CHN-02";
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(24);

    public static readonly (string Tool, string Cls, bool TicketRequired)[] Catalogue =
    [
        ("product_info", "READ", false),
        ("quote_motor_dry_run", "DRY_RUN", false),
        ("bind_policy", "STATE_CHANGING", true),
        ("cancel_policy", "STATE_CHANGING", true),
    ];
}

/// <summary>
/// Checks the step-up proof of a ticket approval: <c>{ "ticketId", "subject", "method", "issuedAt" }</c>, bound to the ticket and the deciding
/// person and no older than <see cref="MaxAge"/>.
/// </summary>
internal static class StepUp
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private static readonly string[] Methods = ["mfa", "webauthn", "totp"];

    /// <summary>Null when the proof is valid, otherwise the reason it is not.</summary>
    public static string? Verify(JsonElement? proof, Guid ticketId, ActorRef actor, Instant now)
    {
        if (proof is not { ValueKind: JsonValueKind.Object } p)
        {
            return "Approving a ticket needs a step-up proof.";
        }

        if (!p.TryGetProperty("ticketId", out var t) || t.ValueKind != JsonValueKind.String || !Guid.TryParse(t.GetString(), out var proofTicket) || proofTicket != ticketId)
        {
            return "The step-up proof is for another ticket.";
        }

        if (!p.TryGetProperty("subject", out var s) || s.ValueKind != JsonValueKind.String || !string.Equals(s.GetString(), actor.ToString(), StringComparison.Ordinal))
        {
            return "The step-up proof was not given by the person deciding.";
        }

        if (!p.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String || !Methods.Contains(m.GetString(), StringComparer.Ordinal))
        {
            return "The step-up proof names no accepted method.";
        }

        if (!p.TryGetProperty("issuedAt", out var i) || i.ValueKind != JsonValueKind.String
            || !System.DateTimeOffset.TryParse(i.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var issued))
        {
            return "The step-up proof has no issue time.";
        }

        var age = now.ToDateTimeOffset() - issued;

        // TODO(hardening): the proof is a client assertion; verify an IdP-signed step-up (acr/amr claim or a signed challenge) instead.
        return age < TimeSpan.FromSeconds(-30) || age > MaxAge ? "The step-up proof has expired or is from the future." : null;
    }
}

/// <summary>Ticket reads and decisions (chn.Ticket.get, approve, reject). Never gated by the AI switch: a human can always decide a pending ticket.</summary>
internal sealed class TicketService(ChannelsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock)
{
    public LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    public async Task<TicketRow> CreatePendingAsync(string tool, JsonElement arguments, Guid? interactionId, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var row = new TicketRow
        {
            TicketId = Guid.CreateVersion7(),
            LegalEntityId = LegalEntity,
            Tool = tool,
            Arguments = arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText(),
            Status = TicketStatuses.Pending,
            InteractionId = interactionId,
            RequestedBy = context.Actor.ToString(),
            CreatedAt = now,
            ExpiresAt = now.Plus(AgentTools.TicketLifetime),
        };
        db.Tickets.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    public async Task<TicketRow?> FindAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        return await db.Tickets.FirstOrDefaultAsync(t => t.TicketId == ticketId && t.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Approves or rejects a pending ticket. Idempotent for a repeat of the same decision; the opposite decision is stale.</summary>
    public async Task<Result<TicketRow>> DecideAsync(Guid ticketId, bool approve, JsonElement? stepUpProof, CancellationToken cancellationToken)
    {
        // Only a person decides (REQ-CHN-269).
        if (context.Actor.Kind != ActorKind.User)
        {
            return DomainError.Of(ModuleCode.CHN, "HUMAN-REQUIRED", "Only a person can decide a ticket.");
        }

        var row = await FindAsync(ticketId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "The ticket does not exist in your legal entity.");
        }

        // TODO(hardening): refuse a decider equal to the delegating person of the agent that raised the ticket (RequestedBy holds only the agent).
        // An approval needs a fresh step-up proof bound to this ticket and to the person deciding. A repeat of the decision already
        // taken is idempotent and needs none. Rejecting is the safe direction and needs no proof.
        if (approve && row.Status != TicketStatuses.Approved)
        {
            var problem = StepUp.Verify(stepUpProof, row.TicketId, context.Actor, clock.Now);
            if (problem is not null)
            {
                return DomainError.Of(ModuleCode.CHN, "STEP-UP-REQUIRED", problem);
            }
        }

        var target = approve ? TicketStatuses.Approved : TicketStatuses.Rejected;
        if (row.Status == target)
        {
            return row;
        }

        if (row.Status != TicketStatuses.Pending)
        {
            return DomainError.Of(ModuleCode.CHN, "TICKET-STALE", $"The ticket was already {row.Status}.");
        }

        var now = clock.Now;
        if (now > row.ExpiresAt)
        {
            return DomainError.Of(ModuleCode.CHN, "TICKET-EXPIRED", "The ticket has expired.");
        }

        row.Status = target;
        row.DecidedAt = now;
        row.DecidedBy = context.Actor.ToString();

        // TODO(hardening): on approval execute the owning module's command with the agent's arguments and record its reference.
        row.ExecutedCommandRef = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    public static object View(TicketRow row) => new
    {
        ticketId = row.TicketId,
        tool = row.Tool,
        status = row.Status,
        decision = row.Status == TicketStatuses.Pending ? null : row.Status,
        executedCommandRef = row.ExecutedCommandRef,
        expiresAt = row.ExpiresAt,
    };
}

/// <summary><c>chn.Agent.mcp</c> (AI-CHN-02): one JSON-RPC endpoint for the MCP methods, behind the AI gate (SL6-AI-FEATURES).</summary>
[ApiController]
[Route("api/chn/v1/agent")]
internal sealed class AgentMcpController : ControllerBase
{
    /// <summary>Disabled: 503 PLT-ERR-AI-DISABLED. Pending tickets stay decidable by a human on chn.Ticket.*.</summary>
    [HttpPost("mcp")]
    [SkipIdempotency]
    [Authorize(Policy = "chn.Agent.mcp")]
    public async Task<IResult> McpAsync([FromBody] AgentMcpRequest request, [FromServices] TicketService tickets, CancellationToken cancellationToken)
    {
        var inputs = new JsonObject { ["method"] = request.Method.ToString(), ["params"] = request.Params is { } p ? JsonNode.Parse(p.GetRawText()) : null };
        var ai = await AiAssist.InvokeAsync(HttpContext, AgentTools.FeatureId, inputs, cancellationToken).ConfigureAwait(false);
        if (ai.Refusal is { } refusal)
        {
            return refusal;
        }

        var interactionId = ai.Response!.InteractionId;
        switch (request.Method)
        {
            case AgentMcpRequest.MethodValue.ToolsList:
                return Rpc(request, new
                {
                    tools = AgentTools.Catalogue.Select(t => new { name = t.Tool, @class = t.Cls, ticketRequired = t.TicketRequired }).ToArray(),
                    interactionId,
                });
            case AgentMcpRequest.MethodValue.ToolsCall:
                return await CallAsync(request, tickets, interactionId, cancellationToken).ConfigureAwait(false);
            default:
                // TODO(hardening): resources/read serves the PRD resource catalogue; none are published yet.
                return Rpc(request, new { contents = Array.Empty<object>(), interactionId });
        }
    }

    private static async Task<IResult> CallAsync(AgentMcpRequest request, TicketService tickets, Guid interactionId, CancellationToken cancellationToken)
    {
        var parameters = request.Params;
        var name = parameters is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var tool = AgentTools.Catalogue.FirstOrDefault(t => t.Tool == name);
        if (tool.Tool is null)
        {
            return RpcError(request, -32602, "Unknown tool.");
        }

        var arguments = parameters!.Value.TryGetProperty("arguments", out var a) ? a : default;
        if (!tool.TicketRequired)
        {
            // TODO(hardening): READ/DRY_RUN tools call the owning module's operation; the deterministic stub echoes the call.
            return Rpc(request, new { tool = tool.Tool, result = new { stub = true }, interactionId });
        }

        var ticket = await tickets.CreatePendingAsync(tool.Tool, arguments, interactionId, cancellationToken).ConfigureAwait(false);
        return Rpc(request, new { tool = tool.Tool, ticket = TicketService.View(ticket), interactionId });
    }

    private static IResult Rpc(AgentMcpRequest request, object result) => Results.Ok(new { jsonrpc = "2.0", id = request.Id, result });

    private static IResult RpcError(AgentMcpRequest request, int code, string message) => Results.Ok(new { jsonrpc = "2.0", id = request.Id, error = new { code, message } });
}

/// <summary><c>chn.Ticket.get|approve|reject</c> (REQ-CHN-269, -270): humans decide tickets; the AI switch does not apply.</summary>
[ApiController]
[Route("api/chn/v1/tickets")]
internal sealed class TicketsController : ControllerBase
{
    [HttpGet("{id}")]
    [Authorize(Policy = "chn.Ticket.get")]
    public async Task<IResult> GetAsync(string id, [FromServices] TicketService tickets, CancellationToken cancellationToken)
    {
        var row = Guid.TryParse(id, out var guid) ? await tickets.FindAsync(guid, cancellationToken).ConfigureAwait(false) : null;
        return row is null ? Problem(DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "The ticket does not exist in your legal entity.")) : Results.Ok(TicketService.View(row));
    }

    [HttpPost("approve")]
    [Authorize(Policy = "chn.Ticket.approve")]
    public Task<IResult> ApproveAsync([FromBody] TicketApproveRequest request, [FromServices] TicketService tickets, CancellationToken cancellationToken) =>
        DecideAsync(request.TicketId, true, request.StepUpProof, tickets, cancellationToken);

    [HttpPost("reject")]
    [Authorize(Policy = "chn.Ticket.reject")]
    public Task<IResult> RejectAsync([FromBody] TicketRejectRequest request, [FromServices] TicketService tickets, CancellationToken cancellationToken) =>
        DecideAsync(request.TicketId, false, null, tickets, cancellationToken);

    private async Task<IResult> DecideAsync(Guid? ticketId, bool approve, JsonElement? stepUpProof, TicketService tickets, CancellationToken cancellationToken)
    {
        if (ticketId is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ticketId"] = ["Required."] });
        }

        var result = await tickets.DecideAsync(ticketId.Value, approve, stepUpProof, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(TicketService.View(result.Value)) : Problem(result.Error);
    }

    private IResult Problem(DomainError error) => HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, HttpContext);
}
