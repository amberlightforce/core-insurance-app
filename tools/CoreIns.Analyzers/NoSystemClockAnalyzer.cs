using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CoreIns.Analyzers;

/// <summary>
/// COREINS002: production code reads the current time only from the platform time service (<c>IClock</c>,
/// REQ-PLT-332, contract §3.9.13 "never system clock"). Reports the static clock reads <c>DateTime.Now</c>,
/// <c>DateTime.UtcNow</c>, <c>DateTime.Today</c>, <c>DateTimeOffset.Now</c> and <c>DateTimeOffset.UtcNow</c>.
/// Code that needs a BCL <see cref="System.TimeProvider"/> takes one by injection; the Platform registers a
/// <c>TimeProvider</c> backed by <c>IClock</c>. The one system-clock implementation suppresses the rule locally with a
/// justification; the architecture tests allow that suppression in exactly one file.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoSystemClockAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "COREINS002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Read the current time from IClock",
        messageFormat: "'{0}' reads the system clock; inject CoreIns.Platform.Time.IClock instead",
        category: "CoreIns.Time",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Contract §3.9.13 / REQ-PLT-332: current time only from the PLT time service, never the system clock.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var property = ((IPropertyReferenceOperation)context.Operation).Property;
        var type = property.ContainingType;
        if (type is null || type.ContainingNamespace?.ToDisplayString() != "System")
        {
            return;
        }

        var banned = type.Name switch
        {
            "DateTime" => property.Name is "Now" or "UtcNow" or "Today",
            "DateTimeOffset" => property.Name is "Now" or "UtcNow",
            _ => false,
        };

        if (banned)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation(), $"{type.Name}.{property.Name}"));
        }
    }
}
