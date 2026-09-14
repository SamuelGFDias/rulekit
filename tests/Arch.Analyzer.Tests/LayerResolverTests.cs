using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Xunit;
using static Arch.Analyzer.Tests.TestConfigs;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Trilha 1B — <see cref="LayerResolver"/> contra símbolos reais de uma compilação em memória.
    /// A configuração é montada à mão (sem o parser YAML da trilha 1A), então o que está sob teste é
    /// exclusivamente a semântica de classificação do ADR-001 D2: critérios combinados por OR,
    /// `exclude` aplicado depois do match, e os globs de namespace do trie.
    /// </summary>
    public class LayerResolverTests
    {
        private const string Source = @"
using System;

namespace Acme.Framework
{
    public class ControllerBase { }
}

namespace Acme.Architecture
{
    public sealed class RepositoryAttribute : Attribute { }
}

namespace Acme.Application
{
    public interface IService { }
    public interface IAuditableService : IService { }
}

namespace Acme.Api.Controllers
{
    // Só o namespace casa: sem sufixo, sem base, sem interface.
    public class Ping { }
}

namespace Acme.Api.Controllers.V2
{
    public class Legacy { }
}

namespace Acme.Api
{
    // Está FORA de Acme.Api.Controllers.** — não pode virar Controller por namespace.
    public class Bootstrap { }
}

namespace Acme.Legacy
{
    public class ReportController { }                                   // casa por nameSuffix
    public class Dashboard : Acme.Framework.ControllerBase { }          // casa por baseType
    public class DeepDashboard : Dashboard { }                          // baseType transitivo
}

namespace Acme.Domain
{
    public class Orders : Acme.Application.IService { }                 // casa por implements
    public class Audit : Acme.Application.IAuditableService { }         // implements transitivo
    public class PriceService { }                                       // casa por nameSuffix
}

namespace Acme.Infra
{
    public class OrderRepository { }                                    // casa por nameSuffix
    [Acme.Architecture.Repository]
    public class OrderStore { }                                         // casa por attribute
}

namespace Acme.Web.Tests
{
    public class TempRepository { }                                     // casa e é excluída
}

namespace Acme.Deep.Nested.Tests
{
    public class OtherRepository { }                                    // `*` casa 1 segmento, não 2
}

namespace Acme.Api.Controllers.Hybrid
{
    public class BillingService : Acme.Application.IService { }         // pertence a 2 camadas
}
";

        private static readonly LayerDefinition[] Layers =
        {
            Layer("Controller", new[]
            {
                Namespace("Acme.Api.Controllers.**"),
                NameSuffix("Controller"),
                BaseType("Acme.Framework.ControllerBase"),
            }),
            Layer("Service", new[]
            {
                Implements("Acme.Application.IService"),
                NameSuffix("Service"),
            }),
            Layer("Repository", new[]
            {
                NameSuffix("Repository"),
                Attribute("Acme.Architecture.RepositoryAttribute"),
            },
            exclude: new[]
            {
                Namespace("Acme.*.Tests.**"),
            }),
        };

        private static (LayerResolver Resolver, Compilation Compilation, ImmutableArray<string> Unresolved) Build(
            LayerDefinition[] layers = null)
        {
            var compilation = TestCompilations.Create(Source);
            ImmutableArray<string> unresolved;
            var resolver = LayerResolver.Create(Policy(layers ?? Layers), compilation, out unresolved);
            return (resolver, compilation, unresolved);
        }

        private static INamedTypeSymbol Type(Compilation compilation, string metadataName)
        {
            var symbol = compilation.GetTypeByMetadataName(metadataName);
            Assert.True(symbol != null, "Tipo de exemplo não encontrado na compilação: " + metadataName);
            return symbol;
        }

        private static string[] LayersOf(LayerResolver resolver, Compilation compilation, string metadataName)
            => resolver.GetLayers(Type(compilation, metadataName)).ToArray();

        [Theory]
        // namespace: `**` casa o próprio nó e qualquer descendente
        [InlineData("Acme.Api.Controllers.Ping", "Controller")]
        [InlineData("Acme.Api.Controllers.V2.Legacy", "Controller")]
        // nameSuffix
        [InlineData("Acme.Legacy.ReportController", "Controller")]
        [InlineData("Acme.Domain.PriceService", "Service")]
        [InlineData("Acme.Infra.OrderRepository", "Repository")]
        // baseType (direto e transitivo)
        [InlineData("Acme.Legacy.Dashboard", "Controller")]
        [InlineData("Acme.Legacy.DeepDashboard", "Controller")]
        // implements (direto e via interface derivada)
        [InlineData("Acme.Domain.Orders", "Service")]
        [InlineData("Acme.Domain.Audit", "Service")]
        // attribute
        [InlineData("Acme.Infra.OrderStore", "Repository")]
        public void Classifica_simbolo_na_camada_esperada(string metadataName, string expectedLayer)
        {
            var (resolver, compilation, _) = Build();

            Assert.Equal(new[] { expectedLayer }, LayersOf(resolver, compilation, metadataName));
        }

        [Fact]
        public void Namespace_sem_curinga_nao_alcanca_descendentes_nem_ancestrais()
        {
            var (resolver, compilation, _) = Build();

            // Acme.Api NÃO está em Acme.Api.Controllers.** — `**` desce, nunca sobe.
            Assert.Empty(LayersOf(resolver, compilation, "Acme.Api.Bootstrap"));
        }

        [Fact]
        public void Exclude_vence_o_match()
        {
            var (resolver, compilation, _) = Build();

            // Casa `nameSuffix: Repository`, mas cai em `exclude: Acme.*.Tests.**`.
            Assert.Empty(LayersOf(resolver, compilation, "Acme.Web.Tests.TempRepository"));

            // Controle: mesmo sufixo, fora do exclude.
            Assert.Equal(new[] { "Repository" }, LayersOf(resolver, compilation, "Acme.Infra.OrderRepository"));
        }

        [Fact]
        public void Curinga_de_um_segmento_nao_atravessa_dois()
        {
            var (resolver, compilation, _) = Build();

            // `Acme.*.Tests.**` casa Acme.Web.Tests, mas NÃO Acme.Deep.Nested.Tests — `*` é
            // exatamente um segmento. Se o glob virasse "contém", este tipo sumiria da camada.
            Assert.Equal(new[] { "Repository" }, LayersOf(resolver, compilation, "Acme.Deep.Nested.Tests.OtherRepository"));
        }

        [Fact]
        public void Simbolo_pode_pertencer_a_mais_de_uma_camada()
        {
            var (resolver, compilation, _) = Build();

            var layers = LayersOf(resolver, compilation, "Acme.Api.Controllers.Hybrid.BillingService")
                .OrderBy(name => name, System.StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { "Controller", "Service" }, layers);
        }

        [Fact]
        public void IsInLayer_concorda_com_GetLayers()
        {
            var (resolver, compilation, _) = Build();

            var controller = Type(compilation, "Acme.Legacy.ReportController");
            var excluded = Type(compilation, "Acme.Web.Tests.TempRepository");

            Assert.True(resolver.IsInLayer(controller, "Controller"));
            Assert.False(resolver.IsInLayer(controller, "Repository"));
            Assert.False(resolver.IsInLayer(excluded, "Repository"));
            Assert.False(resolver.IsInLayer(controller, "CamadaQueNaoExiste"));
        }

        [Fact]
        public void Classificacao_e_memoizada_por_simbolo()
        {
            var (resolver, compilation, _) = Build();
            var symbol = Type(compilation, "Acme.Infra.OrderRepository");

            var first = resolver.GetLayers(symbol);
            var second = resolver.GetLayers(symbol);

            // Mesma INSTÂNCIA de coleção: a segunda consulta não reclassificou nem realocou nada.
            // É o requisito de performance do ADR-001 §5 (memoização com SymbolEqualityComparer),
            // e o que garante que IsInLayer no caminho quente não aloca.
            Assert.Same(first, second);
        }

        [Fact]
        public void Simbolos_equivalentes_mas_de_instancias_diferentes_classificam_igual()
        {
            // Duas instâncias DISTINTAS de INamedTypeSymbol que SymbolEqualityComparer.Default
            // considera iguais (variam só na anotação de nulidade). Com comparação por referência
            // — o default de um ConcurrentDictionary — elas seriam duas chaves diferentes.
            const string nullableSource = @"
#nullable enable
namespace Acme.Infra
{
    public class CacheRepository<T> { }
    public class Holder
    {
        public CacheRepository<string> A = null!;
        public CacheRepository<string?> B = null!;
    }
}
";
            var compilation = TestCompilations.Create(nullableSource);
            ImmutableArray<string> unresolved;
            var resolver = LayerResolver.Create(
                Policy(new[] { Layer("Repository", new[] { NameSuffix("Repository") }) }),
                compilation,
                out unresolved);

            var holder = compilation.GetTypeByMetadataName("Acme.Infra.Holder");
            var a = (INamedTypeSymbol)((IFieldSymbol)holder.GetMembers("A").Single()).Type;
            var b = (INamedTypeSymbol)((IFieldSymbol)holder.GetMembers("B").Single()).Type;

            // Anotações de nulidade diferentes ⇒ são objetos distintos...
            Assert.NotEqual(a.TypeArguments[0].NullableAnnotation, b.TypeArguments[0].NullableAnnotation);
            // ...que SymbolEqualityComparer.Default considera o mesmo tipo.
            Assert.True(SymbolEqualityComparer.Default.Equals(a, b));
            Assert.Same(resolver.GetLayers(a), resolver.GetLayers(b));
            Assert.Equal(new[] { "Repository" }, resolver.GetLayers(a).ToArray());
        }

        [Fact]
        public void Generico_construido_classifica_como_a_definicao_original()
        {
            const string genericSource = @"
namespace Acme.Infra
{
    public class CacheRepository<T> { }
    public class Consumer { public CacheRepository<string> Field; }
}
";
            var compilation = TestCompilations.Create(genericSource);
            ImmutableArray<string> unresolved;
            var resolver = LayerResolver.Create(
                Policy(new[] { Layer("Repository", new[] { NameSuffix("Repository") }) }),
                compilation,
                out unresolved);

            var definition = compilation.GetTypeByMetadataName("Acme.Infra.CacheRepository`1");
            var consumer = compilation.GetTypeByMetadataName("Acme.Infra.Consumer");
            var constructed = (INamedTypeSymbol)((IFieldSymbol)consumer.GetMembers("Field").Single()).Type;

            Assert.Equal(new[] { "Repository" }, resolver.GetLayers(constructed).ToArray());

            // Normalizado para a definição original: uma entrada de memo por tipo, não por
            // instanciação genérica.
            Assert.Same(resolver.GetLayers(definition), resolver.GetLayers(constructed));
        }

        [Fact]
        public void Nome_nao_resolvido_sai_uma_unica_vez_e_nao_derruba_os_demais_criterios()
        {
            var layers = new[]
            {
                Layer("Service", new[]
                {
                    // Citado duas vezes (duas camadas) de propósito: precisa sair UMA vez só.
                    Implements("Acme.Inexistente.IFantasma"),
                    NameSuffix("Service"),
                }),
                Layer("Repository", new[]
                {
                    BaseType("Acme.Inexistente.IFantasma"),
                    NameSuffix("Repository"),
                }),
            };

            var (resolver, compilation, unresolved) = Build(layers);

            Assert.Equal(new[] { "Acme.Inexistente.IFantasma" }, unresolved.ToArray());

            // O critério inválido some; os demais critérios da mesma camada continuam valendo.
            Assert.Equal(new[] { "Service" }, LayersOf(resolver, compilation, "Acme.Domain.PriceService"));
            Assert.Equal(new[] { "Repository" }, LayersOf(resolver, compilation, "Acme.Infra.OrderRepository"));
        }

        [Fact]
        public void Camada_sem_criterio_nao_engole_todos_os_simbolos()
        {
            var (resolver, compilation, _) = Build(new[]
            {
                Layer("Vazia", new LayerMatchCriterion[0]),
            });

            Assert.Empty(LayersOf(resolver, compilation, "Acme.Infra.OrderRepository"));
        }

        [Fact]
        public void Tipo_no_namespace_global_nao_quebra_a_classificacao()
        {
            var compilation = TestCompilations.Create("public class RootRepository { }");
            ImmutableArray<string> unresolved;
            var resolver = LayerResolver.Create(
                Policy(new[]
                {
                    Layer("Repository", new[] { Namespace("Acme.**"), NameSuffix("Repository") }),
                }),
                compilation,
                out unresolved);

            var symbol = compilation.GetTypeByMetadataName("RootRepository");

            Assert.Equal(new[] { "Repository" }, resolver.GetLayers(symbol).ToArray());
        }

        [Fact]
        public void Tipo_aninhado_herda_o_namespace_do_tipo_externo()
        {
            const string nestedSource = @"
namespace Acme.Api.Controllers
{
    public class Outer { public class Inner { } }
}
";
            var compilation = TestCompilations.Create(nestedSource);
            ImmutableArray<string> unresolved;
            var resolver = LayerResolver.Create(
                Policy(new[] { Layer("Controller", new[] { Namespace("Acme.Api.Controllers.**") }) }),
                compilation,
                out unresolved);

            var inner = compilation.GetTypeByMetadataName("Acme.Api.Controllers.Outer+Inner");

            Assert.Equal(new[] { "Controller" }, resolver.GetLayers(inner).ToArray());
        }
    }
}
