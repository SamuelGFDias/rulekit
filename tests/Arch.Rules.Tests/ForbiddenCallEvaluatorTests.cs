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
    /// Trilha 2A — semântica de `forbidden-call` (ADR-001 D2) contra compilações C# reais.
    ///
    /// <para>O que este arquivo NÃO exercita, de propósito: o parser YAML da trilha 1A (a
    /// <see cref="ArchConfig"/> é montada em memória), o <c>RuleEvaluatorRegistry</c> (é
    /// <c>internal</c> em Arch.Analyzer e não está acessível a este projeto) e o pool real de 512
    /// descriptors (um <see cref="DiagnosticDescriptor"/> próprio basta — o que está sob teste é a
    /// DETECÇÃO, e o pipeline motor→slot já foi provado na Fase 1B por
    /// <c>ArchRulesAnalyzerPipelineTests</c>). O descriptor de teste replica o único detalhe de
    /// contrato do pool que importa aqui: <c>messageFormat: "{0}"</c> — um argumento só, a mensagem
    /// já composta.</para>
    /// </summary>
    public class ForbiddenCallEvaluatorTests
    {
        private const string RuleId = "ACME-001";
        private const string TestSlotId = "ARCHTEST01";

        // O motor real de camadas (LayerResolver/NamespaceTrie) é da Fase 1B e já tem os seus
        // próprios testes; aqui um resolvedor por sufixo de nome mantém o foco no avaliador.
        private const string ControllerLayer = "Controller";
        private const string ServiceLayer = "Service";
        private const string RepositoryLayer = "Repository";

        [Fact]
        public void RuleType_e_forbidden_call()
        {
            Assert.Equal("forbidden-call", new ForbiddenCallEvaluator().RuleType);
            Assert.Equal(ForbiddenCallEvaluator.TypeName, new ForbiddenCallEvaluator().RuleType);
        }

        /// <summary>Caso central: Controller alcançando Repository sem passar por ninguém.</summary>
        [Fact]
        public async Task Controller_chamando_Repository_reporta()
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
                Rule(ControllerLayer, RepositoryLayer),
                Expect(0, Message(ControllerLayer, RepositoryLayer, "OrderRepository.Load")));
        }

        /// <summary>
        /// Controle de falso positivo: o destino não é a camada `to`. Mesmo formato de chamada do
        /// teste acima — muda só a camada do alvo.
        /// </summary>
        [Fact]
        public async Task Controller_chamando_Service_nao_reporta()
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

            await VerifyAsync(source, Rule(ControllerLayer, RepositoryLayer));
        }

        /// <summary>Controle de falso positivo simétrico: a origem não é a camada `from`.</summary>
        [Fact]
        public async Task Service_chamando_Repository_nao_reporta()
        {
            const string source = @"
public class OrderRepository
{
    public void Load() { }
}

public class OrderService
{
    public void Do(OrderRepository repo)
    {
        repo.Load();
    }
}
";

            await VerifyAsync(source, Rule(ControllerLayer, RepositoryLayer));
        }

        /// <summary>OperationKind.ObjectCreation — `new Repository()` também cruza a camada.</summary>
        [Fact]
        public async Task ObjectCreation_de_Repository_em_Controller_reporta()
        {
            const string source = @"
public class OrderRepository
{
    public void Load() { }
}

public class OrderController
{
    public OrderRepository Create()
    {
        return {|#0:new OrderRepository()|};
    }
}
";

            await VerifyAsync(
                source,
                Rule(ControllerLayer, RepositoryLayer),
                Expect(0, Message(ControllerLayer, RepositoryLayer, "new OrderRepository")));
        }

        /// <summary>OperationKind.PropertyReference.</summary>
        [Fact]
        public async Task PropertyReference_de_Repository_em_Controller_reporta()
        {
            const string source = @"
public class OrderRepository
{
    public int Count { get; set; }
}

public class OrderController
{
    public int Read(OrderRepository repo)
    {
        return {|#0:repo.Count|};
    }
}
";

            await VerifyAsync(
                source,
                Rule(ControllerLayer, RepositoryLayer),
                Expect(0, Message(ControllerLayer, RepositoryLayer, "OrderRepository.Count")));
        }

        /// <summary>OperationKind.FieldReference.</summary>
        [Fact]
        public async Task FieldReference_de_Repository_em_Controller_reporta()
        {
            const string source = @"
public class OrderRepository
{
    public string Name = string.Empty;
}

public class OrderController
{
    public string Read(OrderRepository repo)
    {
        return {|#0:repo.Name|};
    }
}
";

            await VerifyAsync(
                source,
                Rule(ControllerLayer, RepositoryLayer),
                Expect(0, Message(ControllerLayer, RepositoryLayer, "OrderRepository.Name")));
        }

        /// <summary>
        /// Regra com `from` e `to` na MESMA camada (o caso em que o guarda de mesmo tipo é a única
        /// coisa que separa política de ruído): uma classe chamando a si mesma — por `this` implícito
        /// ou por outra instância do próprio tipo — não cruza camada nenhuma. Duas classes distintas
        /// da mesma camada, sim.
        /// </summary>
        [Fact]
        public async Task Repository_chamando_a_si_mesmo_nao_reporta()
        {
            const string source = @"
public class OrderRepository
{
    public void Load() { }

    public void Reload()
    {
        Load();
    }

    public void Forward(OrderRepository other)
    {
        other.Load();
    }
}

public class UserRepository
{
    public void Touch(OrderRepository repo)
    {
        {|#0:repo.Load()|};
    }
}
";

            await VerifyAsync(
                source,
                Rule(RepositoryLayer, RepositoryLayer),
                Expect(0, Message(RepositoryLayer, RepositoryLayer, "OrderRepository.Load")));
        }

        /// <summary>
        /// Prova da resolução de tipo continente: dentro de uma função local o
        /// <c>ContainingSymbol</c> da operação é o IMethodSymbol da PRÓPRIA função local, não o tipo;
        /// dentro de um lambda, o método que o declara. Nos dois casos quem "faz a chamada", para
        /// efeito de arquitetura, é o Controller.
        /// </summary>
        [Fact]
        public async Task Metodo_local_e_lambda_dentro_do_Controller_reportam()
        {
            const string source = @"
using System;

public class OrderRepository
{
    public void Load() { }
}

public class OrderController
{
    public void Get(OrderRepository repo)
    {
        void Local()
        {
            {|#0:repo.Load()|};
        }

        Action lambda = () => {|#1:repo.Load()|};

        Local();
        lambda();
    }
}
";

            var expectedMessage = Message(ControllerLayer, RepositoryLayer, "OrderRepository.Load");

            await VerifyAsync(
                source,
                Rule(ControllerLayer, RepositoryLayer),
                Expect(0, expectedMessage),
                Expect(1, expectedMessage));
        }

        /// <summary>
        /// `nameof(Repo.Prop)` produz um IPropertyReferenceOperation real, mas é constante de
        /// compilação — não há acesso nenhum em runtime. Reportar aqui seria um falso positivo
        /// especialmente irritante, porque o padrão aparece justamente em código de logging e de
        /// validação escrito por quem está tentando respeitar a arquitetura.
        /// </summary>
        [Fact]
        public async Task Nameof_de_membro_de_Repository_nao_reporta()
        {
            const string source = @"
public class OrderRepository
{
    public int Count { get; set; }
}

public class OrderController
{
    public string Describe()
    {
        return nameof(OrderRepository.Count);
    }
}
";

            await VerifyAsync(source, Rule(ControllerLayer, RepositoryLayer));
        }

        /// <summary>
        /// `message:` do YAML vence o template default; o ID de negócio continua sendo prefixado pelo
        /// motor (ADR-001 §3 — o slot técnico é o que a Error List mostra, o ID de negócio não pode
        /// depender de o consumidor lembrar de escrevê-lo). Placeholder desconhecido fica literal em
        /// vez de virar exceção dentro do analyzer.
        /// </summary>
        [Fact]
        public async Task Message_template_da_regra_substitui_placeholders()
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

            var rule = Rule(
                ControllerLayer,
                RepositoryLayer,
                messageTemplate: "{from} não pode chamar {to} diretamente ({symbol}). Passe por um Service. [{id}] {naoExiste}");

            await VerifyAsync(
                source,
                rule,
                Expect(
                    0,
                    RuleId + ": Controller não pode chamar Repository diretamente (OrderRepository.Load). " +
                    "Passe por um Service. [" + RuleId + "] {naoExiste}"));
        }

        /// <summary>
        /// Regra sem `from`/`to` não tem semântica em D2 e o avaliador não tem canal para reportar
        /// erro de configuração: registrar nada é a falha segura. O oposto (tratar `from` ausente como
        /// "qualquer origem") transformaria um YAML incompleto em avalanche de diagnósticos.
        /// </summary>
        [Theory]
        [InlineData(ControllerLayer, null)]
        [InlineData(null, RepositoryLayer)]
        [InlineData(null, null)]
        [InlineData(ControllerLayer, "")]
        public async Task Regra_sem_from_ou_to_nao_registra_nenhuma_acao(string from, string to)
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
        repo.Load();
    }
}
";

            await VerifyAsync(source, Rule(from, to));
        }

        // ------------------------------------------------------------------
        // Infra do teste
        // ------------------------------------------------------------------

        private static string Message(string fromLayer, string toLayer, string symbol)
        {
            return RuleId + ": " + fromLayer + " não pode acessar " + toLayer + " diretamente (" + symbol + ").";
        }

        private static DiagnosticResult Expect(int markupLocation, string message)
        {
            return new DiagnosticResult(TestSlotId, DiagnosticSeverity.Warning)
                .WithLocation(markupLocation)
                .WithArguments(message);
        }

        private static RuleDefinition Rule(string fromLayer, string toLayer, string messageTemplate = null)
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

            return new RuleDefinition(
                RuleId,
                "ARCH0007",
                ForbiddenCallEvaluator.TypeName,
                enabled: true,
                severityName: "restrita",
                messageTemplate: messageTemplate,
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

        /// <summary>
        /// <see cref="CSharpAnalyzerTest{TAnalyzer, TVerifier}"/> exige um analyzer com construtor
        /// público sem parâmetros; a política de cada teste chega por
        /// <c>GetDiagnosticAnalyzers</c> (mesmo padrão já usado em
        /// <c>Arch.Analyzer.Tests.AnalyzerConfigFilesTests</c>), o que evita estado estático
        /// compartilhado entre testes rodando em paralelo.
        /// </summary>
        private sealed class Harness : CSharpAnalyzerTest<ForbiddenCallHostAnalyzer, DefaultVerifier>
        {
            private readonly ArchConfig _config;

            internal Harness(ArchConfig config)
            {
                _config = config;
            }

            protected override IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers()
            {
                return new DiagnosticAnalyzer[] { new ForbiddenCallHostAnalyzer(_config) };
            }
        }

        /// <summary>
        /// Analyzer mínimo de teste: faz o papel do <c>ArchRulesAnalyzer</c> real (incluindo o
        /// <c>ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)</c>, que é o que
        /// dispensa o avaliador de filtrar código gerado por conta própria) e chama
        /// <see cref="IRuleEvaluator.RegisterActions"/> direto, sem passar pelo
        /// <c>RuleEvaluatorRegistry</c> (internal a Arch.Analyzer).
        /// </summary>
#pragma warning disable RS1036 // analyzer de teste, não empacotado
#pragma warning disable RS2008 // analyzer release tracking não se aplica a um descriptor de teste
        [DiagnosticAnalyzer(LanguageNames.CSharp)]
        internal sealed class ForbiddenCallHostAnalyzer : DiagnosticAnalyzer
        {
            /// <summary>
            /// Réplica do contrato do pool real no ponto que importa para este teste:
            /// <c>messageFormat: "{0}"</c> — o descriptor é pré-declarado antes de o YAML existir e
            /// portanto só pode carregar a mensagem já composta pelo avaliador.
            /// </summary>
            internal static readonly DiagnosticDescriptor TestSlot = new DiagnosticDescriptor(
                id: TestSlotId,
                title: "Violação de regra arquitetural (slot de teste)",
                messageFormat: "{0}",
                category: "Architecture",
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            private readonly ArchConfig _config;
            private readonly IRuleEvaluator _evaluator = new ForbiddenCallEvaluator();

            public ForbiddenCallHostAnalyzer()
                : this(null)
            {
            }

            internal ForbiddenCallHostAnalyzer(ArchConfig config)
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

        /// <summary>
        /// <see cref="ILayerResolver"/> de teste: classifica pelo critério `nameSuffix` das
        /// <see cref="LayerDefinition"/> montadas em memória. Suficiente e deliberado — o resolvedor
        /// real (trie de namespaces, baseType/implements/attribute, `exclude`) é da Fase 1B e tem os
        /// seus próprios testes; misturá-lo aqui faria estes testes falharem por motivos que não são
        /// do avaliador.
        /// </summary>
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
