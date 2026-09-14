using System.Linq;
using Xunit;

namespace Arch.Config.Tests
{
    /// <summary>
    /// Cobertura da Fase 1A (trilha "config-parser", PLANO-implementacao.md) para
    /// <see cref="ArchConfigParser"/>. "Parser puro, sem Roslyn": este projeto não referencia
    /// Microsoft.CodeAnalysis.
    /// </summary>
    public class ArchConfigParserTests
    {
        // YAML de exemplo completo do ADR-001 D2 (seção "Sintaxe").
        private const string ValidYaml = @"
schema: arch-rules/v1

layers:
  Controller:
    match:
      - namespace: ""Acme.Api.Controllers.**""
      - nameSuffix: ""Controller""
      - baseType: ""Microsoft.AspNetCore.Mvc.ControllerBase""
  Service:
    match:
      - implements: ""Acme.Application.IService""
      - nameSuffix: ""Service""
  Repository:
    match:
      - nameSuffix: ""Repository""
      - attribute: ""Acme.Architecture.RepositoryAttribute""
    exclude:
      - namespace: ""Acme.*.Tests.**""

severities:
  restrita: error
  media: warning
  livre: suggestion

defaults:
  severity: media

rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
    severity: restrita
    message: ""{from} não pode chamar {to} diretamente ({symbol}). Passe por um Service.""
    help: https://wiki.acme/arq/ACME-001

  - id: ACME-002
    slot: ARCH0002
    type: must-route-through
    from: Controller
    to: Repository
    through: [Service]

  - id: ACME-003
    slot: ARCH0003
    type: naming-convention
    layer: Repository
    require: { suffix: Repository }

  - id: ACME-004
    slot: ARCH0004
    type: max-dependencies
    layer: Service
    max: 7
    countBy: constructor-parameters
";

        [Fact]
        public void ValidYaml_ProducesConfigWithNoErrors()
        {
            var result = ArchConfigParser.Parse(ValidYaml);

            Assert.Empty(result.Errors);
            Assert.NotNull(result.Config);
            Assert.Equal("arch-rules/v1", result.Config.Schema);

            Assert.Equal(3, result.Config.Layers.Count);
            Assert.True(result.Config.Layers.ContainsKey("Controller"));
            Assert.True(result.Config.Layers.ContainsKey("Service"));
            Assert.True(result.Config.Layers.ContainsKey("Repository"));

            var controller = result.Config.Layers["Controller"];
            Assert.Equal(3, controller.Match.Count);
            Assert.Equal("Acme.Api.Controllers.**", controller.Match[0].Namespace);
            Assert.Equal("Controller", controller.Match[1].NameSuffix);
            Assert.Equal("Microsoft.AspNetCore.Mvc.ControllerBase", controller.Match[2].BaseType);

            var repository = result.Config.Layers["Repository"];
            Assert.Single(repository.Exclude);
            Assert.Equal("Acme.*.Tests.**", repository.Exclude[0].Namespace);

            Assert.Equal(3, result.Config.Severities.Count);
            Assert.Equal("error", result.Config.Severities["restrita"]);
            Assert.Equal("media", result.Config.DefaultSeverityName);

            Assert.Equal(4, result.Config.Rules.Count);

            var forbiddenCall = result.Config.Rules.Single(r => r.Id == "ACME-001");
            Assert.Equal("ARCH0001", forbiddenCall.Slot);
            Assert.Equal("forbidden-call", forbiddenCall.Type);
            Assert.True(forbiddenCall.Enabled);
            Assert.Equal("Controller", forbiddenCall.Extra["from"]);
            Assert.Equal("Repository", forbiddenCall.Extra["to"]);

            var routeThrough = result.Config.Rules.Single(r => r.Id == "ACME-002");
            Assert.Equal("Service", routeThrough.Extra["through"]);

            var naming = result.Config.Rules.Single(r => r.Id == "ACME-003");
            Assert.Equal("Repository", naming.Extra["layer"]);
            Assert.Equal("suffix=Repository", naming.Extra["require"]);

            var maxDeps = result.Config.Rules.Single(r => r.Id == "ACME-004");
            Assert.Equal("7", maxDeps.Extra["max"]);
            Assert.Equal("constructor-parameters", maxDeps.Extra["countBy"]);
        }

        [Fact]
        public void MissingSchema_IsBlockingArch9001()
        {
            const string yaml = @"
layers:
  Controller:
    match:
      - nameSuffix: Controller
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Null(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9001", error.Code);
            Assert.True(error.IsBlocking);
        }

        [Fact]
        public void UnsupportedMajorVersion_IsBlockingArch9001()
        {
            const string yaml = @"
schema: arch-rules/v2
rules: []
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Null(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9001", error.Code);
            Assert.True(error.IsBlocking);
        }

        [Fact]
        public void Anchor_IsBlocking()
        {
            const string yaml = @"
schema: arch-rules/v1
layers:
  Controller: &ctrl
    match:
      - nameSuffix: Controller
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Null(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9001", error.Code);
            Assert.True(error.IsBlocking);
        }

        [Fact]
        public void MultiDocument_IsBlocking()
        {
            const string yaml = @"
schema: arch-rules/v1
layers: {}
---
schema: arch-rules/v1
layers: {}
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Null(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9001", error.Code);
            Assert.True(error.IsBlocking);
        }

        [Fact]
        public void UnknownKeyInsideRule_IsBlocking()
        {
            const string yaml = @"
schema: arch-rules/v1
rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    foo: bar
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Null(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9001", error.Code);
            Assert.True(error.IsBlocking);
        }

        [Fact]
        public void UnknownRuleType_IsNonBlockingArch9003_AndRuleIsExcluded()
        {
            const string yaml = @"
schema: arch-rules/v1
rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
  - id: ACME-999
    slot: ARCH0099
    type: some-future-type
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.NotNull(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9003", error.Code);
            Assert.False(error.IsBlocking);

            Assert.Single(result.Config.Rules);
            Assert.Equal("ACME-001", result.Config.Rules[0].Id);
        }

        [Fact]
        public void Extends_MergesLayersByNameAndAddsNewRule_ChildWins()
        {
            const string baseYaml = @"
schema: arch-rules/v1
layers:
  Controller:
    match:
      - nameSuffix: Controller
  Repository:
    match:
      - nameSuffix: Repository
rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
";
            const string childYaml = @"
schema: arch-rules/v1
extends:
  - ./arch-rules.base.yaml
layers:
  Controller:
    match:
      - nameSuffix: Ctrl
rules:
  - id: ACME-002
    slot: ARCH0002
    type: naming-convention
    layer: Repository
    require: { suffix: Repository }
";
            // Overload interno (ADR-001 D2 + decisão documentada em ArchConfigParser.Parse(string)):
            // recebe os textos já resolvidos pelo chamador, em ordem crescente de precedência —
            // o último (o que declara `extends`) vence um conflito. Acessível aqui via
            // InternalsVisibleTo (AssemblyInfo.cs em src/Arch.Config).
            var result = ArchConfigParser.Parse(new[] { baseYaml, childYaml });

            Assert.NotNull(result.Config);
            Assert.Empty(result.Errors);

            // Controller foi sobrescrita pelo filho (que faz extends) — nome de camada igual,
            // critério de match diferente do base.
            Assert.Equal(2, result.Config.Layers.Count);
            var controller = result.Config.Layers["Controller"];
            Assert.Single(controller.Match);
            Assert.Equal("Ctrl", controller.Match[0].NameSuffix);

            // Repository não foi tocada pelo filho — sobrevive do base.
            Assert.True(result.Config.Layers.ContainsKey("Repository"));

            // Rules: a do base continua, mais a nova do filho.
            Assert.Equal(2, result.Config.Rules.Count);
            Assert.Contains(result.Config.Rules, r => r.Id == "ACME-001");
            Assert.Contains(result.Config.Rules, r => r.Id == "ACME-002");
        }

        [Fact]
        public void RuleDisabled_StaysInRulesList_JustMarkedDisabled()
        {
            const string yaml = @"
schema: arch-rules/v1
rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
    enabled: false
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.NotNull(result.Config);
            Assert.Empty(result.Errors);
            var rule = Assert.Single(result.Config.Rules);
            Assert.Equal("ACME-001", rule.Id);
            Assert.False(rule.Enabled);
        }
    }
}
