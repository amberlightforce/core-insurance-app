using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CoreIns.Analyzers.Tests;

/// <summary>COREINS002: the current time comes only from IClock (REQ-PLT-332).</summary>
public sealed class NoSystemClockAnalyzerTests
{
    [Theory]
    [InlineData("public class C { public System.DateTime M() => System.DateTime.Now; }")]
    [InlineData("public class C { public System.DateTime M() => System.DateTime.UtcNow; }")]
    [InlineData("public class C { public System.DateTime M() => System.DateTime.Today; }")]
    [InlineData("public class C { public System.DateTimeOffset M() => System.DateTimeOffset.Now; }")]
    [InlineData("public class C { public System.DateTimeOffset M() => System.DateTimeOffset.UtcNow; }")]
    [InlineData("using System; public class C { public long M() { var t = DateTime.UtcNow; return t.Ticks; } }")]
    public async Task System_clock_reads_are_reported(string source)
    {
        var diagnostics = await AnalyzeAsync(source);

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe(NoSystemClockAnalyzer.DiagnosticId);
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("public class C { public System.DateTime M(System.DateTime d) => d.AddDays(1); }")]
    [InlineData("public class C { public System.DateTime M() => System.DateTime.UnixEpoch; }")]
    [InlineData("public class C { public System.DateTimeOffset M(System.TimeProvider p) => p.GetUtcNow(); }")]
    [InlineData("public class C { public System.Guid M() => System.Guid.CreateVersion7(); }")]
    [InlineData("public class C { public long M() => System.Diagnostics.Stopwatch.GetTimestamp(); }")]
    [InlineData("public class Now { public static int UtcNow => 1; public int M() => UtcNow; }")]
    public async Task Other_time_code_is_clean(string source)
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
            .WithAnalyzers([new NoSystemClockAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
    }
}
