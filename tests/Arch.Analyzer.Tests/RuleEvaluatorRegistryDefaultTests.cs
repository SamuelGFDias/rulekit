using System.Linq;
using Xunit;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Teste de integração pós-Fase-2: prova que <see cref="RuleEvaluatorRegistry.Default"/>
    /// deixou de ser vazio e agora contém os 4 avaliadores concretos entregues em
    /// <c>src/Arch.Rules/</c>, cada um indexado pela chave (`rules[].type`) esperada.
    /// </summary>
    public class RuleEvaluatorRegistryDefaultTests
    {
        [Fact]
        public void Default_ContemOsQuatroAvaliadoresDaFase2()
        {
            var registry = RuleEvaluatorRegistry.Default;

            Assert.Equal(4, registry.Count);
            Assert.True(registry.ContainsKey("forbidden-call"));
            Assert.True(registry.ContainsKey("must-route-through"));
            Assert.True(registry.ContainsKey("naming-convention"));
            Assert.True(registry.ContainsKey("max-dependencies"));
        }

        /// <summary>
        /// Issue #17: o catálogo de Arch.Config é a fonte de verdade do que é um `type` válido e o
        /// registro é só despacho. Falha se um tipo do catálogo ficar sem avaliador (regra aceita pelo
        /// parser que nunca seria avaliada) ou se um avaliador for registrado para um tipo que o
        /// catálogo não conhece (o parser descartaria a regra com ARCH9003 antes de chegar aqui).
        /// </summary>
        [Fact]
        public void Default_e_RuleTypeCatalog_declaram_exatamente_os_mesmos_tipos()
        {
            var catalogTypes = Arch.Config.RuleTypeCatalog.Default.TypeNames
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToArray();
            var registryTypes = RuleEvaluatorRegistry.Default.Keys
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(catalogTypes, registryTypes);
            Assert.All(RuleEvaluatorRegistry.Default, kv => Assert.Equal(kv.Key, kv.Value.RuleType));
        }

        [Fact]
        public void Default_e_instancia_unica()
        {
            Assert.Same(RuleEvaluatorRegistry.Default, RuleEvaluatorRegistry.Default);
        }
    }
}
