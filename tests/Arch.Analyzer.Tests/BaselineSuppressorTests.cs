using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Issue #25 — o <see cref="BaselineSuppressor"/> (lê <c>arch-rules.baseline.yaml</c> dos AdditionalFiles,
    /// sem I/O) e o <c>ARCH9008</c>-irmão <c>ARCH9009</c> (baseline inválido, reportado pelo analyzer). Pipeline
    /// real: parser, avaliadores, analyzer e suppressor rodando juntos num único driver.
    /// </summary>
    public class BaselineSuppressorTests
    {
        private const string RulesPath = "/repo/arch-rules.yaml";
        private const string BaselinePath = "/repo/arch-rules.baseline.yaml";
        private const string SourcePath = "/repo/Services/OrderController.cs";

        private const string Policy = @"schema: arch-rules/v1
layers:
  Controller:
    match: [ { nameSuffix: Controller } ]
  Repository:
    match: [ { nameSuffix: Repository } ]
  Store:
    match: [ { nameSuffix: Store } ]
severities:
  aviso: warning
defaults:
  severity: aviso
rules:
  - { id: ACME-001, slot: ARCH0001, type: forbidden-call, from: Controller, to: Repository }
  - { id: ACME-002, slot: ARCH0002, type: naming-convention, layer: Store, require: { suffix: Repository } }
";

        private const string Code = @"
public class OrderRepository { public static void Load() { } public static void Save() { } }
public class OrderController
{
    public void Get() { OrderRepository.Load(); }
    public void Get2() { OrderRepository.Load(); }
    public void Put() { OrderRepository.Save(); }
}
public class OrderStore { }
";

        private const string LoadSubject = "OrderController -> OrderRepository.Load";

        private static string Baseline(params string[] entries)
            => "schema: arch-rules-baseline/v1\nentries:\n" + string.Join(string.Empty, entries);

        private static string Entry(string rule, string file, string subject)
            => $"  - {{ rule: {rule}, file: '{file}', subject: '{subject}' }}\n";

        private static async Task<ImmutableArray<Diagnostic>> RunAsync(
            string baselineText,
            string baselinePath = BaselinePath,
            string sourcePath = SourcePath,
            string code = Code)
        {
            var compilation = CSharpCompilation.Create(
                "BaselineSample",
                new[] { CSharpSyntaxTree.ParseText(code, path: sourcePath) },
                TestCompilations.Create("class X {}").References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var files = ImmutableArray.CreateBuilder<AdditionalText>();
            files.Add(new InMemoryAdditionalText(RulesPath, Policy));
            if (baselineText != null)
            {
                files.Add(new InMemoryAdditionalText(baselinePath, baselineText));
            }

            var diagnostics = await compilation
                .WithAnalyzers(
                    ImmutableArray.Create<DiagnosticAnalyzer>(new ArchRulesAnalyzer(), new BaselineSuppressor()),
                    new CompilationWithAnalyzersOptions(
                        new AnalyzerOptions(files.ToImmutable()),
                        onAnalyzerException: null,
                        concurrentAnalysis: true,
                        logAnalyzerExecutionTime: false,
                        reportSuppressedDiagnostics: true))
                .GetAnalyzerDiagnosticsAsync(CancellationToken.None);

            Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
            return diagnostics;
        }

        private static Diagnostic[] Slot(ImmutableArray<Diagnostic> all, string id)
            => all.Where(d => d.Id == id).OrderBy(d => d.Location.SourceSpan.Start).ToArray();

        [Fact]
        public void Declara_uma_supressao_por_slot_com_ids_unicos_e_estaveis()
        {
            var supported = new BaselineSuppressor().SupportedSuppressions;

            Assert.Equal(512, supported.Length);
            Assert.Equal(512, supported.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(supported, s => s.Id == "ARCHB0001" && s.SuppressedDiagnosticId == "ARCH0001");
            Assert.Contains(supported, s => s.Id == "ARCHB0512" && s.SuppressedDiagnosticId == "ARCH0512");
            Assert.All(supported, s => Assert.Contains("arch-rules.baseline.yaml", s.Justification.ToString()));
        }

        [Fact]
        public async Task Sem_baseline_nada_e_suprimido()
        {
            var all = await RunAsync(null);

            Assert.Equal(3, Slot(all, "ARCH0001").Length);
            Assert.All(all, d => Assert.False(d.IsSuppressed));
            Assert.DoesNotContain(all, d => d.Id == "ARCH9009");
        }

        [Fact]
        public async Task Entrada_que_casa_suprime_todas_as_ocorrencias_do_mesmo_assunto_e_so_elas()
        {
            var all = await RunAsync(Baseline(Entry("ACME-001", "Services/OrderController.cs", LoadSubject)));

            var calls = Slot(all, "ARCH0001");
            Assert.Equal(3, calls.Length);
            Assert.Equal(2, calls.Count(d => d.IsSuppressed));
            var live = Assert.Single(calls, d => !d.IsSuppressed);
            Assert.Equal("OrderController -> OrderRepository.Save", live.Properties["archSubject"]);
        }

        [Fact]
        public async Task Assunto_diferente_nao_suprime()
        {
            var all = await RunAsync(Baseline(Entry("ACME-001", "Services/OrderController.cs", "OrderController -> OrderRepository.Outro")));

            Assert.All(Slot(all, "ARCH0001"), d => Assert.False(d.IsSuppressed));
        }

        [Fact]
        public async Task Arquivo_diferente_nao_suprime()
        {
            var all = await RunAsync(Baseline(Entry("ACME-001", "Services/Outro.cs", LoadSubject)));

            Assert.All(Slot(all, "ARCH0001"), d => Assert.False(d.IsSuppressed));
        }

        [Fact]
        public async Task Regra_diferente_nao_suprime()
        {
            var all = await RunAsync(Baseline(Entry("ACME-999", "Services/OrderController.cs", LoadSubject)));

            Assert.All(Slot(all, "ARCH0001"), d => Assert.False(d.IsSuppressed));
        }

        [Fact]
        public async Task Caminho_do_baseline_ignora_caixa_e_aceita_contrabarra()
        {
            var all = await RunAsync(Baseline(Entry("ACME-001", "services\\ORDERCONTROLLER.cs", LoadSubject)));

            Assert.Equal(2, Slot(all, "ARCH0001").Count(d => d.IsSuppressed));
        }

        [Fact]
        public async Task Caminho_e_relativo_ao_diretorio_do_baseline_e_arquivo_fora_dele_nao_casa()
        {
            // Baseline em /repo/Services: a entrada "OrderController.cs" casa /repo/Services/OrderController.cs ...
            var inside = await RunAsync(
                Baseline(Entry("ACME-001", "OrderController.cs", LoadSubject)),
                baselinePath: "/repo/Services/arch-rules.baseline.yaml");
            Assert.Equal(2, Slot(inside, "ARCH0001").Count(d => d.IsSuppressed));

            // ... mas não casa um arquivo de outro diretório com o mesmo nome.
            var outside = await RunAsync(
                Baseline(Entry("ACME-001", "OrderController.cs", LoadSubject)),
                baselinePath: "/repo/Services/arch-rules.baseline.yaml",
                sourcePath: "/repo/Other/OrderController.cs");
            Assert.All(Slot(outside, "ARCH0001"), d => Assert.False(d.IsSuppressed));
        }

        [Fact]
        public async Task Diagnostico_de_simbolo_naming_convention_tambem_e_suprimido()
        {
            var all = await RunAsync(Baseline(Entry("ACME-002", "Services/OrderController.cs", "OrderStore")));

            var d = Assert.Single(Slot(all, "ARCH0002"));
            Assert.True(d.IsSuppressed);
        }

        [Fact]
        public async Task Baseline_invalido_nao_suprime_nada_e_reporta_ARCH9009_com_linha_e_coluna()
        {
            var all = await RunAsync("schema: arch-rules-baseline/v1\nentries:\n  - { rule: ACME-001, file: x }\n");

            Assert.All(Slot(all, "ARCH0001"), d => Assert.False(d.IsSuppressed));
            var d9009 = Assert.Single(all, d => d.Id == "ARCH9009");
            Assert.Equal(DiagnosticSeverity.Warning, d9009.Severity);
            Assert.Contains("subject", d9009.GetMessage());
            var span = d9009.Location.GetLineSpan();
            Assert.Equal(BaselinePath, span.Path);
            Assert.Equal(2, span.StartLinePosition.Line); // linha 3 do arquivo, 0-based
            Assert.Equal(4, span.StartLinePosition.Character);
        }

        [Fact]
        public async Task Baseline_valido_nao_gera_ARCH9009()
        {
            var all = await RunAsync(Baseline(Entry("ACME-001", "Services/OrderController.cs", LoadSubject)));

            Assert.DoesNotContain(all, d => d.Id == "ARCH9009");
        }

        [Fact]
        public async Task Baseline_vazio_e_valido_e_nao_suprime_nada()
        {
            var all = await RunAsync("schema: arch-rules-baseline/v1\nentries: []\n");

            Assert.DoesNotContain(all, d => d.Id == "ARCH9009");
            Assert.All(all, d => Assert.False(d.IsSuppressed));
        }

        [Fact]
        public async Task O_baseline_nao_e_tratado_como_config_nem_como_extends()
        {
            // Se o analyzer o lesse como arch-rules.yaml, o schema 'arch-rules-baseline/v1' geraria ARCH9001.
            var all = await RunAsync(Baseline(Entry("ACME-001", "Services/OrderController.cs", LoadSubject)));

            Assert.DoesNotContain(all, d => d.Id == "ARCH9001" || d.Id == "ARCH9006");
        }

        [Fact]
        public void Cache_devolve_a_mesma_analise_para_a_mesma_instancia_de_texto_e_reanalisa_quando_muda()
        {
            var text = SourceText.From(Baseline(Entry("ACME-001", "a.cs", "S")));

            var first = BaselineCache.Get(text);
            var second = BaselineCache.Get(text);
            var other = BaselineCache.Get(SourceText.From(Baseline(Entry("ACME-001", "a.cs", "S"))));

            Assert.Same(first, second);
            Assert.NotSame(first, other);
            Assert.True(first.IsValid);
            Assert.True(first.Contains("ACME-001", "a.cs", "S"));
            Assert.True(first.Contains("ACME-001", "A.CS", "S"));
            Assert.False(first.Contains("ACME-001", "a.cs", "s"));
        }
    }
}
