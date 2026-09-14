using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Arch.Rules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Arch.Rules.Tests
{
    /// <summary>
    /// Testes de <see cref="NamingConventionEvaluator"/>, isolados por arquivo (Trilha 2C do
    /// PLANO-implementacao.md, Fase 2). Não depende do parser YAML real (trilha 1A não é pré-
    /// requisito aqui): <see cref="RuleDefinition"/> é construído à mão e a camada é resolvida por
    /// um <see cref="ILayerResolver"/> de teste que decide pertencimento por nome simples do tipo,
    /// deliberadamente desacoplado da própria convenção de sufixo/prefixo sob teste — assim um
    /// teste nunca confirma a regra usando o mesmo critério que ela testa.
    ///
    /// O analyzer sob teste é construído manualmente (compilation.WithAnalyzers), do mesmo jeito já
    /// provado em <c>tests/Arch.Analyzer.Tests/TestSupport.cs</c> (AnalyzerRunner) — replicado aqui
    /// porque este projeto não tem acesso aos internals daquele (sem InternalsVisibleTo) nem à
    /// infra interna de <c>Arch.Analyzer</c> (RuleEvaluatorRegistry/ArchRulesAnalyzer são
    /// internal só para Arch.Analyzer.Tests). O uso de CSharpAnalyzerTest genérico exigiria um
    /// analyzer com construtor sem parâmetros (constraint `new()`), incompatível com injetar uma
    /// RuleDefinition/ILayerResolver diferente por cenário — por isso a rota direta via
    /// Compilation.WithAnalyzers.
    /// </summary>
    public class NamingConventionEvaluatorTests
    {
        private const string RepositoryLayer = "Repository";

        private static RuleDefinition NamingRule(string require = "suffix=Repository", string layer = RepositoryLayer)
            => new RuleDefinition(
                id: "ACME-003",
                slot: "ARCH0001",
                type: NamingConventionEvaluator.TypeName,
                enabled: true,
                severityName: "media",
                messageTemplate: null,
                help: null,
                extra: new Dictionary<string, string>
                {
                    ["layer"] = layer,
                    ["require"] = require,
                });

        [Fact]
        public async Task Tipo_na_camada_com_sufixo_correto_nao_reporta()
        {
            const string source = @"
namespace Sample
{
    public class FooRepository { }
}";
            var diagnostics = await RunAsync(source, NamingRule(), new NameSetLayerResolver(RepositoryLayer, "FooRepository"));

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task Tipo_na_camada_sem_sufixo_reporta()
        {
            const string source = @"
namespace Sample
{
    public class Foo { }
}";
            var diagnostics = await RunAsync(source, NamingRule(), new NameSetLayerResolver(RepositoryLayer, "Foo"));

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("ARCH0001", diagnostic.Id);
            Assert.Contains("Foo", diagnostic.GetMessage());
            Assert.Contains("suffix=Repository", diagnostic.GetMessage());
        }

        [Fact]
        public async Task Tipo_fora_da_camada_sem_sufixo_nao_reporta()
        {
            const string source = @"
namespace Sample
{
    public class Foo { }
}";
            // Mesmo código do teste anterior, mas o resolvedor de camadas não classifica "Foo" em
            // Repository nenhuma — a regra não se aplica a quem não pertence à camada declarada.
            var diagnostics = await RunAsync(source, NamingRule(), new NameSetLayerResolver(RepositoryLayer));

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task Tipo_parcial_gera_um_unico_diagnostico()
        {
            const string source = @"
namespace Sample
{
    public partial class Foo { public void PartOne() { } }
    public partial class Foo { public void PartTwo() { } }
}";
            var diagnostics = await RunAsync(source, NamingRule(), new NameSetLayerResolver(RepositoryLayer, "Foo"));

            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("Foo", diagnostic.GetMessage());
        }

        [Fact]
        public async Task Tipo_aninhado_na_camada_com_sufixo_correto_nao_reporta()
        {
            const string source = @"
namespace Sample
{
    public class Outer
    {
        public class InnerRepository { }
    }
}";
            // Só o tipo aninhado pertence à camada (o externo "Outer" não está no NameSetLayerResolver);
            // como InnerRepository já satisfaz o sufixo exigido, nada é reportado — nem para
            // InnerRepository (satisfaz a convenção) nem para Outer (fora da camada).
            var diagnostics = await RunAsync(source, NamingRule(), new NameSetLayerResolver(RepositoryLayer, "InnerRepository"));

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task Tipo_aninhado_na_camada_sem_sufixo_reporta_so_o_aninhado()
        {
            const string source = @"
namespace Sample
{
    public class Outer
    {
        public class Inner { }
    }
}";
            // Só "Inner" pertence à camada; "Outer" não. O diagnóstico deve citar o tipo aninhado,
            // não o externo, confirmando que a checagem é por símbolo, não por árvore/contêiner.
            var diagnostics = await RunAsync(source, NamingRule(), new NameSetLayerResolver(RepositoryLayer, "Inner"));

            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("Inner", diagnostic.GetMessage());
            Assert.DoesNotContain("Outer", diagnostic.GetMessage());
        }

        private static async Task<ImmutableArray<Diagnostic>> RunAsync(
            string source,
            RuleDefinition rule,
            ILayerResolver layerResolver)
        {
            var analyzer = new NamingConventionTestAnalyzer(rule, layerResolver);
            var compilation = CreateCompilation(source);

            var diagnostics = await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer))
                .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
                .ConfigureAwait(false);

            var ad0001 = diagnostics.Where(d => d.Id == "AD0001").ToArray();
            Assert.True(
                ad0001.Length == 0,
                "AD0001 reportado (ID não suportado ou exceção no analyzer): "
                    + string.Join(" | ", ad0001.Select(d => d.GetMessage())));

            return diagnostics;
        }

        private static CSharpCompilation CreateCompilation(string source)
        {
            var compilation = CSharpCompilation.Create(
                "NamingConventionEvaluatorTestsAssembly",
                new[] { CSharpSyntaxTree.ParseText(source) },
                LoadReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            Assert.True(
                errors.Length == 0,
                "O código de exemplo do teste não compila: " + string.Join(" | ", errors.Select(e => e.ToString())));

            return compilation;
        }

        private static ImmutableArray<MetadataReference> LoadReferences()
        {
            var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;

            return trusted
                .Split(System.IO.Path.PathSeparator)
                .Where(path => path.Length > 0)
                .Where(path =>
                {
                    var name = System.IO.Path.GetFileName(path);
                    return name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "netstandard.dll", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "mscorlib.dll", StringComparison.OrdinalIgnoreCase);
                })
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();
        }
    }

    /// <summary>
    /// <see cref="ILayerResolver"/> de teste que classifica um <see cref="INamedTypeSymbol"/> numa
    /// única camada por PERTENCIMENTO EXPLÍCITO DE NOME SIMPLES — nunca por sufixo/prefixo, para não
    /// reusar o próprio critério que <see cref="NamingConventionEvaluator"/> está sendo testado por
    /// violar ou satisfazer.
    /// </summary>
    internal sealed class NameSetLayerResolver : ILayerResolver
    {
        private readonly string _layerName;
        private readonly HashSet<string> _memberSimpleNames;

        internal NameSetLayerResolver(string layerName, params string[] memberSimpleNames)
        {
            _layerName = layerName;
            _memberSimpleNames = new HashSet<string>(memberSimpleNames ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        public IReadOnlyCollection<string> GetLayers(INamedTypeSymbol symbol)
            => _memberSimpleNames.Contains(symbol.Name)
                ? new[] { _layerName }
                : Array.Empty<string>();

        public bool IsInLayer(INamedTypeSymbol symbol, string layerName)
            => string.Equals(layerName, _layerName, StringComparison.Ordinal)
                && _memberSimpleNames.Contains(symbol.Name);
    }

    /// <summary>
    /// Analyzer mínimo de teste: chama <see cref="IRuleEvaluator.RegisterActions"/> diretamente
    /// dentro de <c>RegisterCompilationStartAction</c>, com a <see cref="RuleDefinition"/> e o
    /// <see cref="ILayerResolver"/> injetados por construtor — o mesmo papel de
    /// <c>ArchRulesAnalyzer</c> em produção, reduzido ao necessário para exercitar um único
    /// avaliador sem depender do motor real nem do parser YAML.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    internal sealed class NamingConventionTestAnalyzer : DiagnosticAnalyzer
    {
        private readonly RuleDefinition _rule;
        private readonly ILayerResolver _layerResolver;
        private readonly IRuleEvaluator _evaluator = new NamingConventionEvaluator();
        private readonly DiagnosticDescriptor _slotDescriptor;

        internal NamingConventionTestAnalyzer(RuleDefinition rule, ILayerResolver layerResolver)
        {
            _rule = rule;
            _layerResolver = layerResolver;
            _slotDescriptor = new DiagnosticDescriptor(
                "ARCH0001",
                title: "Convenção de nomenclatura violada",
                messageFormat: "{0}",
                category: "Arch.Rules.Tests",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);
        }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(_slotDescriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(compilationStartContext =>
                _evaluator.RegisterActions(compilationStartContext, _rule, _layerResolver, _slotDescriptor));
        }
    }
}
