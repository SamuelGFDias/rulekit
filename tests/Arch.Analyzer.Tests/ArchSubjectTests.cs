using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Issue #25 — todo diagnóstico de slot traz <c>archRuleId</c> e um <c>archSubject</c> estável (sem
    /// linha/coluna), pelo pipeline real (parser + analyzer + avaliadores).
    /// </summary>
    public class ArchSubjectTests
    {
        private const string Header = @"schema: arch-rules/v1
layers:
  Controller:
    match: [ { nameSuffix: Controller } ]
  Repository:
    match: [ { nameSuffix: Repository } ]
severities:
  aviso: warning
defaults:
  severity: aviso
rules:
";

        private static async Task<ImmutableArray<Diagnostic>> RunAsync(string source, string rules)
        {
            var diagnostics = await AnalyzerRunner.RunAsync(
                new ArchRulesAnalyzer(),
                TestCompilations.Create(source),
                new InMemoryAdditionalText("/repo/arch-rules.yaml", Header + rules));
            return diagnostics.Where(d => d.Id.StartsWith("ARCH0")).ToImmutableArray();
        }

        [Fact]
        public async Task ForbiddenCall_invocacao_tem_origem_seta_alvo_ponto_membro()
        {
            const string source = @"
namespace Acme.Data { public class OrderRepository { public static void Load() { } } }
namespace Acme.Api { public class OrderController { public void Get() { Acme.Data.OrderRepository.Load(); } } }";

            var d = Assert.Single(await RunAsync(source, @"  - { id: ACME-001, slot: ARCH0001, type: forbidden-call, from: Controller, to: Repository }
"));

            Assert.Equal("ACME-001", d.Properties["archRuleId"]);
            Assert.Equal("Acme.Api.OrderController -> Acme.Data.OrderRepository.Load", d.Properties["archSubject"]);
        }

        [Fact]
        public async Task ForbiddenCall_criacao_de_objeto_usa_so_o_tipo_alvo()
        {
            const string source = @"
public class OrderRepository { }
public class OrderController { public object Get() { return new OrderRepository(); } }";

            var d = Assert.Single(await RunAsync(source, @"  - { id: ACME-001, slot: ARCH0001, type: forbidden-call, from: Controller, to: Repository }
"));

            Assert.Equal("OrderController -> OrderRepository", d.Properties["archSubject"]);
        }

        [Fact]
        public async Task ForbiddenCall_propriedade_e_campo_usam_o_nome_do_membro_e_tipos_aninhados_genericos_sao_qualificados()
        {
            const string source = @"
namespace N
{
    public class Outer { public class CacheRepository<T> { public int Count => 0; public string Name = """"; } }
    public class Holder { public class WidgetController { public int A(Outer.CacheRepository<int> r) { return r.Count + r.Name.Length; } } }
}";

            var diagnostics = await RunAsync(source, @"  - { id: ACME-001, slot: ARCH0001, type: forbidden-call, from: Controller, to: Repository }
");

            Assert.Equal(
                new[]
                {
                    "N.Holder.WidgetController -> N.Outer.CacheRepository<T>.Count",
                    "N.Holder.WidgetController -> N.Outer.CacheRepository<T>.Name",
                },
                diagnostics.Select(d => d.Properties["archSubject"]).OrderBy(s => s, System.StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public async Task MustRouteThrough_tem_o_mesmo_assunto_do_forbidden_call()
        {
            const string source = @"
public class OrderRepository { public static void Load() { } }
public class OrderController { public void Get() { OrderRepository.Load(); } }";

            var d = Assert.Single(await RunAsync(source, @"  - { id: ACME-002, slot: ARCH0002, type: must-route-through, from: Controller, to: Repository, through: [Controller] }
"));

            Assert.Equal("ACME-002", d.Properties["archRuleId"]);
            Assert.Equal("OrderController -> OrderRepository.Load", d.Properties["archSubject"]);
        }

        [Fact]
        public async Task Assunto_nao_depende_da_linha_do_codigo()
        {
            const string a = "public class OrderRepository { public static void Load() { } }\npublic class OrderController { public void Get() { OrderRepository.Load(); } }";
            const string b = "\n\n\n// deslocado\n" + a;
            const string rule = "  - { id: ACME-001, slot: ARCH0001, type: forbidden-call, from: Controller, to: Repository }\n";

            var da = Assert.Single(await RunAsync(a, rule));
            var db = Assert.Single(await RunAsync(b, rule));

            Assert.Equal(da.Properties["archSubject"], db.Properties["archSubject"]);
            Assert.NotEqual(da.Location.GetLineSpan().StartLinePosition.Line, db.Location.GetLineSpan().StartLinePosition.Line);
        }

        [Fact]
        public async Task NamingConvention_tem_regra_e_o_tipo_qualificado()
        {
            const string source = "namespace Acme.Data { public class OrderStore { } }";
            const string rules = "  - { id: ACME-003, slot: ARCH0003, type: naming-convention, layer: Store, require: { suffix: Repository } }\n";
            var yaml = Header.Replace("severities:", "  Store:\n    match: [ { nameSuffix: Store } ]\nseverities:") + rules;

            var diagnostics = await AnalyzerRunner.RunAsync(
                new ArchRulesAnalyzer(),
                TestCompilations.Create(source),
                new InMemoryAdditionalText("/repo/arch-rules.yaml", yaml));
            var d = Assert.Single(diagnostics, x => x.Id == "ARCH0003");

            Assert.Equal("ACME-003", d.Properties["archRuleId"]);
            Assert.Equal("naming-convention", d.Properties["archRuleType"]);
            Assert.Equal("Acme.Data.OrderStore", d.Properties["archSubject"]);
        }

        [Fact]
        public async Task MaxDependencies_tem_regra_e_o_tipo_qualificado()
        {
            const string source = "namespace Acme.Api { public class FatController { public FatController(object a, string b, System.IO.Stream c) { } } }";
            const string rules = "  - { id: ACME-004, slot: ARCH0004, type: max-dependencies, layer: Controller, countBy: constructor-parameters, max: 1 }\n";

            var d = Assert.Single(await RunAsync(source, rules));

            Assert.Equal("ACME-004", d.Properties["archRuleId"]);
            Assert.Equal("Acme.Api.FatController", d.Properties["archSubject"]);
        }
    }
}
