using System;
using System.Collections.Generic;
using System.Linq;
using CoreIns.Rules.Syntax;

namespace CoreIns.Rules;

/// <summary>
/// A typed, pure function supplied by the host (for example a country pack's rounding rule bound over a configuration
/// snapshot: <c>mkt.round(amount, "charge.line")</c>). The implementation MUST be pure: no I/O, clock or randomness,
/// and the same arguments must always give the same result. Names may be qualified with dots.
/// </summary>
public sealed class HostFunction
{
    /// <summary>Creates a host function.</summary>
    public HostFunction(string name, IReadOnlyList<RuleType> parameters, RuleType returnType, Func<IReadOnlyList<RuleValue>, RuleValue> implementation)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(returnType);
        ArgumentNullException.ThrowIfNull(implementation);
        var segments = name.Split('.');
        if (segments.Any(s => !Identifiers.IsValid(s)))
        {
            throw new ArgumentException($"'{name}' is not a valid function name", nameof(name));
        }

        if (RuleLanguage.AllBuiltinNames.Contains(name) || RuleLanguage.NonDeterministicNames.Contains(name) || RuleLanguage.UnsupportedCelFunctions.Contains(name))
        {
            throw new ArgumentException($"'{name}' collides with a built-in or reserved function name", nameof(name));
        }

        Name = name;
        Parameters = parameters.ToArray();
        ReturnType = returnType;
        Implementation = implementation;
    }

    /// <summary>Function name, possibly qualified (<c>mkt.round</c>).</summary>
    public string Name { get; }

    /// <summary>Parameter types.</summary>
    public IReadOnlyList<RuleType> Parameters { get; }

    /// <summary>Return type.</summary>
    public RuleType ReturnType { get; }

    /// <summary>The pure implementation.</summary>
    public Func<IReadOnlyList<RuleValue>, RuleValue> Implementation { get; }
}
