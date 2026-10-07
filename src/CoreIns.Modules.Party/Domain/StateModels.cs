using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.StateMachines;

namespace CoreIns.Modules.Party.Domain;

/// <summary>Party status (PRD-01 §7.3, REQ-PTY-033). Stored and published as upper-snake codes.</summary>
internal enum PartyStatus
{
    Prospect,
    Active,
    Inactive,
    Deceased,
    Dissolved,
    Merged,
    Restricted,
    Anonymised,
}

/// <summary>Triggers of <see cref="PartyStatus"/>.</summary>
internal enum PartyTrigger
{
    FirstRole,
    Deactivate,
    Reactivate,
    RecordDeath,
    ReverseDeath,
    Dissolve,
    Merge,
    Unmerge,
    Restrict,
    LiftRestriction,
    Anonymise,
}

/// <summary>
/// The party lifecycle of PRD-01 §7.3 (D-CON-06: unmerge restores the prior status; Prospect → Merged allowed). SL-0
/// fires only the initial state; the table is complete so later work packages add transitions' guards, not states.
/// </summary>
internal static class PartyStateModel
{
    public static StateMachine<PartyStatus, PartyTrigger> Machine { get; } =
        StateMachine.Define<PartyStatus, PartyTrigger>(ModuleCode.PTY, "Party")
            .Initial(PartyStatus.Prospect, PartyStatus.Active)
            .Permit(PartyStatus.Prospect, PartyTrigger.FirstRole, PartyStatus.Active)
            .Permit(PartyStatus.Prospect, PartyTrigger.Anonymise, PartyStatus.Anonymised)
            .Permit(PartyStatus.Prospect, PartyTrigger.Merge, PartyStatus.Merged)
            .Permit(PartyStatus.Active, PartyTrigger.Deactivate, PartyStatus.Inactive)
            .Permit(PartyStatus.Inactive, PartyTrigger.Reactivate, PartyStatus.Active)
            .Permit(PartyStatus.Active, PartyTrigger.RecordDeath, PartyStatus.Deceased)
            .Permit(PartyStatus.Deceased, PartyTrigger.ReverseDeath, PartyStatus.Active)
            .Permit(PartyStatus.Active, PartyTrigger.Dissolve, PartyStatus.Dissolved)
            .Permit([PartyStatus.Active, PartyStatus.Inactive], PartyTrigger.Merge, PartyStatus.Merged)
            .Permit(PartyStatus.Merged, PartyTrigger.Unmerge, PartyStatus.Active)
            .Permit([PartyStatus.Active, PartyStatus.Inactive], PartyTrigger.Restrict, PartyStatus.Restricted)
            .Permit(PartyStatus.Restricted, PartyTrigger.LiftRestriction, PartyStatus.Active)
            .Permit([PartyStatus.Inactive, PartyStatus.Restricted, PartyStatus.Deceased], PartyTrigger.Anonymise, PartyStatus.Anonymised)
            // PRD-01 §7.3 gives Dissolved no way out (retention anonymisation of organisations is not stated): terminal.
            .Terminal(PartyStatus.Anonymised, PartyStatus.Dissolved)
            .Build();
}

/// <summary>Intermediary status (REQ-PTY-198).</summary>
internal enum IntermediaryStatus
{
    Onboarding,
    Active,
    Suspended,
    Terminated,
}

/// <summary>Triggers of <see cref="IntermediaryStatus"/>.</summary>
internal enum IntermediaryTrigger
{
    Activate,
    Suspend,
    Reinstate,
    Terminate,
}

/// <summary>Intermediary lifecycle Onboarding → Active ↔ Suspended → Terminated (REQ-PTY-198).</summary>
internal static class IntermediaryStateModel
{
    public static StateMachine<IntermediaryStatus, IntermediaryTrigger> Machine { get; } =
        StateMachine.Define<IntermediaryStatus, IntermediaryTrigger>(ModuleCode.PTY, "Intermediary")
            .Initial(IntermediaryStatus.Onboarding)
            .Permit(IntermediaryStatus.Onboarding, IntermediaryTrigger.Activate, IntermediaryStatus.Active)
            .Permit(IntermediaryStatus.Active, IntermediaryTrigger.Suspend, IntermediaryStatus.Suspended)
            .Permit(IntermediaryStatus.Suspended, IntermediaryTrigger.Reinstate, IntermediaryStatus.Active)
            .Permit([IntermediaryStatus.Active, IntermediaryStatus.Suspended], IntermediaryTrigger.Terminate, IntermediaryStatus.Terminated)
            .Terminal(IntermediaryStatus.Terminated)
            .Build();
}

/// <summary>Producer code status (REQ-PTY-204).</summary>
internal enum ProducerCodeStatus
{
    Active,
    Suspended,
    Terminated,
}

/// <summary>Upper-snake wire codes of the module's enums (stored in the database and published in events).</summary>
internal static class Codes
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    public static TEnum Parse<TEnum>(string code)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().First(value => string.Equals(Of(value), code, StringComparison.Ordinal));

    /// <summary>Check-constraint SQL listing every code of <typeparamref name="TEnum"/>.</summary>
    public static string CheckSql<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetValues<TEnum>().Select(v => $"'{Of(v)}'"))})";
}
