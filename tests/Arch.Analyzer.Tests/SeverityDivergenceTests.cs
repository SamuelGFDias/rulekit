using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Arch.TestHarness;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Issue #8: ARCH9005 (info) quando a severidade DECLARADA no YAML para uma regra diverge da
    /// severidade EFETIVA do slot na compilação (ADR-001 D3) — sinal de que `arch-rules gen` não foi
    /// reexecutado. Efetiva = <c>SpecificDiagnosticOptions</c> (/warnaserror:ID, /nowarn, ruleset) →
    /// <c>.globalconfig</c> (<c>SyntaxTreeOptionsProvider.TryGetGlobalDiagnosticValue</c>) → default
    /// do descriptor do slot (Warning). <c>.editorconfig</c> por árvore NÃO é considerado.
    ///
    /// Parser e avaliadores reais; o projeto é montado num workspace para que o <c>.globalconfig</c>
    /// chegue à compilação como num build real (<see cref="AnalyzerConfigWorkspaceRunner"/>).
    /// </summary>
    public class SeverityDivergenceTests
    {
        private const string Slot = "ARCH0011";
        private const string YamlPath = "/0/arch-rules.yaml";

        private const string Source = @"
public class OrderRepository { public static void Load() { } }
public class OrderController { public void Get() { OrderRepository.Load(); } }
";

        private static string Yaml(string severitiesBlock, string ruleSeverityLine, bool enabled = true) => @"schema: arch-rules/v1
layers:
  Controller:
    match:
      - nameSuffix: Controller
  Repository:
    match:
      - nameSuffix: Repository
" + severitiesBlock + @"rules:
  - id: OUTRA-001
    slot: ARCH0012
    type: forbidden-call
    from: Repository
    to: Controller
  - id: ACME-001
    slot: " + Slot + @"
    type: forbidden-call
    from: Controller
    to: Repository
" + ruleSeverityLine + (enabled ? string.Empty : "    enabled: false\n");

        private const string ErrorPolicy = "severities:\n  restrita: error\n  media: warning\ndefaults:\n  severity: media\n";

        private static string GlobalConfig(string value)
            => "is_global = true\n\ndotnet_diagnostic." + Slot + ".severity = " + value + "\n";

        private static async Task<ImmutableArray<Diagnostic>> RunAsync(
            string yaml,
            string globalConfig = null,
            IReadOnlyDictionary<string, ReportDiagnostic> specificOptions = null)
        {
            var diagnostics = await AnalyzerConfigWorkspaceRunner.RunAsync(
                new ArchRulesAnalyzer(),
                new[] { ("Sample.cs", Source) },
                additionalFiles: new[] { (YamlPath, yaml) },
                analyzerConfigFiles: globalConfig == null ? null : new[] { ("/0/arch-rules.globalconfig", globalConfig) },
                specificDiagnosticOptions: specificOptions);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
            return diagnostics;
        }

        private static Diagnostic[] Arch9005(ImmutableArray<Diagnostic> diagnostics)
            => diagnostics.Where(d => d.Id == "ARCH9005").ToArray();

        [Fact]
        public async Task GlobalConfig_coerente_com_o_YAML_nao_reporta_ARCH9005()
        {
            var diagnostics = await RunAsync(Yaml(ErrorPolicy, "    severity: restrita\n"), GlobalConfig("error"));

            Assert.Empty(Arch9005(diagnostics));

            // Controle: a violação existe e saiu com a severidade do .globalconfig.
            Assert.Equal(DiagnosticSeverity.Error, Assert.Single(diagnostics, d => d.Id == Slot).Severity);
        }

        [Fact]
        public async Task GlobalConfig_divergente_reporta_ARCH9005_na_regra_do_YAML()
        {
            var yaml = Yaml(ErrorPolicy, "    severity: restrita\n");

            var diagnostics = await RunAsync(yaml, GlobalConfig("warning"));

            var divergence = Assert.Single(Arch9005(diagnostics));
            Assert.Equal(DiagnosticSeverity.Info, divergence.Severity);

            var message = divergence.GetMessage();
            Assert.Contains("ACME-001", message);
            Assert.Contains(Slot, message);
            Assert.Contains("'error'", message);
            Assert.Contains("'warning'", message);
            Assert.Contains("arch-rules gen", message);

            var span = divergence.Location.GetLineSpan();
            Assert.Equal(YamlPath, span.Path);
            var expectedLine = Array.FindIndex(yaml.Split('\n'), l => l.Contains("id: ACME-001"));
            Assert.Equal(expectedLine, span.StartLinePosition.Line);
        }

        [Fact]
        public async Task Sem_globalconfig_e_YAML_pedindo_error_reporta_ARCH9005_contra_o_default_do_slot()
        {
            var diagnostics = await RunAsync(Yaml(ErrorPolicy, "    severity: restrita\n"));

            var divergence = Assert.Single(Arch9005(diagnostics));
            Assert.Contains("'error'", divergence.GetMessage());
            Assert.Contains("'warning'", divergence.GetMessage());
        }

        [Fact]
        public async Task Sem_globalconfig_e_YAML_pedindo_o_mesmo_que_o_default_nao_reporta()
        {
            // OUTRA-001 e ACME-001 caem em defaults.severity = media → warning = default do slot.
            var diagnostics = await RunAsync(Yaml(ErrorPolicy, string.Empty));

            Assert.Empty(Arch9005(diagnostics));
        }

        [Fact]
        public async Task YAML_sem_severidade_declarada_nem_default_nunca_reporta()
        {
            var diagnostics = await RunAsync(Yaml(string.Empty, string.Empty), GlobalConfig("error"));

            Assert.Empty(Arch9005(diagnostics));
        }

        [Fact]
        public async Task Regra_desabilitada_nao_reporta()
        {
            var diagnostics = await RunAsync(Yaml(ErrorPolicy, "    severity: restrita\n", enabled: false));

            Assert.Empty(Arch9005(diagnostics));
        }

        [Fact]
        public async Task SpecificDiagnosticOptions_vencem_e_entram_na_comparacao()
        {
            // /warnaserror:ARCH0011 (ou WarningsAsErrors no .csproj) com o YAML pedindo warning.
            var diagnostics = await RunAsync(
                Yaml(ErrorPolicy, string.Empty),
                specificOptions: new Dictionary<string, ReportDiagnostic> { [Slot] = ReportDiagnostic.Error });

            var divergence = Assert.Single(Arch9005(diagnostics));
            Assert.Contains("'warning'", divergence.GetMessage());
            Assert.Contains("'error'", divergence.GetMessage());
        }

        [Fact]
        public async Task GlobalConfig_none_contra_YAML_warning_reporta()
        {
            var diagnostics = await RunAsync(Yaml(ErrorPolicy, string.Empty), GlobalConfig("none"));

            var divergence = Assert.Single(Arch9005(diagnostics));
            Assert.Contains("'none'", divergence.GetMessage());
            Assert.DoesNotContain(diagnostics, d => d.Id == Slot);
        }
    }
}
