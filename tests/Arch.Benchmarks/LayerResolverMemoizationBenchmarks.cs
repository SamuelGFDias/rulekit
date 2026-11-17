using System;
using System.Collections.Immutable;
using System.Linq;
using Arch.Analyzer;
using Arch.Benchmarks.Support;
using Arch.Config;
using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Arch.Benchmarks
{
    /// <summary>
    /// Trilha 4C (perf-audit) — audita ADR-001 §5: "Classificação símbolo→camada memoizada em
    /// <c>ConcurrentDictionary&lt;INamedTypeSymbol, LayerSet&gt;</c>". Implementado em
    /// <c>src/Arch.Analyzer/LayerResolver.cs</c>.
    ///
    /// <para><b>Chamada direta, sem reflexão (issue #11, item 3):</b> <c>LayerResolver</c> é
    /// <c>internal</c> e este projeto é amigo de <c>Arch.Analyzer</c> (<c>InternalsVisibleTo</c> em
    /// <c>src/Arch.Analyzer/AssemblyInfo.cs</c>). A versão anterior usava reflexão +
    /// <c>Delegate.CreateDelegate</c> para contornar dois <c>Arch.Config.ArchConfig</c> de assemblies
    /// diferentes (o embutido no <c>Arch.Analyzer.dll</c> fundido e o do <c>Arch.Config.dll</c>
    /// solto); desde que o ILRepack roda só no <c>dotnet pack</c>, um <c>ProjectReference</c> nunca
    /// vê o assembly fundido e esse problema não existe mais.</para>
    ///
    /// <para><b>Desenho da comparação (pedido da trilha):</b> "classificar o MESMO símbolo
    /// repetidamente" vs. "1000 símbolos DIFERENTES uma vez cada". Cada benchmark cria um
    /// <c>LayerResolver</c> NOVO a cada chamada (memo sempre vazio no início) — isso reflete a
    /// realidade do próprio pipeline: <c>ArchRulesAnalyzer.OnCompilationStart</c> chama
    /// <c>LayerResolver.Create</c> a cada compilação, o memo NUNCA sobrevive entre compilações (só a
    /// <c>ArchConfig</c> em si é cacheada via <c>ConditionalWeakTable</c> — ver
    /// <see cref="ConfigCacheBenchmarks"/>). <see cref="CreateResolverOnly"/> isola esse custo fixo
    /// de construção para que a diferença entre os outros dois benchmarks possa ser lida como "custo
    /// da classificação em si", sem o ruído de <c>Create</c>.</para>
    /// </summary>
    [MemoryDiagnoser]
    public class LayerResolverMemoizationBenchmarks
    {
        private const int SymbolCount = 1000;
        private const string CheckedLayer = "Repository";

        private ArchConfig _config;
        private CSharpCompilation _compilation;

        private INamedTypeSymbol[] _distinctSymbols;
        private INamedTypeSymbol _sameSymbol;

        [GlobalSetup]
        public void Setup()
        {
            var parseResult = ArchConfigParser.Parse(SyntheticProjectFactory.ArchRulesYaml);
            _config = parseResult.Config
                ?? throw new InvalidOperationException(
                    "arch-rules.yaml sintético do benchmark não parseou: "
                        + string.Join(" | ", parseResult.Errors.Select(e => e.Code + ": " + e.Message)));

            _compilation = SyntheticProjectFactory.CreateManyDistinctTypesProject(SymbolCount);

            _distinctSymbols = CollectDeclaredNamedTypes(_compilation);
            if (_distinctSymbols.Length != SymbolCount)
            {
                throw new InvalidOperationException(
                    $"Esperava {SymbolCount} tipos declarados no projeto sintético, encontrou {_distinctSymbols.Length}.");
            }

            // Um símbolo cujo sufixo é "Repository" — mesma camada checada no laço de símbolos
            // distintos, para que os dois benchmarks comparem exatamente a mesma pergunta
            // (IsInLayer(..., "Repository")) e só a IDENTIDADE do(s) símbolo(s) varie.
            _sameSymbol = _distinctSymbols.First(s => s.Name.EndsWith(CheckedLayer, StringComparison.Ordinal));
        }

        [Benchmark(Description = "Baseline: só LayerResolver.Create (sem classificar nada)")]
        public object CreateResolverOnly()
        {
            return CreateResolver();
        }

        [Benchmark(Description = "1000x IsInLayer no MESMO símbolo (memoização: 1 miss + 999 hits)")]
        public int SameSymbol_1000Classifications()
        {
            var resolver = CreateResolver();
            var hits = 0;

            for (var i = 0; i < SymbolCount; i++)
            {
                if (resolver.IsInLayer(_sameSymbol, CheckedLayer))
                {
                    hits++;
                }
            }

            return hits;
        }

        [Benchmark(Description = "1000x IsInLayer em 1000 símbolos DIFERENTES (1000 misses)")]
        public int DifferentSymbols_1000Classifications()
        {
            var resolver = CreateResolver();
            var hits = 0;

            for (var i = 0; i < _distinctSymbols.Length; i++)
            {
                if (resolver.IsInLayer(_distinctSymbols[i], CheckedLayer))
                {
                    hits++;
                }
            }

            return hits;
        }

        /// <summary>Cria um <c>LayerResolver</c> novo (memo vazio).</summary>
        private LayerResolver CreateResolver()
        {
            ImmutableArray<string> unresolvedTypeNames;
            return LayerResolver.Create(_config, _compilation, out unresolvedTypeNames);
        }

        private static INamedTypeSymbol[] CollectDeclaredNamedTypes(CSharpCompilation compilation)
        {
            var tree = compilation.SyntaxTrees[0];
            var model = compilation.GetSemanticModel(tree);

            return tree.GetRoot()
                .DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Select(node => model.GetDeclaredSymbol(node))
                .Where(symbol => symbol != null)
                .Cast<INamedTypeSymbol>()
                .ToArray();
        }
    }
}
