using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>Contract status (PRD-08 §3 state machine, REQ-RI-056). Closed is reserved for the later close / commutation work.</summary>
internal enum ContractStatus
{
    Draft,
    PendingApproval,
    Approved,
    Active,
    Expired,
    Closed,
}

/// <summary>Triggers of <see cref="ContractStatus"/>.</summary>
internal enum ContractTrigger
{
    Submit,
    Return,
    Approve,
    Activate,
    Expire,
}

/// <summary>Draft → PendingApproval → Approved → Active → Expired, and PendingApproval → Draft (returned with a reason).</summary>
internal static class ContractStateModel
{
    public static StateMachine<ContractStatus, ContractTrigger> Machine { get; } =
        StateMachine.Define<ContractStatus, ContractTrigger>(ModuleCode.RI, "Contract")
            .Initial(ContractStatus.Draft)
            .Permit(ContractStatus.Draft, ContractTrigger.Submit, ContractStatus.PendingApproval)
            .Permit(ContractStatus.PendingApproval, ContractTrigger.Return, ContractStatus.Draft)
            .Permit(ContractStatus.PendingApproval, ContractTrigger.Approve, ContractStatus.Approved)
            .Permit(ContractStatus.Approved, ContractTrigger.Activate, ContractStatus.Active)
            .Permit(ContractStatus.Active, ContractTrigger.Expire, ContractStatus.Expired)
            .Terminal(ContractStatus.Expired, ContractStatus.Closed)
            .Build();

    /// <summary>The wire / column code: <c>PENDING_APPROVAL</c>.</summary>
    public static string Code(ContractStatus status) =>
        string.Concat(status.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    /// <summary>Parses a column code.</summary>
    public static ContractStatus Parse(string code) =>
        Enum.GetValues<ContractStatus>().First(status => string.Equals(Code(status), code, StringComparison.Ordinal));
}
