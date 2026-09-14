using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Arch.TestHarness.SelfTests
{
    /// <summary>
    /// Prova, sem depender de nenhum analyzer de <c>src/**</c>, que
    /// <see cref="ConfigAwareAnalyzerTest{TAnalyzer, TVerifier}"/> compila e faz o que promete:
    /// restaura o efeito do .editorconfig/.globalconfig de teste sobre a severidade de um
    /// diagnóstico, contornando a armadilha de <c>ApplyCompilationOptions</c> documentada na
    /// classe base. Replica, de forma reduzida e genérica, o gate original da Fase 0
    /// (<c>tests/Arch.Analyzer.Tests/AnalyzerConfigFilesTests.cs</c>).
    /// </summary>
    public class ConfigAwareAnalyzerTestSelfTests
    {
        private const string Source = "public class Foo { }\n";

        [Fact]
        public async Task EditorConfig_Escalates_Severity_To_Error()
        {
            var test = new ConfigAwareAnalyzerTest<EmptyClassAnalyzer, DefaultVerifier>
            {
                TestState = { Sources = { Source } },
            };

            test.TestState.AnalyzerConfigFiles.Add((
                "/0/.editorconfig",
                "root = true\n\n[*.cs]\ndotnet_diagnostic.HARNESS001.severity = error\n"));

            test.ExpectedDiagnostics.Add(
                DiagnosticResult.CompilerError(EmptyClassAnalyzer.DiagnosticId)
                    .WithSpan(1, 14, 1, 17)
                    .WithArguments("Foo"));

            await test.RunAsync();
        }

        /// <summary>
        /// Controle negativo: sem AnalyzerConfigFiles, a severidade efetiva volta a ser a
        /// default do descriptor (Warning) — prova que é o .editorconfig, e não algo
        /// hardcoded, quem determina a severidade no teste acima.
        /// </summary>
        [Fact]
        public async Task Without_EditorConfig_Severity_Stays_Default_Warning()
        {
            var test = new ConfigAwareAnalyzerTest<EmptyClassAnalyzer, DefaultVerifier>
            {
                TestState = { Sources = { Source } },
            };

            test.ExpectedDiagnostics.Add(
                DiagnosticResult.CompilerWarning(EmptyClassAnalyzer.DiagnosticId)
                    .WithSpan(1, 14, 1, 17)
                    .WithArguments("Foo"));

            await test.RunAsync();
        }
    }
}
