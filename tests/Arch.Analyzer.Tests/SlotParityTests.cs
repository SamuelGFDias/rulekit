using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Analyzer ponta a ponta com o parser REAL (sem injeção de config): issues #3, #5 e #6.
    /// Os cenários de slot (#6) são o espelho de <c>Arch.Cli.Tests/SlotAndRuleValidationTests</c> —
    /// CLI e analyzer têm de dar o mesmo veredito para o mesmo YAML.
    /// </summary>
    public class SlotParityTests
    {
        private const string ConfigPath = "/repo/arch-rules.yaml";

        private const string Source = @"
namespace Acme.Infra
{
    public class OrderRepository { public void Load() { } }
}

namespace Acme.Api.Controllers
{
    public class OrderController
    {
        public void Get() { new Acme.Infra.OrderRepository().Load(); }
    }
}
";

        private const string Layers = @"
schema: arch-rules/v1
layers:
  Controller:
    match:
      - nameSuffix: Controller
  Repository:
    match:
      - nameSuffix: Repository
rules:
";

        private static string ForbiddenCall(string id, string slot, string type = "forbidden-call") => @"
  - id: " + id + @"
    slot: " + slot + @"
    type: " + type + @"
    from: Controller
    to: Repository
";

        private static Task<ImmutableArray<Diagnostic>> RunAsync(string yaml)
            => AnalyzerRunner.RunAsync(
                new ArchRulesAnalyzer(),
                TestCompilations.Create(Source),
                new InMemoryAdditionalText(ConfigPath, yaml));

        [Theory]
        [InlineData("arch0007")]
        [InlineData("' ARCH0007 '")]
        public async Task Issue6_SlotCaseAndSpaces_AreAccepted_SameVerdictAsCli(string slot)
        {
            var diagnostics = await RunAsync(Layers + ForbiddenCall("ACME-007", slot));

            Assert.True(
                diagnostics.Length > 0 && diagnostics.All(d => d.Id == "ARCH0007"),
                AnalyzerRunner.Describe(diagnostics));
        }

        [Fact]
        public async Task Issue6_SameSlotWithDifferentCase_IsDuplicateArch9002_SameVerdictAsCli()
        {
            var diagnostics = await RunAsync(
                Layers + ForbiddenCall("ACME-007", "ARCH0007") + ForbiddenCall("ACME-008", "arch0007"));

            var duplicate = Assert.Single(diagnostics, d => d.Id == "ARCH9002");
            Assert.Contains("ACME-008", duplicate.GetMessage());
            Assert.Contains(diagnostics, d => d.Id == "ARCH0007" && d.GetMessage().StartsWith("ACME-007"));
        }

        [Fact]
        public async Task Issue3_TypeWithDifferentCase_IsEvaluated_NotArch9003()
        {
            var diagnostics = await RunAsync(Layers + ForbiddenCall("ACME-001", "ARCH0001", "Forbidden-Call"));

            Assert.DoesNotContain(diagnostics, d => d.Id == "ARCH9003");
            Assert.Contains(diagnostics, d => d.Id == "ARCH0001");
        }

        [Fact]
        public async Task Issue5_MissingRequiredField_IsReportedAsArch9008_AndOtherRulesStillRun()
        {
            var yaml = Layers + ForbiddenCall("ACME-001", "ARCH0001") + @"
  - id: ACME-020
    slot: ARCH0020
    type: max-dependencies
    layer: Controller
    max: 7
";
            var diagnostics = await RunAsync(yaml);

            var arch9008 = Assert.Single(diagnostics, d => d.Id == "ARCH9008");
            Assert.Equal(DiagnosticSeverity.Warning, arch9008.Severity);
            Assert.Contains("ACME-020", arch9008.GetMessage());
            Assert.Contains("countBy", arch9008.GetMessage());
            Assert.True(arch9008.Location.GetLineSpan().StartLinePosition.Line > 0, "ARCH9008 deve apontar a regra no YAML.");
            Assert.Contains(diagnostics, d => d.Id == "ARCH0001");
        }
    }
}
