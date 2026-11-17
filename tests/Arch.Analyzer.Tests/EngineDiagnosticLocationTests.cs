using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Issue #7 (achado principal): um diagnóstico de motor sobre uma regra precisa apontar a LINHA
    /// da regra no YAML (ADR-001 D4: "apontando linha/coluna") — com 30+ regras, "slot fora do pool"
    /// na linha 1 obriga a caçar a culpada à mão. E, com `extends`, a linha só faz sentido no arquivo
    /// que declarou a regra, não no principal. Parser e avaliadores reais.
    /// </summary>
    public class EngineDiagnosticLocationTests
    {
        private const string MainPath = "/repo/arch-rules.yaml";
        private const string BasePath = "/repo/arch-rules.base.yaml";

        private const string Source = @"
public class OrderRepository { public static void Load() { } }
public class OrderController { public void Get() { OrderRepository.Load(); } }
";

        private const string Header = @"schema: arch-rules/v1
layers:
  Controller:
    match:
      - nameSuffix: Controller
  Repository:
    match:
      - nameSuffix: Repository
severities:
  aviso: warning
defaults:
  severity: aviso
";

        private const string RulesWithProblemsInTheMiddle = @"rules:
  - id: OK-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
  - id: FORA-001
    slot: ARCH0999
    type: forbidden-call
    from: Controller
    to: Repository
  - id: DUP-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
  - id: RUIM-001
    slot: ARCH0003
    type: forbidden-call
    from: Controller
  - id: FIM-001
    slot: ARCH0004
    type: forbidden-call
    from: Controller
    to: Repository
";

        private static Task<ImmutableArray<Diagnostic>> RunAsync(params (string Path, string Yaml)[] files)
            => AnalyzerRunner.RunAsync(
                new ArchRulesAnalyzer(),
                TestCompilations.Create(Source),
                files.Select(f => (AdditionalText)new InMemoryAdditionalText(f.Path, f.Yaml)).ToArray());

        /// <summary>Linha 0-based (convenção de <see cref="LinePosition"/>) de <paramref name="marker"/>.</summary>
        private static int LineOf(string yaml, string marker)
        {
            var lines = yaml.Replace("\r\n", "\n").Split('\n');
            var index = Array.FindIndex(lines, l => l.Contains(marker));
            Assert.True(index >= 0, "marcador não encontrado: " + marker);
            return index;
        }

        private static Diagnostic Single(ImmutableArray<Diagnostic> diagnostics, string id, string ruleId)
            => Assert.Single(diagnostics, d => d.Id == id && d.GetMessage().Contains(ruleId));

        [Fact]
        public async Task ARCH9002_de_slot_fora_do_pool_aponta_a_linha_da_regra()
        {
            var yaml = Header + RulesWithProblemsInTheMiddle;

            var diagnostics = await RunAsync((MainPath, yaml));

            var span = Single(diagnostics, "ARCH9002", "FORA-001").Location.GetLineSpan();
            Assert.Equal(MainPath, span.Path);
            Assert.Equal(LineOf(yaml, "id: FORA-001"), span.StartLinePosition.Line);
        }

        [Fact]
        public async Task ARCH9002_de_slot_duplicado_aponta_a_linha_da_regra_duplicada()
        {
            var yaml = Header + RulesWithProblemsInTheMiddle;

            var diagnostics = await RunAsync((MainPath, yaml));

            var span = Single(diagnostics, "ARCH9002", "DUP-001").Location.GetLineSpan();
            Assert.Equal(LineOf(yaml, "id: DUP-001"), span.StartLinePosition.Line);
        }

        [Fact]
        public async Task ARCH9008_aponta_a_linha_da_regra_no_meio_do_arquivo()
        {
            var yaml = Header + RulesWithProblemsInTheMiddle;

            var diagnostics = await RunAsync((MainPath, yaml));

            var span = Single(diagnostics, "ARCH9008", "RUIM-001").Location.GetLineSpan();
            Assert.Equal(MainPath, span.Path);
            Assert.Equal(LineOf(yaml, "id: RUIM-001"), span.StartLinePosition.Line);
        }

        [Fact]
        public async Task Erro_de_regra_declarada_no_arquivo_estendido_aponta_o_arquivo_estendido()
        {
            var baseYaml = Header + RulesWithProblemsInTheMiddle;
            var mainYaml = "schema: arch-rules/v1\nextends: [./arch-rules.base.yaml]\n";

            var diagnostics = await RunAsync((BasePath, baseYaml), (MainPath, mainYaml));

            var schemaError = Single(diagnostics, "ARCH9008", "RUIM-001").Location.GetLineSpan();
            Assert.Equal(BasePath, schemaError.Path);
            Assert.Equal(LineOf(baseYaml, "id: RUIM-001"), schemaError.StartLinePosition.Line);

            var slotError = Single(diagnostics, "ARCH9002", "FORA-001").Location.GetLineSpan();
            Assert.Equal(BasePath, slotError.Path);
            Assert.Equal(LineOf(baseYaml, "id: FORA-001"), slotError.StartLinePosition.Line);
        }

        [Fact]
        public async Task Diagnostico_sem_posicao_continua_no_inicio_do_arquivo_principal()
        {
            var yaml = Header.Replace(
                    "      - nameSuffix: Repository\n",
                    "      - nameSuffix: Repository\n      - implements: Nao.Existe.IFoo\n")
                + RulesWithProblemsInTheMiddle;

            var diagnostics = await RunAsync((MainPath, yaml));

            var span = Assert.Single(diagnostics, d => d.Id == "ARCH9004").Location.GetLineSpan();
            Assert.Equal(MainPath, span.Path);
            Assert.Equal(0, span.StartLinePosition.Line);
        }
    }
}
