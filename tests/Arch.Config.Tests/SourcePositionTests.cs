using System.Linq;
using Arch.Config;
using Xunit;

namespace Arch.Config.Tests
{
    /// <summary>
    /// Issue #7: o parser registra de onde veio cada regra e cada erro (índice do texto na cadeia +
    /// linha/coluna 1-based), para que o analyzer ancore diagnósticos no arquivo e linha certos.
    /// </summary>
    public class SourcePositionTests
    {
        private const string BaseYaml = @"schema: arch-rules/v1
layers:
  A:
    match:
      - nameSuffix: A
rules:
  - id: BASE-001
    slot: ARCH0001
    type: forbidden-call
    from: A
    to: A
  - id: QUEBRADA
    slot: ARCH0002
    type: forbidden-call
    from: A
";

        private const string MainYaml = @"schema: arch-rules/v1
extends: [./base.yaml]
rules:
  - id: MAIN-001
    slot: ARCH0003
    type: forbidden-call
    from: A
    to: A
";

        [Fact]
        public void Regras_e_erros_carregam_documento_linha_e_coluna_de_origem()
        {
            var result = ArchConfigParser.Parse(new[] { BaseYaml, MainYaml });

            Assert.NotNull(result.Config);

            var baseRule = result.Config.Rules.Single(r => r.Id == "BASE-001");
            Assert.Equal(0, baseRule.SourceDocumentIndex);
            Assert.Equal(7, baseRule.SourceLine);
            Assert.Equal(5, baseRule.SourceColumn);

            var mainRule = result.Config.Rules.Single(r => r.Id == "MAIN-001");
            Assert.Equal(1, mainRule.SourceDocumentIndex);
            Assert.Equal(4, mainRule.SourceLine);

            var error = Assert.Single(result.Errors, e => e.Code == "ARCH9008");
            Assert.Equal(0, error.SourceDocumentIndex);
            Assert.Equal(12, error.Line);
        }

        [Fact]
        public void Regra_montada_a_mao_nao_tem_posicao()
        {
            var rule = new RuleDefinition("X", "ARCH0001", "forbidden-call", true, null, null, null, null);

            Assert.Equal(-1, rule.SourceDocumentIndex);
            Assert.Equal(0, rule.SourceLine);
            Assert.Equal(-1, new SchemaValidationError("ARCH9001", "m", 1, 1, true).SourceDocumentIndex);
        }
    }
}
