using System.Collections.Immutable;
using System.Linq;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Xunit;
using static Arch.Analyzer.Tests.TestConfigs;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Issue #23 — critério de camada <c>assembly</c> no <see cref="LayerResolver"/>: comparado com
    /// <c>ContainingAssembly.Identity.Name</c>, com a mesma semântica de curinga do namespace.
    /// </summary>
    public class AssemblyCriterionTests
    {
        private const string LibSource = @"
namespace Acme.Domain { public class Order { } }
namespace Acme.Infrastructure.Data { public class SqlStore { } }
namespace Acme.Infrastructure.Data.Deep { public class DeepStore { } }
namespace Acme.Infrastructure { public class RootInfra { } }
";

        private static (LayerResolver Resolver, Compilation App) Build(
            LayerDefinition[] layers,
            string domainAssembly = "Acme.Domain",
            string infraAssembly = "Acme.Infrastructure.Data")
        {
            // Dois assemblies: um "domínio" e um "infra" referenciado pela compilação de teste, de
            // modo que parte dos tipos classificados venha de metadata (PE) e parte do código-fonte.
            var domain = TestCompilations.Create("namespace Acme.Domain { public class Order { } }", domainAssembly);
            var infra = TestCompilations.Create(
                @"
namespace Acme.Infrastructure.Data { public class SqlStore { } }
namespace Acme.Infrastructure.Data.Deep { public class DeepStore { } }
namespace Acme.Infrastructure { public class RootInfra { } }",
                infraAssembly);
            var app = TestCompilations.Create(
                "namespace Acme.App { public class Local { } }",
                "Acme.App",
                domain.ToMetadataReference(),
                infra.ToMetadataReference());

            ImmutableArray<string> unresolved;
            var resolver = LayerResolver.Create(Policy(layers), app, out unresolved);
            return (resolver, app);
        }

        private static string[] LayersOf(LayerResolver resolver, Compilation app, string metadataName)
        {
            var symbol = app.GetTypeByMetadataName(metadataName);
            Assert.True(symbol != null, "Tipo não encontrado: " + metadataName);
            return resolver.GetLayers(symbol).ToArray();
        }

        [Fact]
        public void Nome_exato_casa_so_aquele_assembly()
        {
            var (resolver, app) = Build(new[] { Layer("Domain", new[] { Assembly("Acme.Domain") }) });

            Assert.Equal(new[] { "Domain" }, LayersOf(resolver, app, "Acme.Domain.Order"));
            Assert.Empty(LayersOf(resolver, app, "Acme.Infrastructure.Data.SqlStore"));
            Assert.Empty(LayersOf(resolver, app, "Acme.App.Local"));
        }

        [Fact]
        public void Nome_sem_curinga_nao_casa_assembly_descendente()
        {
            var (resolver, app) = Build(
                new[] { Layer("Infra", new[] { Assembly("Acme.Infrastructure") }) });

            // O assembly é Acme.Infrastructure.Data — nome diferente, sem prefixo implícito.
            Assert.Empty(LayersOf(resolver, app, "Acme.Infrastructure.Data.SqlStore"));
        }

        [Fact]
        public void Duplo_asterisco_casa_zero_ou_mais_segmentos()
        {
            var layers = new[] { Layer("Infra", new[] { Assembly("Acme.Infrastructure.**") }) };

            var (resolver, app) = Build(layers);
            Assert.Equal(new[] { "Infra" }, LayersOf(resolver, app, "Acme.Infrastructure.Data.SqlStore"));

            // Zero segmentos: o próprio Acme.Infrastructure.
            var (resolverZero, appZero) = Build(layers, infraAssembly: "Acme.Infrastructure");
            Assert.Equal(new[] { "Infra" }, LayersOf(resolverZero, appZero, "Acme.Infrastructure.Data.SqlStore"));

            // Vários segmentos.
            var (resolverMany, appMany) = Build(layers, infraAssembly: "Acme.Infrastructure.A.B.C");
            Assert.Equal(new[] { "Infra" }, LayersOf(resolverMany, appMany, "Acme.Infrastructure.Data.SqlStore"));
        }

        [Fact]
        public void Asterisco_casa_exatamente_um_segmento()
        {
            var layers = new[] { Layer("Infra", new[] { Assembly("Acme.*.Data") }) };

            var (resolver, app) = Build(layers, infraAssembly: "Acme.Infrastructure.Data");
            Assert.Equal(new[] { "Infra" }, LayersOf(resolver, app, "Acme.Infrastructure.Data.SqlStore"));

            var (resolverTwo, appTwo) = Build(layers, infraAssembly: "Acme.Infra.Core.Data");
            Assert.Empty(LayersOf(resolverTwo, appTwo, "Acme.Infrastructure.Data.SqlStore"));
        }

        [Fact]
        public void Assembly_e_exclude_aplicado_depois_do_match()
        {
            var (resolver, app) = Build(new[]
            {
                Layer(
                    "Acme",
                    new[] { Assembly("Acme.**") },
                    new[] { Assembly("Acme.Infrastructure.**") }),
            });

            Assert.Equal(new[] { "Acme" }, LayersOf(resolver, app, "Acme.Domain.Order"));
            Assert.Empty(LayersOf(resolver, app, "Acme.Infrastructure.Data.SqlStore"));
        }

        [Fact]
        public void Assembly_combina_por_OU_com_namespace()
        {
            var (resolver, app) = Build(new[]
            {
                Layer("Mix", new[] { Assembly("Acme.Domain"), Namespace("Acme.App.**") }),
            });

            Assert.Equal(new[] { "Mix" }, LayersOf(resolver, app, "Acme.Domain.Order"));
            Assert.Equal(new[] { "Mix" }, LayersOf(resolver, app, "Acme.App.Local"));
            Assert.Empty(LayersOf(resolver, app, "Acme.Infrastructure.Data.SqlStore"));
        }

        [Fact]
        public void Criterio_assembly_vazio_nao_casa_nada()
        {
            var (resolver, app) = Build(new[] { Layer("Nada", new[] { Assembly("") }) });

            Assert.Empty(LayersOf(resolver, app, "Acme.Domain.Order"));
            Assert.Empty(LayersOf(resolver, app, "Acme.App.Local"));
        }

        [Fact]
        public void IsInLayer_concorda_para_tipo_de_metadata()
        {
            var (resolver, app) = Build(new[] { Layer("Domain", new[] { Assembly("Acme.Domain") }) });
            var order = app.GetTypeByMetadataName("Acme.Domain.Order");

            Assert.True(resolver.IsInLayer(order, "Domain"));
            Assert.False(resolver.IsInLayer(app.GetTypeByMetadataName("Acme.App.Local"), "Domain"));
        }
    }
}
