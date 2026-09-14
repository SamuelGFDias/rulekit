using Arch.Analyzer;
using BenchmarkDotNet.Attributes;

namespace Arch.Benchmarks
{
    /// <summary>
    /// Esqueleto de benchmark (trilha 1C, PLANO-implementacao.md — infra-tests). Só prova que
    /// o projeto compila e roda sob BenchmarkDotNet; NÃO mede overhead de build real (isso é
    /// trabalho de <c>Arch.Benchmarks</c> nas Fases 2/4, contra o orçamento de &lt;5% do ADR-001).
    /// Método placeholder: custo de instanciar <see cref="ArchRulesAnalyzer"/> vazio.
    /// </summary>
    [MemoryDiagnoser]
    public class AnalyzerInstantiationBenchmarks
    {
        [Benchmark]
        public ArchRulesAnalyzer InstantiateEmptyAnalyzer()
        {
            return new ArchRulesAnalyzer();
        }
    }
}
