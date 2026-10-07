using Mono.Cecil;
using NetArchTest.Rules;

namespace CoreIns.ArchitectureTests;

/// <summary>
/// NetArchTest rule: the assembly containing the type references no type or assembly of another module's
/// implementation. References to another module's <c>.Contracts</c> are allowed.
/// (NetArchTest's built-in dependency rules match by namespace prefix, so they cannot tell
/// <c>CoreIns.Modules.Billing</c> from <c>CoreIns.Modules.Billing.Contracts</c>.)
/// </summary>
internal sealed class NoOtherModuleImplementationRule(string ownModule) : ICustomRule
{
    public List<string> Violations { get; } = [];

    public bool MeetsRule(TypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var module = type.Module;

        var offending = module.AssemblyReferences
            .Select(reference => reference.Name)
            .Concat(module.GetTypeReferences().Select(reference => reference.Namespace))
            .Where(name => SolutionModel.IsOtherModuleImplementation(name, ownModule))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var name in offending.Where(name => !Violations.Contains(name)))
        {
            Violations.Add(name);
        }

        return offending.Count == 0;
    }
}
