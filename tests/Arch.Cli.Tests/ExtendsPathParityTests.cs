using System;
using System.IO;
using Arch.Cli;
using Xunit;

namespace Arch.Cli.Tests
{
    /// <summary>
    /// Revisão B1: o caminho declarado em `extends:` é normalizado (trim + `\` → `/`) numa única
    /// função (<c>ExtendsChainResolver.NormalizeDeclaredPath</c>) usada por analyzer e CLI. Antes, o
    /// analyzer aceitava `sub\base.yaml` e ` base.yaml ` e o CLI, em Linux, não achava o arquivo
    /// (validate verde-ou-vermelho divergente do build). O espelho do analyzer está em
    /// <c>Arch.Analyzer.Tests/ExtendsResolutionTests</c>.
    /// </summary>
    public sealed class ExtendsPathParityTests : IDisposable
    {
        private readonly string _dir;

        public ExtendsPathParityTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "arch-cli-extends-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "sub"));
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

        private const string BaseYaml = @"schema: arch-rules/v1
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
  - id: BASE-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
";

        [Theory]
        [InlineData("'sub\\arch-rules.base.yaml'")]
        [InlineData("'.\\sub\\arch-rules.base.yaml'")]
        [InlineData("' sub/arch-rules.base.yaml '")]
        [InlineData("'  .\\sub/arch-rules.base.yaml\t'")]
        public void Validate_ResolvesExtendsWithBackslashOrSurroundingSpaces_LikeTheAnalyzer(string declared)
        {
            File.WriteAllText(Path.Combine(_dir, "sub", "arch-rules.base.yaml"), BaseYaml);
            var main = Path.Combine(_dir, "arch-rules.yaml");
            File.WriteAllText(main, "schema: arch-rules/v1\nextends: [" + declared + "]\n");

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = Program.Run(new[] { "validate", main }, output, error);

            Assert.True(exit == 0, output + "\n" + error);
        }
    }
}
