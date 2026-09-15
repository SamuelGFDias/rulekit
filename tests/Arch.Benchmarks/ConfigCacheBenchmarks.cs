using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Arch.Analyzer;
using Arch.Benchmarks.Support;
using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Arch.Benchmarks
{
    /// <summary>
    /// Trilha 4C (perf-audit, PLANO-implementacao.md Fase 4) — audita o requisito de ADR-001 §5:
    /// "cache em <c>ConditionalWeakTable&lt;SourceText, ArchConfig&gt;</c> chaveado pela INSTÂNCIA
    /// de <c>SourceText</c> ... elimina reparsing repetido" do <c>arch-rules.yaml</c>.
    ///
    /// Roda <see cref="ArchRulesAnalyzer"/> real via <c>CompilationWithAnalyzers</c> sobre um
    /// projeto sintético em memória (<see cref="SyntheticProjectFactory.CreateMediumProject"/>, ~90
    /// tipos Controller/Service/Repository), com <see cref="MemoryDiagnoserAttribute"/> medindo
    /// alocação total — não é um placeholder, é o pipeline de produção completo (parse YAML →
    /// <c>LayerResolver.Create</c> → registro/execução dos 4 avaliadores de <c>Arch.Rules</c>).
    ///
    /// <para><b>Desenho frio vs. quente:</b> cada benchmark decide sozinho, dentro do próprio
    /// método medido, se o cache vai ou não ser atingido — não depende de quantas vezes o
    /// BenchmarkDotNet decide invocar o método por iteração (pilot/unroll), o que tornaria a
    /// comparação frágil.</para>
    /// <list type="bullet">
    ///   <item><description><see cref="Cold_NewAnalyzerAndSourceTextEveryCall"/> cria um
    ///   <see cref="ArchRulesAnalyzer"/> NOVO e um <see cref="SourceText"/> NOVO (mesmo conteúdo
    ///   textual, instância diferente) a cada chamada — a <c>ConditionalWeakTable</c> NUNCA acerta,
    ///   forçando reparse completo do YAML + reconstrução do <c>LayerResolver</c> toda vez, como uma
    ///   primeira compilação depois de instalar o pacote.</description></item>
    ///   <item><description><see cref="Warm_SameAnalyzerAndSourceTextEveryCall"/> reaproveita a
    ///   MESMA instância de analyzer e a MESMA instância de <see cref="SourceText"/>
    ///   (<see cref="GlobalSetupAttribute"/> já roda uma passada de "aquecimento" antes da medição
    ///   começar) — é o cenário que o ADR promete: o usuário digitando código sem mexer no YAML, a
    ///   IDE reaproveitando o mesmo <c>SourceText</c> do AdditionalFile entre compilações.</description></item>
    /// </list>
    /// </summary>
    [MemoryDiagnoser]
    public class ConfigCacheBenchmarks
    {
        private CSharpCompilation _compilation;

        private ArchRulesAnalyzer _warmAnalyzer;
        private SourceText _warmSourceText;
        private InMemoryAdditionalText _warmAdditionalText;

        [GlobalSetup]
        public void Setup()
        {
            _compilation = SyntheticProjectFactory.CreateMediumProject();

            _warmAnalyzer = new ArchRulesAnalyzer();
            _warmSourceText = SourceText.From(SyntheticProjectFactory.ArchRulesYaml);
            _warmAdditionalText = new InMemoryAdditionalText(SyntheticProjectFactory.ConfigFileName, _warmSourceText);

            // Aquece o cache ANTES de qualquer medição: a primeira passada sempre paga o parse
            // completo (é o próprio propósito do cache — não medir isso aqui seria fingir que o
            // cenário "quente" não teve custo de entrada nenhum). A partir daqui, toda chamada de
            // Warm_* com esta mesma instância de analyzer + SourceText acerta a
            // ConditionalWeakTable.
            RunAnalysisAsync(_warmAnalyzer, _warmAdditionalText).GetAwaiter().GetResult();
        }

        [Benchmark(Description = "Cold: novo analyzer + novo SourceText a cada chamada (sem cache hit)")]
        public async Task<int> Cold_NewAnalyzerAndSourceTextEveryCall()
        {
            var analyzer = new ArchRulesAnalyzer();
            var sourceText = SourceText.From(SyntheticProjectFactory.ArchRulesYaml);
            var additionalText = new InMemoryAdditionalText(SyntheticProjectFactory.ConfigFileName, sourceText);

            var diagnostics = await RunAnalysisAsync(analyzer, additionalText).ConfigureAwait(false);
            return diagnostics.Length;
        }

        [Benchmark(Description = "Warm: mesma instância de analyzer + mesmo SourceText (cache hit)")]
        public async Task<int> Warm_SameAnalyzerAndSourceTextEveryCall()
        {
            var diagnostics = await RunAnalysisAsync(_warmAnalyzer, _warmAdditionalText).ConfigureAwait(false);
            return diagnostics.Length;
        }

        private Task<ImmutableArray<Diagnostic>> RunAnalysisAsync(DiagnosticAnalyzer analyzer, AdditionalText additionalText)
        {
            var options = new AnalyzerOptions(ImmutableArray.Create(additionalText));

            return _compilation
                .WithAnalyzers(ImmutableArray.Create(analyzer), options)
                .GetAnalyzerDiagnosticsAsync(CancellationToken.None);
        }
    }
}
