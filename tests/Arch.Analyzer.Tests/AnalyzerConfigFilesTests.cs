using System;
using System.Collections.Generic;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using System.Threading.Tasks;
using Xunit;
using static Arch.Analyzer.Tests.TestConfigs;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Gate da Fase 0 (PLANO-implementacao.md), mantido vivo sobre o motor da Fase 1B: o
    /// `.editorconfig`/`.globalconfig` sobrescreve a severidade DO SLOT — que é a razão de existir do
    /// pool (ADR-001 §3, Opção C: override por regra individual, e não por tipo de regra).
    ///
    /// RESULTADO DO GATE: <c>TestState.AnalyzerConfigFiles</c> EXISTE em
    /// <c>AnalyzerTest&lt;TVerifier&gt;.TestState</c> na 1.1.4 e funciona — o plano B
    /// (`SolutionTransforms` + `Solution.AddAnalyzerConfigDocument`) não foi necessário.
    ///
    /// ACHADO NÃO TRIVIAL (documentado aqui porque bloqueou o teste até ser isolado):
    /// <c>AnalyzerTest&lt;TVerifier&gt;.ApplyCompilationOptions</c> (chamado por <c>RunAsync()</c>
    /// DEPOIS de <c>SolutionTransforms</c>, para as duas abordagens) força
    /// <c>CompilationOptions.SpecificDiagnosticOptions[id] = ReportDiagnostic.Default</c> para TODO
    /// ID habilitado por padrão em <c>SupportedDiagnostics</c>. Nesta versão do Roslyn (4.8.0), uma
    /// entrada em <c>SpecificDiagnosticOptions</c> — mesmo com o valor sentinela <c>Default</c> —
    /// tem precedência sobre o <c>SyntaxTreeOptionsProvider</c> (o que o .editorconfig alimenta), e
    /// portanto ANULA silenciosamente o escalonamento de severidade. É preciso sobrescrever
    /// <c>ApplyCompilationOptions</c> removendo a entrada forçada do ID sob teste.
    ///
    /// Isso é um detalhe do HARNESS, não do analyzer: a prova com `dotnet build` de verdade
    /// (tests/Arch.Spike.Consumer) mostrou o .editorconfig funcionando sem workaround nenhum.
    /// </summary>
    public class AnalyzerConfigFilesTests
    {
        private const string SlotUnderTest = "ARCH0001";
        private const string RuleId = "ACME-001";

        /// <summary>
        /// O conteúdo do YAML é irrelevante para ESTE teste (o parser real é da trilha 1A): o que
        /// importa é existir um <c>arch-rules.yaml</c> nos AdditionalFiles, porque é a presença dele
        /// que faz o motor registrar alguma ação.
        /// </summary>
        private const string Yaml = "schema: arch-rules/v1\n";

        private const string Source = @"
public class FooRepository
{
    public void Do() { }
}

public class OrderController
{
    public void M()
    {
        {|#0:new FooRepository().Do()|};
    }
}
";

        private static readonly string ExpectedMessage =
            TestForbiddenNameEvaluator.BuildMessage(
                Rule(RuleId, SlotUnderTest, TestForbiddenNameEvaluator.TypeName),
                "Repository",
                "FooRepository");

        /// <summary>
        /// Configuração equivalente ao que o YAML produzirá: duas camadas por sufixo e uma regra
        /// `Controller -> Repository` vinculada ao slot sob teste.
        /// </summary>
        private static ArchConfig PolicyConfig()
            => Policy(
                new[]
                {
                    Layer("Controller", new[] { NameSuffix("Controller") }),
                    Layer("Repository", new[] { NameSuffix("Repository") }),
                },
                new[]
                {
                    Rule(
                        RuleId,
                        SlotUnderTest,
                        TestForbiddenNameEvaluator.TypeName,
                        TestForbiddenNameEvaluator.Extra("Controller", "Repository")),
                });

        /// <summary>
        /// Variante que (a) injeta o analyzer com um avaliador de teste registrado e a config já
        /// parseada — a Fase 2 é quem entrega avaliadores reais — e (b) desfaz o
        /// <c>ApplyCompilationOptions</c> da classe base para o slot sob teste (ver o comentário da
        /// classe).
        /// </summary>
        private sealed class ConfigAwareAnalyzerTest : CSharpAnalyzerTest<ArchRulesAnalyzer, DefaultVerifier>
        {
            protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers()
            {
                return new DiagnosticAnalyzer[]
                {
                    new ArchRulesAnalyzer(
                        RuleEvaluatorRegistry.Create(new TestForbiddenNameEvaluator()),
                        Parser(PolicyConfig())),
                };
            }

            protected override Project ApplyCompilationOptions(Project project)
            {
                var configured = base.ApplyCompilationOptions(project);
                var withoutForcedDefault = configured.CompilationOptions!.SpecificDiagnosticOptions.Remove(SlotUnderTest);
                return configured.WithCompilationOptions(
                    configured.CompilationOptions.WithSpecificDiagnosticOptions(withoutForcedDefault));
            }
        }

        private static ConfigAwareAnalyzerTest CreateTest(string analyzerConfigPath = null, string analyzerConfigContent = null)
        {
            var test = new ConfigAwareAnalyzerTest
            {
                TestState =
                {
                    Sources = { Source },
                },
            };

            test.TestState.AdditionalFiles.Add(("arch-rules.yaml", Yaml));

            if (analyzerConfigPath != null)
            {
                test.TestState.AnalyzerConfigFiles.Add((analyzerConfigPath, analyzerConfigContent));
            }

            return test;
        }

        /// <summary>
        /// Item de gate: ".editorconfig sobrescreve a severidade do slot". O default do slot é
        /// Warning (ArchDiagnostics); o .editorconfig eleva para Error.
        /// </summary>
        [Fact]
        public async Task AnalyzerConfigFiles_Severity_Error_Escalates_Diagnostic()
        {
            var test = CreateTest(
                "/0/.editorconfig",
                "root = true\n\n[*.cs]\ndotnet_diagnostic." + SlotUnderTest + ".severity = error\n");

            test.ExpectedDiagnostics.Add(
                new DiagnosticResult(SlotUnderTest, DiagnosticSeverity.Error)
                    .WithLocation(0)
                    .WithArguments(ExpectedMessage));

            await test.RunAsync();
        }

        /// <summary>
        /// Controle negativo: sem nenhum AnalyzerConfigFiles, a severidade efetiva é a default do
        /// descriptor (Warning) — prova que é o .editorconfig, e não algo hardcoded, que decide.
        /// </summary>
        [Fact]
        public async Task Without_AnalyzerConfigFiles_Severity_Stays_Default_Warning()
        {
            var test = CreateTest();

            test.ExpectedDiagnostics.Add(
                new DiagnosticResult(SlotUnderTest, DiagnosticSeverity.Warning)
                    .WithLocation(0)
                    .WithArguments(ExpectedMessage));

            await test.RunAsync();
        }

        /// <summary>
        /// Item de gate: "um .globalconfig usando suggestion/silent (não info/hidden) é aceito".
        /// `suggestion`/`silent` são os únicos nomes válidos para
        /// `dotnet_diagnostic.&lt;ID&gt;.severity` (ADR-001 D3); "info"/"hidden" são valores do enum
        /// DiagnosticSeverity, não do AnalyzerConfig.
        ///
        /// Formato .globalconfig real: cabeçalho `is_global = true`, sem seções `[glob]`.
        /// </summary>
        [Theory]
        [InlineData("suggestion", DiagnosticSeverity.Info)]
        [InlineData("silent", DiagnosticSeverity.Hidden)]
        public async Task GlobalConfig_Accepts_Suggestion_And_Silent_Severities(string severityName, DiagnosticSeverity expectedSeverity)
        {
            var test = CreateTest(
                "/0/test.globalconfig",
                "is_global = true\n\ndotnet_diagnostic." + SlotUnderTest + ".severity = " + severityName + "\n");

            test.ExpectedDiagnostics.Add(
                new DiagnosticResult(SlotUnderTest, expectedSeverity)
                    .WithLocation(0)
                    .WithArguments(ExpectedMessage));

            await test.RunAsync();
        }
    }
}
