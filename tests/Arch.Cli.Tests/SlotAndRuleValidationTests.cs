using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arch.Cli;
using Arch.Config;
using Xunit;

namespace Arch.Cli.Tests
{
    /// <summary>
    /// Issue #6: o CLI precisa dar o MESMO veredito que o analyzer sobre o que é um slot válido
    /// (trim + case-insensitive; duplicatas comparadas depois da normalização). O espelho destes
    /// cenários do lado do analyzer está em <c>Arch.Analyzer.Tests/SlotParityTests</c>.
    /// Issue #5: regra com campo obrigatório faltando aparece no `arch-rules validate`.
    /// </summary>
    public sealed class SlotAndRuleValidationTests : IDisposable
    {
        private readonly string _dir;

        public SlotAndRuleValidationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "arch-cli-slot-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
        }

        private static RuleDefinition Rule(string id, string slot)
            => new RuleDefinition(
                id,
                slot,
                "forbidden-call",
                enabled: true,
                severityName: null,
                messageTemplate: null,
                help: null,
                extra: new Dictionary<string, string> { ["from"] = "Controller", ["to"] = "Repository" });

        [Theory]
        [InlineData("arch0007")]
        [InlineData(" ARCH0007 ")]
        [InlineData("Arch0512")]
        public void Issue6_SlotIsAcceptedCaseInsensitiveAndTrimmed_LikeTheAnalyzer(string slot)
        {
            var errors = SlotValidator.Validate(new[] { Rule("ACME-007", slot) });

            Assert.True(errors.Count == 0, string.Join(" | ", errors.Select(e => e.ToString())));
        }

        [Fact]
        public void Issue6_SameSlotWithDifferentCase_IsADuplicate_LikeTheAnalyzer()
        {
            var errors = SlotValidator.Validate(new[] { Rule("ACME-007", "ARCH0007"), Rule("ACME-008", "arch0007") });

            var error = Assert.Single(errors);
            Assert.Equal("ARCH9002", error.Code);
            Assert.Contains("duplicado", error.Message);
            Assert.Contains("ACME-007", error.Message);
            Assert.Contains("ACME-008", error.Message);
        }

        [Theory]
        [InlineData("ARCH0000")]
        [InlineData("ARCH0513")]
        [InlineData("ARCH9001")]
        [InlineData("ARCH7")]
        [InlineData("ARCH00007")]
        [InlineData("ACME-001")]
        [InlineData("ARCH١٢٣٤")] // dígitos arábico-índicos: \d casaria, o pool não
        public void Issue6_SlotOutsideThePool_IsRejected(string slot)
        {
            var error = Assert.Single(SlotValidator.Validate(new[] { Rule("ACME-007", slot) }));
            Assert.Equal("ARCH9002", error.Code);
        }

        [Fact]
        public void Issue6_GlobalConfig_UsesTheCanonicalSlot()
        {
            var yamlPath = Path.Combine(_dir, "arch-rules.yaml");
            File.WriteAllText(yamlPath, @"
schema: arch-rules/v1
severities:
  media: warning
defaults:
  severity: media
rules:
  - id: ACME-007
    slot: ' arch0007 '
    type: forbidden-call
    from: Controller
    to: Repository
");
            var output = new StringWriter();

            var exitCode = GenCommand.Run(yamlPath, outDir: null, output: output);

            Assert.True(exitCode == 0, output.ToString());
            var globalConfig = File.ReadAllText(Path.Combine(_dir, ".globalconfig"));
            Assert.Contains("dotnet_diagnostic.ARCH0007.severity = warning", globalConfig);
        }

        [Fact]
        public void Issue5_Validate_ReportsArch9008_ForMissingRequiredField()
        {
            var yamlPath = Path.Combine(_dir, "arch-rules.yaml");
            File.WriteAllText(yamlPath, @"
schema: arch-rules/v1
severities:
  media: warning
defaults:
  severity: media
rules:
  - id: ACME-020
    slot: ARCH0020
    type: max-dependencies
    layer: Service
    max: 7
");
            var output = new StringWriter();

            var exitCode = ValidateCommand.Run(yamlPath, output);

            var text = output.ToString();
            Assert.Equal(1, exitCode); // regra malformada é sempre erro do usuário (revisão M1)
            Assert.Contains("[ARCH9008]", text);
            Assert.Contains("ACME-020", text);
            Assert.Contains("countBy", text);
        }

        private const string ModuleYaml = @"
schema: arch-rules/v1
severities:
  media: warning
defaults:
  severity: media
layers:
  Services:
    match: [ { namespace: ""Acme.[Module].Services.**"" } ]
  Domain:
    match: [ { namespace: ""Acme.[Module].Domain.**"" } ]
rules:
  - id: ACME-010
    slot: ARCH0010
    type: forbidden-call
    from: Services
    to: Domain
    when: { differ: [WHEN_NAME] }
";

        [Fact]
        public void Issue24_Validate_AcceptsWhenNamingAPlaceholder()
        {
            var yamlPath = Path.Combine(_dir, "arch-rules.yaml");
            File.WriteAllText(yamlPath, ModuleYaml.Replace("WHEN_NAME", "Module"));

            var exitCode = ValidateCommand.Run(yamlPath, new StringWriter());

            Assert.Equal(0, exitCode);
        }

        [Fact]
        public void Issue24_Validate_ReportsArch9008_ForWhenNamingAnUncapturedPlaceholder()
        {
            var yamlPath = Path.Combine(_dir, "arch-rules.yaml");
            File.WriteAllText(yamlPath, ModuleYaml.Replace("WHEN_NAME", "Tenant"));
            var output = new StringWriter();

            var exitCode = ValidateCommand.Run(yamlPath, output);

            var text = output.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("[ARCH9008]", text);
            Assert.Contains("Tenant", text);
        }

        // ---- Revisão M2: regra `enabled: false` não entra na validação de slot (paridade com o analyzer) ----

        private const string DisabledRuleHeader = @"
schema: arch-rules/v1
layers:
  Controller:
    match:
      - nameSuffix: Controller
  Repository:
    match:
      - nameSuffix: Repository
severities:
  media: warning
defaults:
  severity: media
rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
";

        [Fact]
        public void M2_DisabledRule_WithSlotOutsidePool_IsNotReported_AndLandsOnlyInLock()
        {
            var yamlPath = Path.Combine(_dir, "arch-rules.yaml");
            File.WriteAllText(yamlPath, DisabledRuleHeader + @"  - id: ACME-099
    slot: ARCH0999
    enabled: false
    type: forbidden-call
    from: Controller
    to: Repository
");
            var output = new StringWriter();

            var exitCode = GenCommand.Run(yamlPath, null, output);

            Assert.True(exitCode == 0, output.ToString());
            var globalConfig = File.ReadAllText(Path.Combine(_dir, ".globalconfig"));
            Assert.Contains("dotnet_diagnostic.ARCH0001.severity", globalConfig);
            Assert.DoesNotContain("ARCH0999", globalConfig);
            var lockFile = File.ReadAllText(Path.Combine(_dir, "arch-rules.lock.yaml"));
            Assert.Contains("slot: ARCH0999", lockFile);
            Assert.Contains("enabled: false", lockFile);
        }

        [Fact]
        public void M2_DisabledRule_ReusingSlotOfEnabledRule_IsNotADuplicate()
        {
            var yamlPath = Path.Combine(_dir, "arch-rules.yaml");
            File.WriteAllText(yamlPath, DisabledRuleHeader + @"  - id: ACME-002
    slot: ARCH0001
    enabled: false
    type: forbidden-call
    from: Controller
    to: Repository
");
            var output = new StringWriter();

            var exitCode = ValidateCommand.Run(yamlPath, output);

            Assert.True(exitCode == 0, output.ToString());
            Assert.DoesNotContain("ARCH9002", output.ToString());
        }

        [Fact]
        public void M2_DisabledRule_WithInvalidFormatSlot_IsNotReported()
        {
            var errors = SlotValidator.Validate(new[] { DisabledRule("ACME-099", "slot-qualquer") });

            Assert.Empty(errors);
        }

        [Fact]
        public void M2_EnabledRule_AfterDisabledOneWithSameSlot_KeepsTheSlot()
        {
            // A desabilitada vem PRIMEIRO: não pode "ocupar" o slot e fazer a habilitada virar duplicata.
            var errors = SlotValidator.Validate(new[] { DisabledRule("ACME-002", "ARCH0001"), Rule("ACME-001", "ARCH0001") });

            Assert.Empty(errors);
        }

        [Fact]
        public void M2_EnabledRule_WithSlotOutsidePool_IsStillReported()
        {
            var errors = SlotValidator.Validate(new[] { Rule("ACME-099", "ARCH0999") });

            Assert.Single(errors);
            Assert.Equal("ARCH9002", errors[0].Code);
        }

        private static RuleDefinition DisabledRule(string id, string slot)
            => new RuleDefinition(
                id,
                slot,
                "forbidden-call",
                enabled: false,
                severityName: null,
                messageTemplate: null,
                help: null,
                extra: new Dictionary<string, string> { ["from"] = "Controller", ["to"] = "Repository" });
    }
}
