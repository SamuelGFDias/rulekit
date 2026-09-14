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
    }
}
