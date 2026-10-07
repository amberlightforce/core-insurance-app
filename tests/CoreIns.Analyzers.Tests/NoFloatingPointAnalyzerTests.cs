using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CoreIns.Analyzers.Tests;

public sealed class NoFloatingPointAnalyzerTests
{
    [Theory]
    [InlineData("public class C { public double Rate; }")]
    [InlineData("public class C { public float Rate { get; set; } }")]
    [InlineData("public class C { public decimal M(double x) => (decimal)x; }")]
    [InlineData("public class C { public decimal M() { var x = 1.5; return (decimal)x; } }")]
    [InlineData("public class C { public decimal M() => (decimal)System.Math.Sqrt(2); }")]
    [InlineData("public class C { public System.Collections.Generic.List<System.Double> Rates { get; } = new(); }")]
    [InlineData("public class C { public decimal M() => (decimal)(1 / 3f); }")]
    [InlineData("public class C { public System.Type T() => typeof(double); }")]
    [InlineData("public record R(double Amount);")]
    public async Task Floating_point_is_reported(string source)
    {
        var diagnostics = await AnalyzeAsync(source);

        diagnostics.ShouldNotBeEmpty();
        diagnostics.ShouldAllBe(d => d.Id == NoFloatingPointAnalyzer.DiagnosticId && d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("public class C { public decimal Rate; public decimal M(decimal x) => decimal.Round(x * 1.5m, 2); }")]
    [InlineData("public class C { public int Count; public long Total(int[] xs) { long t = 0; foreach (var x in xs) t += x; return t; } }")]
    [InlineData("public class C { public System.TimeSpan Wait() => System.TimeSpan.FromSeconds(30); }")]
    [InlineData("public class C { public string Text(decimal x) => x.ToString(System.Globalization.CultureInfo.InvariantCulture); }")]
    public async Task Decimal_and_integer_code_is_clean(string source)
    {
        var diagnostics = await AnalyzeAsync(source);

        diagnostics.ShouldBeEmpty();
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            "Probe",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty("the probe source must compile");

        return await compilation
            .WithAnalyzers([new NoFloatingPointAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }
}
