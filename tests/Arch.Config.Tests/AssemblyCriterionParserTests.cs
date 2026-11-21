using System.Linq;
using Xunit;

namespace Arch.Config.Tests
{
    /// <summary>Issue #23 — critério de camada <c>assembly</c> no parser.</summary>
    public class AssemblyCriterionParserTests
    {
        private const string Header = "schema: arch-rules/v1\n";

        [Fact]
        public void Assembly_valido_em_match_e_exclude_chega_ao_modelo()
        {
            var yaml = Header + @"
layers:
  Domain:
    match:
      - assembly: Acme.Domain
    exclude:
      - assembly: ""Acme.Domain.Tests.**""
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            var domain = result.Config.Layers["Domain"];
            Assert.Equal("Acme.Domain", domain.Match.Single().Assembly);
            Assert.Null(domain.Match.Single().Namespace);
            Assert.Equal("Acme.Domain.Tests.**", domain.Exclude.Single().Assembly);
        }

        [Fact]
        public void Assembly_vazio_e_aceito_e_preservado_como_vazio()
        {
            var yaml = Header + @"
layers:
  Domain:
    match:
      - assembly: """"
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            Assert.Equal(string.Empty, result.Config.Layers["Domain"].Match.Single().Assembly);
        }

        [Fact]
        public void Assembly_combina_por_OU_com_outros_criterios()
        {
            var yaml = Header + @"
layers:
  Infra:
    match:
      - assembly: ""Acme.Infrastructure.**""
      - namespace: ""Acme.Legacy.Data.**""
      - nameSuffix: Repository
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            var match = result.Config.Layers["Infra"].Match;
            Assert.Equal(3, match.Count);
            Assert.Equal("Acme.Infrastructure.**", match[0].Assembly);
            Assert.Equal("Acme.Legacy.Data.**", match[1].Namespace);
            Assert.Equal("Repository", match[2].NameSuffix);
        }

        [Fact]
        public void Assembly_junto_com_outro_campo_no_mesmo_item_e_erro_bloqueante()
        {
            var yaml = Header + @"
layers:
  Infra:
    match:
      - assembly: Acme.Infra
        namespace: Acme.Infra
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Null(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9001", error.Code);
            Assert.True(error.IsBlocking);
        }
    }
}
