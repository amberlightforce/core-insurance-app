namespace CoreIns.Modules.Market.Contracts.Spi;

// Shared SPI vocabulary (contracts/openapi/spi.md "Conventions"; PRD-17 §9.4.0, REQ-MKT-002).
// SPIs are C# interfaces declared in the core and implemented by country packs. Callers obtain the implementation
// bound for their legal entity, axis value and date through mkt.Spi.bind and never reference a pack.

/// <summary>Error categories used by every SPI (spi.md "Conventions"). The calling module maps them to its own errors.</summary>
public enum SpiErrorCategory
{
    /// <summary>The input is invalid for the bound rules (for example <c>CHECK_DIGIT</c>, <c>FORMAT</c>, <c>LENGTH</c>).</summary>
    Validation,

    /// <summary>No rule, rate or value exists for the input date (fail closed).</summary>
    RuleMissing,

    /// <summary>The operation or scheme is not bound or does not apply.</summary>
    NotApplicable,

    /// <summary>An external dependency is unavailable (asynchronous SPIs queue).</summary>
    Unavailable,

    /// <summary>The call exceeded its timeout.</summary>
    Timeout,

    /// <summary>The implementation broke the SPI contract (for example an inconsistent treatment result).</summary>
    ContractViolation,
}

/// <summary>One typed SPI error: category plus a stable machine code (for example <c>POSTCODE_FORMAT</c>).</summary>
/// <param name="Category">Error category.</param>
/// <param name="Code">Stable upper-snake code defined by the SPI specification.</param>
/// <param name="Field">Optional input field the error refers to.</param>
public sealed record SpiError(SpiErrorCategory Category, string Code, string? Field = null);

/// <summary>
/// Thrown by an SPI implementation when an operation fails as a whole (fail closed). Results that carry an
/// <c>errors[]</c> list (for example <c>IdValidator.validate</c>) report validation problems in the result instead.
/// </summary>
public sealed class SpiException : Exception
{
    public SpiException()
    {
        Error = new SpiError(SpiErrorCategory.ContractViolation, "UNSPECIFIED");
    }

    public SpiException(string message)
        : base(message)
    {
        Error = new SpiError(SpiErrorCategory.ContractViolation, "UNSPECIFIED");
    }

    public SpiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = new SpiError(SpiErrorCategory.ContractViolation, "UNSPECIFIED");
    }

    public SpiException(SpiError error, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>The typed error.</summary>
    public SpiError Error { get; }

    /// <summary>Shortcut for <see cref="SpiError.Category"/>.</summary>
    public SpiErrorCategory Category => Error.Category;
}

/// <summary>
/// Legal status of a regulatory value held in pack configuration (D-REG-01, REQ-MKT-343). Production activation
/// refuses non-Settled motor-path values (D-REG-02).
/// </summary>
public enum LegalStatus
{
    Settled,
    Unverified,
    Draft,

    /// <summary>Not a statutory, tax or clock rule (core defaults, currency roles); servable in Production.</summary>
    NotRegulatory,

    /// <summary>Awaiting the D2 tax and legal opinion (PRD-17 index: Pending opinion, OI-MKT-19).</summary>
    PendingOpinion,

    /// <summary>Source to be verified (PRD-17 index: Verify).</summary>
    Verify,

    /// <summary>Applicability uncertain (PRD-17 index: Uncertain).</summary>
    Uncertain,

    /// <summary>Market practice, no legal source (PRD-17 index: Market practice).</summary>
    MarketPractice,
}

/// <summary>A money amount in a currency (ISO 4217). Money is always decimal (ADR §2 rule 2).</summary>
/// <remarks>Local to the SPI contracts until CoreIns.SharedKernel publishes <c>Money</c> (F-1b); then replaced.</remarks>
/// <param name="Amount">Amount in major units.</param>
/// <param name="Currency">ISO 4217 alphabetic code.</param>
public readonly record struct SpiMoney(decimal Amount, string Currency);
