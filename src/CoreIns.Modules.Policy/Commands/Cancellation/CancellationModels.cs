using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Domain.Servicing;

namespace CoreIns.Modules.Policy.Commands.Cancellation;

/// <summary>The command wrapper (<c>pol.Cancellation.create</c>); the request and response are the generated contract types (SL3-CONTRACTS).</summary>
internal sealed record CancelPolicy(CancellationCreateRequest Request) : CoreIns.Platform.Commands.ICommand<CancellationCreateResponse>;

/// <summary>Parsing of the request's text fields.</summary>
internal static class CancellationText
{
    public const string NoticesNotSent = "NOT_SENT_DOC_NOT_BUILT";

    /// <summary>The domain kind of the contract's kind enum.</summary>
    public static Domain.CancellationKind Kind(CancellationKind kind) =>
        kind == CancellationKind.Flat ? Domain.CancellationKind.Flat : Domain.CancellationKind.Standard;

    /// <summary>The wire code of a refund method (PFC: PRO_RATA, FULL_REFUND).</summary>
    public static string Code(RefundMethod method) => Domain.Codes.Of(method);
}

/// <summary>
/// Refund method by cancellation source (REQ-POL-206). The term's pinned artefact (MOTOR-GR 1.1, SL3-PFC-MOTOR11) is the
/// authority; until PFC exposes it to POL this table carries its content: Policyholder → ProRata (commercial, ILLUSTRATIVE);
/// every other source is absent and the command fails closed.
/// </summary>
internal interface ICancellationRefundMethods
{
    /// <summary>The method for <paramref name="source"/>, or null when the product declares none (fail closed).</summary>
    RefundMethod? Resolve(string source);
}

/// <summary>Illustrative refund methods: only Policyholder → ProRata.</summary>
internal sealed class IllustrativeRefundMethods : ICancellationRefundMethods
{
    public RefundMethod? Resolve(string source) => source == Domain.CancellationSources.Policyholder ? RefundMethod.ProRata : null;
}
