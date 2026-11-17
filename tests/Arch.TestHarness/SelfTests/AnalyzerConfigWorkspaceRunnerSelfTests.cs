using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Arch.TestHarness.SelfTests
{
    /// <summary>
    /// Prova que <see cref="AnalyzerConfigWorkspaceRunner"/> faz o <c>.globalconfig</c> chegar à
    /// severidade efetiva (via <c>SyntaxTreeOptionsProvider</c>) e que, sem config, vale o default.
    /// </summary>
    public class AnalyzerConfigWorkspaceRunnerSelfTests
    {
        private static readonly (string, string)[] Sources = { ("Foo.cs", "public class Foo { }\n") };

        [Fact]
        public async Task GlobalConfig_Changes_Effective_Severity()
        {
            var diagnostics = await AnalyzerConfigWorkspaceRunner.RunAsync(
                new EmptyClassAnalyzer(),
                Sources,
                analyzerConfigFiles: new[]
                {
                    ("/0/rules.globalconfig", "is_global = true\ndotnet_diagnostic.HARNESS001.severity = error\n"),
                });

            Assert.Equal(DiagnosticSeverity.Error, Assert.Single(diagnostics).Severity);
        }

        [Fact]
        public async Task Without_Config_Severity_Is_The_Descriptor_Default()
        {
            var diagnostics = await AnalyzerConfigWorkspaceRunner.RunAsync(new EmptyClassAnalyzer(), Sources);

            Assert.Equal(DiagnosticSeverity.Warning, diagnostics.Single().Severity);
        }
    }
}
