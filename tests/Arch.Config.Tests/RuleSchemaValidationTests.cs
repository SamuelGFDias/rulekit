using System;
using System.Linq;
using Xunit;

namespace Arch.Config.Tests
{
    /// <summary>
    /// Validação de regras contra o catálogo de tipos (issues #2, #3, #5 e #17) e robustez do parser
    /// contra YAML malformado (issue #4). Todos os cenários usam só a API pública do parser.
    /// </summary>
    public class RuleSchemaValidationTests
    {
        private const string Header = @"
schema: arch-rules/v1
rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
";

        // ---------------------------------------------------------------- #2

        [Fact]
        public void Issue2_UnknownTypeWithItsOwnFields_IsOnlyArch9003_AndOtherRulesSurvive()
        {
            var yaml = Header + @"
  - id: ACME-010
    slot: ARCH0010
    type: cycle-check
    maxCycleLength: 3
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.NotNull(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9003", error.Code);
            Assert.False(error.IsBlocking);
            Assert.Contains("ACME-010", error.Message);

            var rule = Assert.Single(result.Config.Rules);
            Assert.Equal("ACME-001", rule.Id);
        }

        // ---------------------------------------------------------------- #3

        [Theory]
        [InlineData("Forbidden-Call")]
        [InlineData("FORBIDDEN-CALL")]
        [InlineData("' forbidden-call '")]
        public void Issue3_RuleType_IsCaseInsensitiveAndTrimmed_AndCanonicalized(string typeLiteral)
        {
            var yaml = @"
schema: arch-rules/v1
rules:
  - id: ACME-001
    slot: ARCH0001
    type: " + typeLiteral + @"
    from: Controller
    to: Repository
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.NotNull(result.Config);
            Assert.Empty(result.Errors);
            var rule = Assert.Single(result.Config.Rules);
            Assert.Equal("forbidden-call", rule.Type);
        }

        // ---------------------------------------------------------------- #4

        [Fact]
        public void Issue4_SchemaMajorOverflow_IsBlockingArch9001_NotAnException()
        {
            const string yaml = "schema: arch-rules/v99999999999999999999\n";

            var result = ArchConfigParser.Parse(yaml);

            Assert.Null(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9001", error.Code);
            Assert.True(error.IsBlocking);
            Assert.Contains("arch-rules/v99999999999999999999", error.Message);
        }

        [Theory]
        [InlineData("schema: arch-rules/v1\n? [a, b] : v\n")]
        [InlineData("schema: arch-rules/v1\nlayers:\n  ? [a, b] : v\n")]
        [InlineData("schema: arch-rules/v1\nlayers:\n  Api:\n    ? [a, b] : v\n")]
        [InlineData("schema: arch-rules/v1\nlayers:\n  Api:\n    match:\n      - ? [a, b] : v\n")]
        [InlineData("schema: arch-rules/v1\nseverities:\n  ? [a, b] : v\n")]
        [InlineData("schema: arch-rules/v1\ndefaults:\n  ? [a, b] : v\n")]
        [InlineData("schema: arch-rules/v1\nrules:\n  - id: A\n    ? [a, b] : v\n")]
        public void Issue4_ComplexKey_IsBlockingArch9001_NotAnException(string yaml)
        {
            var single = ArchConfigParser.Parse(yaml);
            var chain = ArchConfigParser.Parse(new[] { "schema: arch-rules/v1\n", yaml });

            foreach (var result in new[] { single, chain })
            {
                Assert.Null(result.Config);
                var error = Assert.Single(result.Errors);
                Assert.Equal("ARCH9001", error.Code);
                Assert.True(error.IsBlocking);
            }

            Assert.Empty(ArchConfigParser.PeekExtends(yaml));
        }

        [Fact]
        public void Issue4_PeekExtends_NeverThrows_ForTheIssueScenarios()
        {
            // Guarda (PeekExtends já tinha catch-all): só extrai `extends`, sem validar o resto.
            Assert.Empty(ArchConfigParser.PeekExtends("schema: arch-rules/v99999999999999999999\n"));
            Assert.Equal(new[] { "x.yaml" }, ArchConfigParser.PeekExtends("? [a, b] : v\nextends: [x.yaml]\n"));
        }

        // ---------------------------------------------------------------- #5

        [Fact]
        public void Issue5_MissingRequiredField_IsNonBlockingArch9008_CitingRuleAndField_AndOtherRulesSurvive()
        {
            var yaml = Header + @"
  - id: ACME-020
    slot: ARCH0020
    type: max-dependencies
    layer: Service
    max: 7
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.NotNull(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9008", error.Code);
            Assert.False(error.IsBlocking);
            Assert.Contains("ACME-020", error.Message);
            Assert.Contains("countBy", error.Message);
            Assert.True(error.Line > 0, "ARCH9008 precisa apontar a linha da regra no YAML.");

            var rule = Assert.Single(result.Config.Rules);
            Assert.Equal("ACME-001", rule.Id);
        }

        [Theory]
        [InlineData("type: max-dependencies\n    layer: Service\n    max: sete\n    countBy: fields", "max")]
        [InlineData("type: max-dependencies\n    layer: Service\n    max: -1\n    countBy: fields", "max")]
        [InlineData("type: max-dependencies\n    layer: Service\n    max: 7\n    countBy: everything", "countBy")]
        [InlineData("type: naming-convention\n    layer: Repository\n    require: Repository", "require")]
        [InlineData("type: naming-convention\n    layer: Repository\n    require: { suffix: A, prefix: B }", "require")]
        [InlineData("type: naming-convention\n    layer: Repository\n    require: { sufix: Repository }", "require")]
        [InlineData("type: naming-convention\n    require: { suffix: Repository }", "layer")]
        [InlineData("type: forbidden-call\n    from: Controller", "to")]
        [InlineData("type: forbidden-call\n    from: [Controller, Api]\n    to: Repository", "from")]
        [InlineData("type: forbidden-call\n    from: Controller\n    to: Repository\n    foo: bar", "foo")]
        [InlineData("type: forbidden-call\n    from: Controller\n    to: Repository\n    countBy: fields", "countBy")]
        [InlineData("type: must-route-through\n    from: Controller\n    to: Repository\n    through: { a: b }", "through")]
        public void Issue5_InvalidOrUnknownFieldForKnownType_IsNonBlockingArch9008(string body, string field)
        {
            var yaml = Header + @"
  - id: ACME-030
    slot: ARCH0030
    " + body + "\n";

            var result = ArchConfigParser.Parse(yaml);

            Assert.NotNull(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9008", error.Code);
            Assert.False(error.IsBlocking);
            Assert.Contains("ACME-030", error.Message);
            Assert.Contains("'" + field + "'", error.Message);

            var rule = Assert.Single(result.Config.Rules);
            Assert.Equal("ACME-001", rule.Id);
        }

        [Fact]
        public void Issue5_AllProblemsOfOneRule_AreReportedInASingleArch9008()
        {
            var yaml = Header + @"
  - id: ACME-040
    slot: ARCH0040
    type: max-dependencies
    max: muitos
    bogus: 1
";
            var result = ArchConfigParser.Parse(yaml);

            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9008", error.Code);
            foreach (var field in new[] { "layer", "countBy", "max", "bogus" })
            {
                Assert.Contains("'" + field + "'", error.Message);
            }
        }

        [Fact]
        public void ValidRules_OfAllFourTypes_ProduceNoErrors()
        {
            var yaml = Header + @"
  - id: ACME-002
    slot: ARCH0002
    type: must-route-through
    from: Controller
    to: Repository
    through: [Service, Facade]
  - id: ACME-003
    slot: ARCH0003
    type: naming-convention
    layer: Repository
    require: { prefix: Repo }
  - id: ACME-004
    slot: ARCH0004
    type: max-dependencies
    layer: Service
    max: 0
    countBy: all
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.NotNull(result.Config);
            Assert.True(result.Errors.Count == 0, string.Join(" | ", result.Errors.Select(e => e.Code + " " + e.Message)));
            Assert.Equal(4, result.Config.Rules.Count);
            Assert.Equal("Service,Facade", result.Config.Rules[1].Extra["through"]);
            Assert.Equal("prefix=Repo", result.Config.Rules[2].Extra["require"]);
            Assert.Equal("0", result.Config.Rules[3].Extra["max"]);
        }
    }
}
