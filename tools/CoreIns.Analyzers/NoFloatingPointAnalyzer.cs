using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CoreIns.Analyzers;

/// <summary>
/// COREINS001: production code never uses floating point (System.Double / System.Single). Money, rates and
/// percentages are decimal (ARCHITECTURE-DECISIONS §2 rule 2). Reports declarations (fields, properties, method
/// signatures, locals), expressions (literals, conversions, calls returning double) and generic arguments.
/// Generated code is not analysed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoFloatingPointAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "COREINS001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Floating point is forbidden in production code",
        messageFormat: "'{0}' uses floating point ({1}); use decimal for money, rates and percentages",
        category: "CoreIns.Money",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "ARCHITECTURE-DECISIONS §2 rule 2: no double or float anywhere in production code.");

    private static readonly OperationKind[] AllOperationKinds = Enum.GetValues(typeof(OperationKind))
        .Cast<OperationKind>()
        .Where(kind => kind != OperationKind.None)
        .Distinct()
        .ToArray();

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(AnalyzeOperation, AllOperationKinds);
        context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.Field, SymbolKind.Property, SymbolKind.Method, SymbolKind.Event);
    }

    private static void AnalyzeOperation(OperationAnalysisContext context)
    {
        var operation = context.Operation;
        switch (operation)
        {
            case ITypeOfOperation typeOf:
                Report(context, operation, typeOf.TypeOperand);
                return;
            case IVariableDeclaratorOperation declarator:
                Report(context, operation, declarator.Symbol.Type);
                return;
        }

        // Report only the outermost floating-point expression of a chain (e.g. `a * 1.5`, not also `1.5`).
        if (operation.Parent is { } parent && FloatingPointIn(parent.Type) is not null)
        {
            return;
        }

        Report(context, operation, operation.Type);
    }

    private static void Report(OperationAnalysisContext context, IOperation operation, ITypeSymbol? type)
    {
        if (FloatingPointIn(type) is { } found)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, operation.Syntax.GetLocation(), Shorten(operation.Syntax.ToString()), found));
        }
    }

    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        var symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared || symbol.Locations.Length == 0 || !symbol.Locations[0].IsInSource)
        {
            return;
        }

        var types = symbol switch
        {
            IFieldSymbol field => new[] { field.Type },
            IPropertySymbol property => property.Parameters.Select(p => p.Type).Prepend(property.Type).ToArray(),
            IEventSymbol @event => new[] { @event.Type },
            IMethodSymbol method when method.AssociatedSymbol is null =>
                method.Parameters.Select(p => p.Type).Prepend(method.ReturnType).ToArray(),
            _ => Array.Empty<ITypeSymbol>(),
        };

        var found = types.Select(FloatingPointIn).FirstOrDefault(name => name is not null);
        if (found is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, symbol.Locations[0], symbol.Name, found));
        }
    }

    /// <summary>Returns "double" or "float" when the type is, or contains, a floating-point type; otherwise null.</summary>
    private static string? FloatingPointIn(ITypeSymbol? type) => type switch
    {
        null => null,
        { SpecialType: SpecialType.System_Double } => "double",
        { SpecialType: SpecialType.System_Single } => "float",
        IArrayTypeSymbol array => FloatingPointIn(array.ElementType),
        IPointerTypeSymbol pointer => FloatingPointIn(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.Select(FloatingPointIn).FirstOrDefault(name => name is not null),
        _ => null,
    };

    private static string Shorten(string text) => text.Length <= 60 ? text : text.Substring(0, 57) + "...";
}
