using System.Collections.Immutable;
using System.Threading.Tasks;
using Arch.Analyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Gate da Fase 0 (PLANO-implementacao.md): "confirme se TestState.AnalyzerConfigFiles
    /// existe na versão instalada de Microsoft.CodeAnalysis.CSharp.Analyzer.Testing; se não
    /// existir, use o plano B (SolutionTransforms + Solution.AddAnalyzerConfigDocument)".
    ///
    /// RESULTADO: <c>AnalyzerConfigFiles</c> EXISTE em <c>AnalyzerTest&lt;TVerifier&gt;.TestState</c>
    /// na 1.1.4 e sua mecânica de wiring está correta — confirmado tanto por reflexão
    /// (backing field "&lt;AnalyzerConfigFiles&gt;k__BackingField" em
    /// Microsoft.CodeAnalysis.Analyzer.Testing.dll) quanto por um repro isolado direto na
    /// API de Workspaces (fora do harness): <c>Solution.AddAnalyzerConfigDocument</c> +
    /// <c>Project.GetCompilationAsync()</c> + <c>CompilationWithAnalyzers.GetAnalyzerDiagnosticsAsync()</c>
    /// escalam ARCH0001 de Warning (default do descriptor) para Error corretamente quando
    /// nada mais mexe em <c>CompilationOptions.SpecificDiagnosticOptions</c>.
    ///
    /// ACHADO NÃO TRIVIAL (documentado aqui porque bloqueou o teste até ser isolado):
    /// <c>AnalyzerTest&lt;TVerifier&gt;.ApplyCompilationOptions</c> (chamado por
    /// <c>RunAsync()</c> DEPOIS de <c>SolutionTransforms</c>, para as duas abordagens — Plano A
    /// e Plano B correm nele igualmente) força
    /// <c>CompilationOptions.SpecificDiagnosticOptions[id] = ReportDiagnostic.Default</c> para
    /// TODO ID em <c>SupportedDiagnostics</c> habilitado por padrão. Nesta versão do Roslyn
    /// (4.8.0), uma entrada em <c>SpecificDiagnosticOptions</c> — mesmo com o valor sentinela
    /// <c>Default</c> — tem precedência sobre o <c>SyntaxTreeOptionsProvider</c> (o que o
    /// .editorconfig/.globalconfig alimenta), e portanto ANULA silenciosamente qualquer
    /// escalonamento de severidade vindo de <c>TestState.AnalyzerConfigFiles</c> — para as
    /// DUAS abordagens do gate, já que ambas passam pelo mesmo `ApplyCompilationOptions`. Ou
    /// seja: nem o plano A nem o plano B, usados "à mão-cheia" contra `RunAsync()`, bastam
    /// para testar escalonamento de severidade nesta versão do pacote — é preciso sobrescrever
    /// `ApplyCompilationOptions` (ver <see cref="ConfigAwareAnalyzerTest"/> abaixo) removendo a
    /// entrada forçada para o ID sob teste, para que o .editorconfig volte a valer.
    ///
    /// Isso é um detalhe do HARNESS de teste, não do analyzer real: a prova de ponta a ponta
    /// com `dotnet build` de verdade (tests/Arch.Spike.Consumer) já mostrou o .editorconfig
    /// funcionando sem nenhum workaround — é assim que um consumidor real vai usar a lib.
    /// </summary>
    public class AnalyzerConfigFilesTests
    {
        /// <summary>
        /// Variante de <see cref="CSharpAnalyzerTest{TAnalyzer, TVerifier}"/> que remove, após
        /// o comportamento padrão da base, a entrada forçada de <c>ARCH0001</c> em
        /// <c>SpecificDiagnosticOptions</c> — ver o comentário da classe acima. Sem isto, o
        /// teste de escalonamento de severidade via AnalyzerConfigFiles nunca veria o efeito
        /// do .editorconfig, independente de usar TestState.AnalyzerConfigFiles (plano A) ou
        /// SolutionTransforms + AddAnalyzerConfigDocument (plano B): os dois alimentam o mesmo
        /// Project, e é o ApplyCompilationOptions da classe base, chamado depois de ambos, que
        /// neutraliza a severidade vinda do .editorconfig.
        /// </summary>
        private sealed class ConfigAwareAnalyzerTest : CSharpAnalyzerTest<ArchRulesAnalyzer, DefaultVerifier>
        {
            protected override Project ApplyCompilationOptions(Project project)
            {
                var configured = base.ApplyCompilationOptions(project);
                var withoutForcedDefault = configured.CompilationOptions!.SpecificDiagnosticOptions.Remove("ARCH0001");
                return configured.WithCompilationOptions(
                    configured.CompilationOptions.WithSpecificDiagnosticOptions(withoutForcedDefault));
            }
        }

        private const string YamlWithForbiddenSuffix = "schema: arch-rules/v1\nforbiddenSuffix: Repository\n";

        private const string Source = @"
public class FooRepository
{
    public void Do() { }
}

public class Consumer
{
    public void M()
    {
        new FooRepository().Do();
    }
}
";

        /// <summary>
        /// Item de gate: ".editorconfig sobrescreve a severidade do slot" — replicado aqui
        /// via harness (além da prova em tests/Arch.Spike.Consumer, que usa um .nupkg e
        /// `dotnet build` de verdade). Severidade default de ARCH0001 é Warning
        /// (ArchDiagnostics.cs); o AnalyzerConfigFiles abaixo eleva para Error.
        /// </summary>
        [Fact]
        public async Task AnalyzerConfigFiles_Severity_Error_Escalates_Diagnostic()
        {
            var test = new ConfigAwareAnalyzerTest
            {
                TestState =
                {
                    Sources = { Source },
                },
            };

            test.TestState.AdditionalFiles.Add(("arch-rules.yaml", YamlWithForbiddenSuffix));
            test.TestState.AnalyzerConfigFiles.Add((
                "/0/.editorconfig",
                "root = true\n\n[*.cs]\ndotnet_diagnostic.ARCH0001.severity = error\n"));

            test.ExpectedDiagnostics.Add(
                DiagnosticResult.CompilerError("ARCH0001")
                    .WithSpan(11, 9, 11, 33)
                    .WithArguments("FooRepository", "Repository"));

            await test.RunAsync();
        }

        /// <summary>
        /// Sem nenhum AnalyzerConfigFiles, a severidade efetiva é a default do descriptor
        /// (Warning) — controle negativo do teste acima, provando que é o AnalyzerConfigFiles
        /// (não algo hardcoded) que muda a severidade.
        /// </summary>
        [Fact]
        public async Task Without_AnalyzerConfigFiles_Severity_Stays_Default_Warning()
        {
            var test = new CSharpAnalyzerTest<ArchRulesAnalyzer, DefaultVerifier>
            {
                TestState =
                {
                    Sources = { Source },
                },
            };

            test.TestState.AdditionalFiles.Add(("arch-rules.yaml", YamlWithForbiddenSuffix));

            test.ExpectedDiagnostics.Add(
                DiagnosticResult.CompilerWarning("ARCH0001")
                    .WithSpan(11, 9, 11, 33)
                    .WithArguments("FooRepository", "Repository"));

            await test.RunAsync();
        }

        /// <summary>
        /// Item de gate: "um .globalconfig de teste usando suggestion/silent (não info/hidden)
        /// é aceito". `suggestion`/`silent` são os únicos nomes válidos para
        /// dotnet_diagnostic.&lt;ID&gt;.severity (ADR-001 D3) — "info"/"hidden" são valores do
        /// enum DiagnosticSeverity, não do AnalyzerConfig, e um .globalconfig com eles seria
        /// rejeitado/ignorado pelo parser de config do próprio Roslyn.
        ///
        /// Formato .globalconfig real: cabeçalho `is_global = true`, sem seções `[glob]` (a
        /// diferença central para um .editorconfig comum).
        /// </summary>
        [Theory]
        [InlineData("suggestion", DiagnosticSeverity.Info)]
        [InlineData("silent", DiagnosticSeverity.Hidden)]
        public async Task GlobalConfig_Accepts_Suggestion_And_Silent_Severities(string severityName, DiagnosticSeverity expectedSeverity)
        {
            var test = new ConfigAwareAnalyzerTest
            {
                TestState =
                {
                    Sources = { Source },
                },
            };

            test.TestState.AdditionalFiles.Add(("arch-rules.yaml", YamlWithForbiddenSuffix));
            test.TestState.AnalyzerConfigFiles.Add((
                "/0/test.globalconfig",
                $"is_global = true\n\ndotnet_diagnostic.ARCH0001.severity = {severityName}\n"));

            test.ExpectedDiagnostics.Add(
                new DiagnosticResult("ARCH0001", expectedSeverity)
                    .WithSpan(11, 9, 11, 33)
                    .WithArguments("FooRepository", "Repository"));

            await test.RunAsync();
        }
    }
}
