using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Arch.Rules.Tests
{
    /// <summary>
    /// Testes de <see cref="MaxDependenciesEvaluator"/>, isolados do parser YAML (trilha 1A) e do
    /// pipeline completo do <c>ArchRulesAnalyzer</c> (que vive em <c>Arch.Analyzer.Tests</c> e não é
    /// referenciado por este projeto). Mesma ideia dos testes de 1B: um <see cref="RuleDefinition"/>
    /// construído à mão, um <see cref="ILayerResolver"/> em memória (fake, por predicado) e um
    /// <see cref="DiagnosticAnalyzer"/> mínimo que só encaminha para
    /// <see cref="IRuleEvaluator.RegisterActions"/> — o suficiente para rodar o avaliador contra uma
    /// compilação Roslyn real.
    /// </summary>
    public class MaxDependenciesEvaluatorTests
    {
        private const string ServiceLayer = "Service";

        private static readonly DiagnosticDescriptor SlotDescriptor = new DiagnosticDescriptor(
            "ARCH0004",
            "Máximo de dependências excedido",
            "{0}",
            "Architecture",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static RuleDefinition Rule(string layer, string max, string countBy)
            => new RuleDefinition(
                "ACME-004",
                "ARCH0004",
                "max-dependencies",
                enabled: true,
                severityName: "media",
                messageTemplate: null,
                help: null,
                extra: new Dictionary<string, string>
                {
                    ["layer"] = layer,
                    ["max"] = max,
                    ["countBy"] = countBy,
                });

        /// <summary>
        /// Camada fake: um tipo pertence a <see cref="ServiceLayer"/> sse seu nome simples estiver em
        /// <paramref name="typeNamesInServiceLayer"/>. Suficiente para provar o avaliador sem
        /// depender do <c>LayerResolver</c> real (que é responsabilidade de outra trilha).
        /// </summary>
        private sealed class FakeLayerResolver : ILayerResolver
        {
            private readonly HashSet<string> _typeNamesInServiceLayer;

            internal FakeLayerResolver(params string[] typeNamesInServiceLayer)
            {
                _typeNamesInServiceLayer = new HashSet<string>(typeNamesInServiceLayer, StringComparer.Ordinal);
            }

            public IReadOnlyCollection<string> GetLayers(INamedTypeSymbol symbol)
                => IsInLayer(symbol, ServiceLayer) ? new[] { ServiceLayer } : Array.Empty<string>();

            public bool IsInLayer(INamedTypeSymbol symbol, string layerName)
                => string.Equals(layerName, ServiceLayer, StringComparison.Ordinal)
                    && _typeNamesInServiceLayer.Contains(symbol.Name);
        }

        private sealed class WrapperAnalyzer : DiagnosticAnalyzer
        {
            private readonly IRuleEvaluator _evaluator;
            private readonly RuleDefinition _rule;
            private readonly ILayerResolver _layerResolver;
            private readonly DiagnosticDescriptor _descriptor;

            internal WrapperAnalyzer(
                IRuleEvaluator evaluator,
                RuleDefinition rule,
                ILayerResolver layerResolver,
                DiagnosticDescriptor descriptor)
            {
                _evaluator = evaluator;
                _rule = rule;
                _layerResolver = layerResolver;
                _descriptor = descriptor;
            }

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
                => ImmutableArray.Create(_descriptor);

            public override void Initialize(AnalysisContext context)
            {
                context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                context.EnableConcurrentExecution();
                context.RegisterCompilationStartAction(
                    csac => _evaluator.RegisterActions(csac, _rule, _layerResolver, _descriptor));
            }
        }

        private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

        private static ImmutableArray<MetadataReference> LoadReferences()
        {
            // Referências do próprio runtime que executa os testes (net10.0) — evita depender de um
            // pacote de reference assemblies só para ter System.Object/System.String disponíveis.
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

        private static CSharpCompilation Compile(string source)
        {
            var compilation = CSharpCompilation.Create(
                "MaxDependenciesSample",
                new[] { CSharpSyntaxTree.ParseText(source) },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            Assert.True(
                errors.Length == 0,
                "O código de exemplo do teste não compila: " + string.Join(" | ", errors.Select(e => e.ToString())));

            return compilation;
        }

        private static async Task<ImmutableArray<Diagnostic>> RunAsync(
            string source,
            RuleDefinition rule,
            ILayerResolver layerResolver)
        {
            var compilation = Compile(source);
            var evaluator = new MaxDependenciesEvaluator();
            var analyzer = new WrapperAnalyzer(evaluator, rule, layerResolver, SlotDescriptor);

            var diagnostics = await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer))
                .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
                .ConfigureAwait(false);

            var ad0001 = diagnostics.Where(d => d.Id == "AD0001").ToArray();
            Assert.True(
                ad0001.Length == 0,
                "AD0001 reportado (ID não suportado ou exceção no avaliador): "
                    + string.Join(" | ", ad0001.Select(d => d.GetMessage())));

            return diagnostics;
        }

        [Fact]
        public async Task ConstructorParameters_AcimaDoMax_Reporta()
        {
            const string source = @"
namespace Acme.Domain
{
    public class OrderService
    {
        public OrderService(
            Dep1 a, Dep2 b, Dep3 c, Dep4 d, Dep5 e, Dep6 f, Dep7 g, Dep8 h) { }
    }

    public class Dep1 { } public class Dep2 { } public class Dep3 { } public class Dep4 { }
    public class Dep5 { } public class Dep6 { } public class Dep7 { } public class Dep8 { }
}";
            var rule = Rule(ServiceLayer, max: "7", countBy: "constructor-parameters");
            var layerResolver = new FakeLayerResolver("OrderService");

            var diagnostics = await RunAsync(source, rule, layerResolver);

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("ARCH0004", diagnostic.Id);
            Assert.Contains("OrderService", diagnostic.GetMessage());
            Assert.Contains("8", diagnostic.GetMessage());
            Assert.Contains("7", diagnostic.GetMessage());
        }

        [Fact]
        public async Task ConstructorParameters_ExatamenteNoMax_NaoReporta()
        {
            const string source = @"
namespace Acme.Domain
{
    public class OrderService
    {
        public OrderService(
            Dep1 a, Dep2 b, Dep3 c, Dep4 d, Dep5 e, Dep6 f, Dep7 g, Dep8 h) { }
    }

    public class Dep1 { } public class Dep2 { } public class Dep3 { } public class Dep4 { }
    public class Dep5 { } public class Dep6 { } public class Dep7 { } public class Dep8 { }
}";
            // Limite inclusivo: max: 8 com contagem 8 não deve reportar.
            var rule = Rule(ServiceLayer, max: "8", countBy: "constructor-parameters");
            var layerResolver = new FakeLayerResolver("OrderService");

            var diagnostics = await RunAsync(source, rule, layerResolver);

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task Fields_NaoPrimitivosAcimaDoMax_Reporta()
        {
            const string source = @"
namespace Acme.Domain
{
    public class OrderService
    {
        private readonly Dep1 _a;
        private readonly Dep2 _b;
        protected readonly Dep3 _c;
    }

    public class Dep1 { } public class Dep2 { } public class Dep3 { }
}";
            var rule = Rule(ServiceLayer, max: "2", countBy: "fields");
            var layerResolver = new FakeLayerResolver("OrderService");

            var diagnostics = await RunAsync(source, rule, layerResolver);

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("ARCH0004", diagnostic.Id);
            Assert.Contains("OrderService", diagnostic.GetMessage());
            Assert.Contains("3", diagnostic.GetMessage());
            Assert.Contains("2", diagnostic.GetMessage());
        }

        [Fact]
        public async Task Fields_ApenasPrimitivos_NaoReporta()
        {
            const string source = @"
namespace Acme.Domain
{
    public class OrderService
    {
        private readonly int _count;
        private readonly string _name;
        private readonly bool _active;
    }
}";
            var rule = Rule(ServiceLayer, max: "0", countBy: "fields");
            var layerResolver = new FakeLayerResolver("OrderService");

            var diagnostics = await RunAsync(source, rule, layerResolver);

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task TipoForaDaCamada_NaoReportaMesmoExcedendo()
        {
            const string source = @"
namespace Acme.Domain
{
    public class SomeOtherThing
    {
        public SomeOtherThing(
            Dep1 a, Dep2 b, Dep3 c, Dep4 d, Dep5 e, Dep6 f, Dep7 g, Dep8 h,
            Dep9 i, Dep10 j, Dep11 k, Dep12 l, Dep13 m, Dep14 n, Dep15 o, Dep16 p,
            Dep17 q, Dep18 r, Dep19 s, Dep20 t) { }
    }

    public class Dep1 {} public class Dep2 {} public class Dep3 {} public class Dep4 {}
    public class Dep5 {} public class Dep6 {} public class Dep7 {} public class Dep8 {}
    public class Dep9 {} public class Dep10 {} public class Dep11 {} public class Dep12 {}
    public class Dep13 {} public class Dep14 {} public class Dep15 {} public class Dep16 {}
    public class Dep17 {} public class Dep18 {} public class Dep19 {} public class Dep20 {}
}";
            var rule = Rule(ServiceLayer, max: "7", countBy: "constructor-parameters");
            // FakeLayerResolver não conhece "SomeOtherThing" -> nunca está na camada Service.
            var layerResolver = new FakeLayerResolver();

            var diagnostics = await RunAsync(source, rule, layerResolver);

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task CountByDesconhecido_NaoReportaNemLancaExcecao()
        {
            const string source = @"
namespace Acme.Domain
{
    public class OrderService
    {
        public OrderService(
            Dep1 a, Dep2 b, Dep3 c, Dep4 d, Dep5 e, Dep6 f, Dep7 g, Dep8 h) { }
    }

    public class Dep1 { } public class Dep2 { } public class Dep3 { } public class Dep4 { }
    public class Dep5 { } public class Dep6 { } public class Dep7 { } public class Dep8 { }
}";
            var rule = Rule(ServiceLayer, max: "7", countBy: "bogus");
            var layerResolver = new FakeLayerResolver("OrderService");

            var diagnostics = await RunAsync(source, rule, layerResolver);

            Assert.Empty(diagnostics);
        }
    }
}
