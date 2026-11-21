using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Xunit;
using static Arch.Analyzer.Tests.TestConfigs;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Issue #24 — capturas <c>[Nome]</c> no <see cref="LayerResolver"/> (<c>GetCaptures</c>) e o
    /// efeito de <c>when: { same | differ }</c> ponta a ponta (YAML real → analyzer real).
    /// </summary>
    public class PlaceholderCaptureTests
    {
        private const string Source = @"
namespace Acme.Vendas.Services { public class PedidoService { } }
namespace Acme.Vendas.Domain { public class Pedido { } }
namespace Acme.Estoque.Domain { public class Item { } }
namespace Acme.Estoque.Services { public class ItemService { } }
namespace Acme.Vendas.Domain.Deep.Nested { public class Nested { } }
namespace Acme.Outro { public class Solto { } }
";

        private static (LayerResolver Resolver, Compilation Compilation) Build(params LayerDefinition[] layers)
        {
            var compilation = TestCompilations.Create(Source);
            ImmutableArray<string> unresolved;
            return (LayerResolver.Create(Policy(layers), compilation, out unresolved), compilation);
        }

        private static IReadOnlyDictionary<string, string> Captures(
            LayerResolver resolver, Compilation compilation, string type, string layer)
            => resolver.GetCaptures(compilation.GetTypeByMetadataName(type), layer);

        [Fact]
        public void Captura_um_segmento_de_namespace_por_nome()
        {
            var (resolver, compilation) = Build(
                Layer("Services", new[] { Namespace("Acme.[Module].Services.**") }));

            var captures = Captures(resolver, compilation, "Acme.Vendas.Services.PedidoService", "Services");

            Assert.Equal("Vendas", Assert.Single(captures).Value);
            Assert.Equal("Module", captures.Keys.Single());
        }

        [Fact]
        public void Placeholder_casa_exatamente_um_segmento()
        {
            var (resolver, compilation) = Build(
                Layer("Dominio", new[] { Namespace("Acme.[Module]") }));

            // Acme.Vendas.Domain tem dois segmentos depois de Acme: [Module] sozinho não casa.
            Assert.Empty(resolver.GetLayers(compilation.GetTypeByMetadataName("Acme.Vendas.Domain.Pedido")));
            Assert.Equal(new[] { "Dominio" }, resolver.GetLayers(compilation.GetTypeByMetadataName("Acme.Outro.Solto")).ToArray());
        }

        [Fact]
        public void Placeholder_combina_com_duplo_asterisco_e_curinga()
        {
            var (resolver, compilation) = Build(
                Layer("Dominio", new[] { Namespace("Acme.[Module].Domain.**") }),
                Layer("Qualquer", new[] { Namespace("Acme.*.[Area].**") }));

            Assert.Equal("Vendas", Captures(resolver, compilation, "Acme.Vendas.Domain.Deep.Nested.Nested", "Dominio")["Module"]);
            Assert.Equal("Vendas", Captures(resolver, compilation, "Acme.Vendas.Domain.Pedido", "Dominio")["Module"]);
            Assert.Equal("Domain", Captures(resolver, compilation, "Acme.Vendas.Domain.Pedido", "Qualquer")["Area"]);
        }

        [Fact]
        public void Captura_de_varios_placeholders_no_mesmo_padrao()
        {
            var (resolver, compilation) = Build(
                Layer("L", new[] { Namespace("Acme.[Module].[Kind].**") }));

            var captures = Captures(resolver, compilation, "Acme.Vendas.Domain.Pedido", "L");

            Assert.Equal("Vendas", captures["Module"]);
            Assert.Equal("Domain", captures["Kind"]);
            Assert.Equal(2, captures.Count);
        }

        [Fact]
        public void Captura_funciona_em_assembly()
        {
            var lib = TestCompilations.Create("namespace N { public class Tipo { } }", "Acme.Vendas.Core");
            var app = TestCompilations.Create("namespace M { public class Local { } }", "App", lib.ToMetadataReference());
            ImmutableArray<string> unresolved;
            var resolver = LayerResolver.Create(
                Policy(new[] { Layer("Core", new[] { Assembly("Acme.[Module].Core") }) }), app, out unresolved);

            var captures = resolver.GetCaptures(app.GetTypeByMetadataName("N.Tipo"), "Core");

            Assert.Equal("Vendas", captures["Module"]);
        }

        [Fact]
        public void Vale_o_primeiro_criterio_de_match_que_casar_na_ordem_declarada()
        {
            var (resolver, compilation) = Build(
                Layer("L", new[]
                {
                    Namespace("Acme.[First].Domain.**"),
                    Namespace("Acme.[Second].**"),
                }));

            var captures = Captures(resolver, compilation, "Acme.Vendas.Domain.Pedido", "L");

            Assert.Equal(new[] { "First" }, captures.Keys.ToArray());
            Assert.Equal("Vendas", captures["First"]);

            // Só o segundo critério casa: a captura vem dele.
            var other = Captures(resolver, compilation, "Acme.Vendas.Services.PedidoService", "L");
            Assert.Equal(new[] { "Second" }, other.Keys.ToArray());
        }

        [Fact]
        public void Exclude_nao_captura_e_tipo_excluido_nao_pertence_a_camada()
        {
            var (resolver, compilation) = Build(
                Layer(
                    "L",
                    new[] { Namespace("Acme.[Module].**") },
                    new[] { Namespace("Acme.[Excluido].Services.**") }));

            Assert.Empty(Captures(resolver, compilation, "Acme.Vendas.Services.PedidoService", "L"));
            Assert.Equal("Vendas", Captures(resolver, compilation, "Acme.Vendas.Domain.Pedido", "L")["Module"]);
        }

        [Fact]
        public void Dicionario_vazio_sem_captura_camada_inexistente_ou_tipo_fora_da_camada()
        {
            var (resolver, compilation) = Build(
                Layer("Sem", new[] { Namespace("Acme.Vendas.**") }),
                Layer("Com", new[] { Namespace("Acme.[Module].Domain.**") }));

            Assert.Empty(Captures(resolver, compilation, "Acme.Vendas.Domain.Pedido", "Sem"));
            Assert.Empty(Captures(resolver, compilation, "Acme.Vendas.Domain.Pedido", "NaoExiste"));
            Assert.Empty(Captures(resolver, compilation, "Acme.Outro.Solto", "Com"));
            Assert.NotNull(resolver.GetCaptures(null, "Com"));
        }

        [Fact]
        public void Capturas_sao_por_camada()
        {
            var (resolver, compilation) = Build(
                Layer("A", new[] { Namespace("Acme.[Module].Domain.**") }),
                Layer("B", new[] { Namespace("Acme.Vendas.[Parte].**") }));

            var type = "Acme.Vendas.Domain.Pedido";

            Assert.Equal(new[] { "Module" }, Captures(resolver, compilation, type, "A").Keys.ToArray());
            Assert.Equal(new[] { "Parte" }, Captures(resolver, compilation, type, "B").Keys.ToArray());
        }

        [Fact]
        public void Capturas_sao_memoizadas_por_simbolo()
        {
            var (resolver, compilation) = Build(
                Layer("A", new[] { Namespace("Acme.[Module].Domain.**") }));
            var symbol = compilation.GetTypeByMetadataName("Acme.Vendas.Domain.Pedido");

            Assert.Same(resolver.GetCaptures(symbol, "A"), resolver.GetCaptures(symbol, "A"));
            Assert.Same(resolver.GetLayers(symbol), resolver.GetLayers(symbol));
        }

        // ---- ponta a ponta: when same/differ --------------------------------------------------

        private const string CallsSource = Source + @"
namespace Acme.Vendas.Services
{
    public class Mesmo { public void Run(Acme.Vendas.Domain.Pedido p) { var x = new Acme.Vendas.Domain.Pedido(); } }
    public class Cruzado { public void Run() { var x = new Acme.Estoque.Domain.Item(); } }
}
namespace Acme.Outro
{
    public class SemCaptura { public void Run() { var x = new Acme.Estoque.Domain.Item(); } }
}
";

        private static string Yaml(string when) => $@"schema: arch-rules/v1
layers:
  ModuleServices:
    match: [ {{ namespace: ""Acme.[Module].Services.**"" }} ]
  ModuleDomain:
    match: [ {{ namespace: ""Acme.[Module].Domain.**"" }} ]
  Solta:
    match: [ {{ namespace: ""Acme.Outro.**"" }} ]
rules:
  - id: ACME-010
    slot: ARCH0010
    type: forbidden-call
    from: {{FROM}}
    to: ModuleDomain
{(when == null ? string.Empty : "    when: " + when + "\n")}";

        private static async Task<string[]> RunAsync(string yaml, string from = "ModuleServices")
        {
            var text = yaml.Replace("{FROM}", from);
            var analyzer = new ArchRulesAnalyzer(
                RuleEvaluatorRegistry.Default,
                (IReadOnlyList<string> texts) => ArchConfigParser.Parse(texts));
            var diagnostics = await AnalyzerRunner.RunAsync(
                analyzer,
                TestCompilations.Create(CallsSource),
                new InMemoryAdditionalText("/proj/arch-rules.yaml", text));

            return diagnostics
                .Where(d => d.Id == "ARCH0010")
                .Select(d => d.Location.GetLineSpan().StartLinePosition.Line + ":" + d.GetMessage())
                .ToArray();
        }

        private static int ClassOf(string name)
            => CallsSource.Split('\n').ToList().FindIndex(l => l.Contains("class " + name));

        [Fact]
        public async Task Sem_when_o_comportamento_atual_se_mantem()
        {
            var reports = await RunAsync(Yaml(null));

            Assert.Equal(2, reports.Length); // Mesmo (new Pedido) e Cruzado (new Item)
        }

        [Fact]
        public async Task Differ_so_reporta_quando_o_modulo_difere()
        {
            var reports = await RunAsync(Yaml("{ differ: [Module] }"));

            var report = Assert.Single(reports);
            Assert.StartsWith(ClassOf("Cruzado") + ":", report);
        }

        [Fact]
        public async Task Same_so_reporta_quando_o_modulo_e_o_mesmo()
        {
            var reports = await RunAsync(Yaml("{ same: [Module] }"));

            var report = Assert.Single(reports);
            Assert.StartsWith(ClassOf("Mesmo") + ":", report);
        }

        [Fact]
        public async Task Lado_sem_a_captura_nao_reporta_o_par()
        {
            // Origem em Acme.Outro.** (camada Solta, sem captura): nem same nem differ se aplicam.
            Assert.Empty(await RunAsync(Yaml("{ differ: [Module] }"), from: "Solta"));
            Assert.Empty(await RunAsync(Yaml("{ same: [Module] }"), from: "Solta"));
            // Controle: sem when a regra reporta o par.
            Assert.Single(await RunAsync(Yaml(null), from: "Solta"));
        }
    }
}
