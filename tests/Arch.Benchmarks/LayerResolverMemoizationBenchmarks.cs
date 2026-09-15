using System;
using System.Linq;
using System.Reflection;
using Arch.Analyzer;
using Arch.Benchmarks.Support;
using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Arch.Benchmarks
{
    /// <summary>
    /// Trilha 4C (perf-audit) — audita ADR-001 §5: "Classificação símbolo→camada memoizada em
    /// <c>ConcurrentDictionary&lt;INamedTypeSymbol, LayerSet&gt;</c>". Implementado em
    /// <c>src/Arch.Analyzer/LayerResolver.cs</c> (só leitura nesta trilha, não editado).
    ///
    /// <para><b>Por que reflexão:</b> <c>LayerResolver</c> é <c>internal sealed</c> e este projeto
    /// NÃO tem <c>InternalsVisibleTo</c> (só <c>Arch.Analyzer.Tests</c> tem — ver
    /// <c>src/Arch.Analyzer/AssemblyInfo.cs</c>, não editado). Adicionar o benchmark à lista de
    /// amigos exigiria tocar em código de produção, fora do escopo desta trilha.
    ///
    /// <c>Create</c> e <c>IsInLayer</c> são resolvidos via reflexão UMA VEZ no
    /// <see cref="GlobalSetupAttribute"/>; dentro do laço quente, porém, a chamada em si passa por um
    /// <em>delegate</em> ligado com <see cref="Delegate.CreateDelegate(Type, object, MethodInfo)"/>
    /// (<c>Func&lt;INamedTypeSymbol, string, bool&gt;</c>) — não por <c>MethodInfo.Invoke</c> a cada
    /// chamada. Isso funciona mesmo com o tipo DECLARANTE (<c>LayerResolver</c>) sendo interno: a
    /// vinculação de delegate só exige que a ASSINATURA bata (aqui,
    /// <see cref="INamedTypeSymbol"/>/<see cref="string"/>/<see cref="bool"/> — todos tipos
    /// compartilhados, nunca internalizados pelo ILRepack), não que o chamador enxergue o tipo
    /// declarante publicamente. O custo de <c>Delegate.CreateDelegate</c> em si é pago dentro de
    /// <see cref="CreateResolver"/>, igualmente nos três benchmarks abaixo — não distorce a
    /// comparação relativa entre eles (ver <see cref="CreateResolverOnly"/>).</para>
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
    ///
    /// <para><b>Por que a ArchConfig também é obtida via reflexão (achado real desta trilha, não só
    /// implementação):</b> <c>build/ilrepack.targets</c> funde e INTERNALIZA <c>Arch.Config.dll</c>
    /// dentro do <c>Arch.Analyzer.dll</c> final — a <c>Arch.Analyzer.csproj</c> que este projeto
    /// referencia via <c>ProjectReference</c> já produz esse DLL fundido, não os assemblies soltos
    /// (ver saída de build: "Merging 5 assemblies"). Isso significa que o <c>Arch.Config.ArchConfig</c>
    /// EMBUTIDO no <c>Arch.Analyzer.dll</c> fundido é um TIPO DE TEMPO DE EXECUÇÃO DIFERENTE do
    /// <c>Arch.Config.ArchConfig</c> do <c>Arch.Config.dll</c> solto que também chega ao nosso
    /// bin/ (via referência transitiva) — mesmo namespace e nome, dois tokens de tipo distintos.
    /// Chamar <c>ArchConfigParser.Parse</c> diretamente (contra o DLL solto) e passar o resultado
    /// para <c>LayerResolver.Create</c> (que espera o tipo embutido no fundido) falha em runtime com
    /// <c>ArgumentException: "Object of type ... cannot be converted to type ..."</c> mesmo os dois
    /// tipos tendo o MESMO nome totalmente qualificado — sintoma clássico de dois contextos de
    /// carregamento de assembly distintos para o "mesmo" tipo. A correção aqui é usar reflexão para
    /// os DOIS lados (parser e resolver), garantindo que o <c>ArchConfig</c> produzido já nasce no
    /// mesmo assembly fundido que <c>LayerResolver.Create</c> espera. Isto é uma particularidade de
    /// COMO ESTE BENCHMARK consome o analyzer (via ProjectReference direto ao projeto fundido,
    /// nunca ao <c>.nupkg</c> publicado) — um consumidor real da lib nunca vê essa duplicidade,
    /// porque só enxerga o <c>Arch.Analyzer.dll</c> fundido, nunca o <c>Arch.Config.dll</c> solto.
    /// Reportado como achado da auditoria, não corrigido em <c>src/</c> (fora do escopo desta
    /// trilha).</para>
    /// </summary>
    [MemoryDiagnoser]
    public class LayerResolverMemoizationBenchmarks
    {
        private const int SymbolCount = 1000;
        private const string CheckedLayer = "Repository";

        private object _config;
        private CSharpCompilation _compilation;
        private MethodInfo _createMethod;
        private MethodInfo _isInLayerMethod;

        private INamedTypeSymbol[] _distinctSymbols;
        private INamedTypeSymbol _sameSymbol;

        [GlobalSetup]
        public void Setup()
        {
            var analyzerAssembly = typeof(ArchRulesAnalyzer).Assembly;

            // ArchConfigParser/ArchConfig OBTIDOS DO MESMO ASSEMBLY FUNDIDO que LayerResolver —
            // ver comentário de classe acima ("Por que a ArchConfig também é obtida via reflexão").
            var configParserType = analyzerAssembly.GetType("Arch.Config.ArchConfigParser", throwOnError: true);
            var parseMethod = configParserType!.GetMethod(
                "Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
                ?? throw new InvalidOperationException("Arch.Config.ArchConfigParser.Parse(string) não encontrado via reflexão.");

            var parseResult = parseMethod.Invoke(null, new object[] { SyntheticProjectFactory.ArchRulesYaml });
            var resultType = parseResult!.GetType();
            _config = resultType.GetProperty("Config")!.GetValue(parseResult);

            if (_config == null)
            {
                var errors = (System.Collections.IEnumerable)resultType.GetProperty("Errors")!.GetValue(parseResult)!;
                var messages = errors.Cast<object>().Select(e =>
                {
                    var t = e.GetType();
                    return t.GetProperty("Code")!.GetValue(e) + ": " + t.GetProperty("Message")!.GetValue(e);
                });
                throw new InvalidOperationException(
                    "arch-rules.yaml sintético do benchmark não parseou: " + string.Join(" | ", messages));
            }

            _compilation = SyntheticProjectFactory.CreateManyDistinctTypesProject(SymbolCount);

            var layerResolverType = analyzerAssembly.GetType("Arch.Analyzer.LayerResolver", throwOnError: true);
            _createMethod = layerResolverType!.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("LayerResolver.Create não encontrado via reflexão.");
            _isInLayerMethod = layerResolverType.GetMethod("IsInLayer", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("LayerResolver.IsInLayer não encontrado via reflexão.");

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
            var isInLayer = CreateResolver();
            var hits = 0;

            for (var i = 0; i < SymbolCount; i++)
            {
                if (isInLayer(_sameSymbol, CheckedLayer))
                {
                    hits++;
                }
            }

            return hits;
        }

        [Benchmark(Description = "1000x IsInLayer em 1000 símbolos DIFERENTES (1000 misses)")]
        public int DifferentSymbols_1000Classifications()
        {
            var isInLayer = CreateResolver();
            var hits = 0;

            for (var i = 0; i < _distinctSymbols.Length; i++)
            {
                if (isInLayer(_distinctSymbols[i], CheckedLayer))
                {
                    hits++;
                }
            }

            return hits;
        }

        /// <summary>
        /// Cria um <c>LayerResolver</c> novo (memo vazio) e devolve um <em>delegate</em> já ligado
        /// ao seu <c>IsInLayer</c> — ver comentário de classe ("Por que reflexão") sobre por que um
        /// delegate funciona aqui apesar do tipo declarante ser interno/de outro "mundo" de
        /// assembly.
        /// </summary>
        private Func<INamedTypeSymbol, string, bool> CreateResolver()
        {
            // args[2] é o parâmetro `out ImmutableArray<string> unresolvedTypeNames` — reflexão
            // aceita `null` como placeholder para um `out` (o valor de entrada nunca é lido).
            var args = new object[] { _config, _compilation, null };
            var resolver = _createMethod.Invoke(null, args);
            return (Func<INamedTypeSymbol, string, bool>)Delegate.CreateDelegate(
                typeof(Func<INamedTypeSymbol, string, bool>), resolver, _isInLayerMethod);
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
