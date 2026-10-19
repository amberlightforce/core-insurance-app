namespace CoreIns.CountryPacks.GR.Claims;

/// <summary>Configuration of the Greek Friendly Settlement stub (host section <c>FriendlySettlement:Stub</c>).</summary>
public sealed class FriendlySettlementStubOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "FriendlySettlement:Stub";

    /// <summary>Member-list entry that makes every code a member (E2E stacks, where the counterparty party ids are created at run time).</summary>
    public const string AnyMember = "*";

    /// <summary>Insurer code of our own legal entity on the clearing house member list.</summary>
    public string OwnInsurerCode { get; set; } = "OWN-INSURER";

    /// <summary>
    /// Organisation party ids (insurer codes) on the stub member list. Seeded by tests and the E2E stack.
    /// TODO(hardening): the real member list comes from the clearing agreement via the pack data, not configuration.
    /// </summary>
    public IList<string> MemberCodes { get; } = [];
}
