using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Arch.Rules.Tests
{
    /// <summary>
    /// Trilha 2B — `must-route-through` como açúcar sintático sobre `forbidden-call` (ADR-001 D2,
    /// "Nota sobre must-route-through"). A detecção (mesma de `from`/`to`), o controle de falso
    /// positivo e a composição de mensagem já são cobertos por
    /// <see cref="ForbiddenCallEvaluatorTests"/> via herança de <see cref="ForbiddenCallEvaluator"/>;
    /// este arquivo cobre só o que é específico desta regra: <see cref="IRuleEvaluator.RuleType"/> e a
    /// expansão de <c>{through}</c> no template default.
    /// </summary>
    public class MustRouteThroughEvaluatorTests
    {
        private const string RuleId = "ACME-002";
        private const string TestSlotId = "ARCHTEST02";

        private const string ControllerLayer = "Controller";
        private const string ServiceLayer = "Service";
        private const string RepositoryLayer = "Repository";

        [Fact]
        public void RuleType_e_must_route_through()
        {
            Assert.Equal("must-route-through", new MustRouteThroughEvaluator().RuleType);
            Assert.Equal(MustRouteThroughEvaluator.TypeName, new MustRouteThroughEvaluator().RuleType);
        }

        /// <summary>
        /// Caso central: Controller alcançando Repository diretamente, com `through: [Service]` na
        /// regra. Prova que `{through}` é expandido de verdade na mensagem (e não fica literal), com
        /// o valor exatamente como o parser da 1A o serializa em <c>Extra["through"]</c> — uma
        /// sequência YAML de um item vira a string escalar "Service" (ver
        /// <c>ArchConfigDocumentParser.StringifyNode</c>).
        /// </summary>
        [Fact]
        public async Task Controller_chamando_Repository_diretamente_reporta_com_through_na_mensagem()
        {
            const string source = @"
public class OrderRepository
{
    public void Load() { }
}

public class OrderController
{
    public void Get(OrderRepository repo)
    {
        {|#0:repo.Load()|};
    }
}
";

            await VerifyAsync(
                source,
                Rule(ControllerLayer, RepositoryLayer, ServiceLayer),
                Expect(
                    0,
                    RuleId + ": Controller não pode alcançar Repository diretamente (OrderRepository.Load). " +
                    "Passe por Service."));
        }

        /// <summary>
        /// Controle central desta regra: chamar o próprio intermediário (`Service`, o `through`) NÃO
        /// é o alvo de `must-route-through` — a regra só proíbe o alcance DIRETO a `to`
        /// (`Repository`). Isso já é garantido pela detecção herdada de `ForbiddenCallEvaluator`
        /// (que só registra ações sobre a camada `to`), mas este teste prova explicitamente que o
        /// comportamento central da regra — "pode chamar o intermediário" — se sustenta.
        /// </summary>
        [Fact]
        public async Task Controller_chamando_Service_o_intermediario_nao_reporta()
        {
            const string source = @"
public class OrderService
{
    public void Do() { }
}

public class OrderController
{
    public void Get(OrderService service)
    {
        service.Do();
    }
}
";

            await VerifyAsync(source, Rule(ControllerLayer, RepositoryLayer, ServiceLayer));
        }

        // ------------------------------------------------------------------
        // Infra do teste (mesmo padrão de ForbiddenCallEvaluatorTests)
        // ------------------------------------------------------------------

        private static DiagnosticResult Expect(int markupLocation, string message)
        {
            return new DiagnosticResult(TestSlotId, DiagnosticSeverity.Warning)
                .WithLocation(markupLocation)
                .WithArguments(message);
        }

        private static RuleDefinition Rule(string fromLayer, string toLayer, string through)
        {
            var extra = new Dictionary<string, string>(StringComparer.Ordinal);
            if (fromLayer != null)
            {
                extra["from"] = fromLayer;
            }

            if (toLayer != null)
            {
                extra["to"] = toLayer;
            }

            if (through != null)
            {
                extra["through"] = through;
            }

            return new RuleDefinition(
                RuleId,
                "ARCH0008",
                MustRouteThroughEvaluator.TypeName,
                enabled: true,
                severityName: "restrita",
                messageTemplate: null,
                help: "https://wiki.acme/arq/" + RuleId,
                extra: extra);
        }

        private static ArchConfig Policy(RuleDefinition rule)
        {
            var layers = new Dictionary<string, LayerDefinition>(StringComparer.Ordinal)
            {
                [ControllerLayer] = LayerBySuffix(ControllerLayer),
                [ServiceLayer] = LayerBySuffix(ServiceLayer),
                [RepositoryLayer] = LayerBySuffix(RepositoryLayer),
            };

            return new ArchConfig(
                "arch-rules/v1",
                layers,
                new[] { rule },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["restrita"] = "error" },
                "restrita");
        }

        private static LayerDefinition LayerBySuffix(string name)
        {
            return new LayerDefinition(
                name,
                new[] { new LayerMatchCriterion(nameSuffix: name) },
                Array.Empty<LayerMatchCriterion>());
        }

        private static async Task VerifyAsync(string source, RuleDefinition rule, params DiagnosticResult[] expected)
        {
            var test = new Harness(Policy(rule))
            {
                TestState =
                {
                    Sources = { source },
                },
            };

            foreach (var diagnostic in expected)
            {
                test.ExpectedDiagnostics.Add(diagnostic);
            }

            await test.RunAsync();
        }

        private sealed class Harness : CSharpAnalyzerTest<MustRouteThroughHostAnalyzer, DefaultVerifier>
        {
            private readonly ArchConfig _config;

            internal Harness(ArchConfig config)
            {
                _config = config;
            }

            protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers()
            {
                return new DiagnosticAnalyzer[] { new MustRouteThroughHostAnalyzer(_config) };
            }
        }

#pragma warning disable RS1036 // analyzer de teste, não empacotado
#pragma warning disable RS2008 // analyzer release tracking não se aplica a um descriptor de teste
        [DiagnosticAnalyzer(LanguageNames.CSharp)]
        internal sealed class MustRouteThroughHostAnalyzer : DiagnosticAnalyzer
        {
            internal static readonly DiagnosticDescriptor TestSlot = new DiagnosticDescriptor(
                id: TestSlotId,
                title: "Violação de regra arquitetural (slot de teste)",
                messageFormat: "{0}",
                category: "Architecture",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            private readonly ArchConfig _config;
            private readonly IRuleEvaluator _evaluator = new MustRouteThroughEvaluator();

            public MustRouteThroughHostAnalyzer()
                : this(null)
            {
            }

            internal MustRouteThroughHostAnalyzer(ArchConfig config)
            {
                _config = config;
            }

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            {
                get { return ImmutableArray.Create(TestSlot); }
            }

            public override void Initialize(AnalysisContext context)
            {
                context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                context.EnableConcurrentExecution();
                context.RegisterCompilationStartAction(start =>
                {
                    if (_config == null)
                    {
                        return;
                    }

                    var resolver = new SuffixLayerResolver(_config);

                    foreach (var rule in _config.Rules)
                    {
                        if (string.Equals(rule.Type, _evaluator.RuleType, StringComparison.OrdinalIgnoreCase))
                        {
                            _evaluator.RegisterActions(start, rule, resolver, TestSlot);
                        }
                    }
                });
            }
        }
#pragma warning restore RS2008
#pragma warning restore RS1036

        private sealed class SuffixLayerResolver : ILayerResolver
        {
            private readonly ArchConfig _config;

            internal SuffixLayerResolver(ArchConfig config)
            {
                _config = config;
            }

            public IReadOnlyCollection<string> GetLayers(INamedTypeSymbol symbol)
            {
                var layers = new List<string>();
                if (symbol == null)
                {
                    return layers;
                }

                foreach (var layer in _config.Layers)
                {
                    if (Matches(layer.Value, symbol))
                    {
                        layers.Add(layer.Key);
                    }
                }

                return layers;
            }

            public bool IsInLayer(INamedTypeSymbol symbol, string layerName)
            {
                LayerDefinition layer;
                return symbol != null
                    && layerName != null
                    && _config.Layers.TryGetValue(layerName, out layer)
                    && Matches(layer, symbol);
            }

            private static bool Matches(LayerDefinition layer, INamedTypeSymbol symbol)
            {
                foreach (var criterion in layer.Match)
                {
                    if (!string.IsNullOrEmpty(criterion.NameSuffix)
                        && symbol.Name.EndsWith(criterion.NameSuffix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
