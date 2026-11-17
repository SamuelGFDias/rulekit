using System;
using System.IO;
using Arch.Cli;
using Xunit;

namespace Arch.Cli.Tests
{
    /// <summary>
    /// Código de saída de `arch-rules validate`/`gen` diante de avisos (revisão do commit 287c86e,
    /// achado M1). Política: ARCH9008 (regra malformada — sempre erro do usuário) faz `validate`
    /// sair 1; ARCH9003 (tipo desconhecido — pode ser tipo de uma versão futura da lib) segue como
    /// aviso com saída 0; `--strict` faz QUALQUER aviso sair 1, em `validate` e em `gen`. `gen` sem
    /// `--strict` continua gerando e saindo 0 com avisos.
    /// </summary>
    public sealed class ExitCodeTests : IDisposable
    {
        private readonly string _dir;

        public ExitCodeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "arch-cli-exit-" + Guid.NewGuid().ToString("N"));
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

        private const string Header = @"schema: arch-rules/v1
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

        // max-dependencies sem 'countBy' obrigatório -> ARCH9008 (regra descartada).
        private const string Arch9008Rule = @"  - id: ACME-020
    slot: ARCH0020
    type: max-dependencies
    layer: Controller
    max: 7
";

        // Tipo que o catálogo desconhece -> ARCH9003 (regra descartada).
        private const string Arch9003Rule = @"  - id: ACME-030
    slot: ARCH0030
    type: tipo-de-versao-futura
    layer: Controller
";

        private string WriteYaml(string content)
        {
            var path = Path.Combine(_dir, "arch-rules.yaml");
            File.WriteAllText(path, content);
            return path;
        }

        private static (int ExitCode, string Text) RunCli(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exitCode = Program.Run(args, output, error);
            return (exitCode, output + "\n" + error);
        }

        [Fact]
        public void Validate_ValidYaml_ExitsZero()
        {
            var (exit, text) = RunCli("validate", WriteYaml(Header));

            Assert.Equal(0, exit);
            Assert.Contains("OK", text);
        }

        [Fact]
        public void Validate_Arch9008_ExitsOne_AndReportsTheRule()
        {
            var (exit, text) = RunCli("validate", WriteYaml(Header + Arch9008Rule));

            Assert.Equal(1, exit);
            Assert.Contains("[ARCH9008]", text);
            Assert.Contains("ACME-020", text);
            Assert.DoesNotContain("OK: configuração válida", text);
        }

        [Fact]
        public void Validate_Arch9003_StaysWarning_ExitsZero()
        {
            var (exit, text) = RunCli("validate", WriteYaml(Header + Arch9003Rule));

            Assert.Equal(0, exit);
            Assert.Contains("AVISO [ARCH9003]", text);
        }

        [Fact]
        public void Validate_Arch9003_WithStrict_ExitsOne()
        {
            var (exit, text) = RunCli("validate", WriteYaml(Header + Arch9003Rule), "--strict");

            Assert.Equal(1, exit);
            Assert.Contains("[ARCH9003]", text);
        }

        [Fact]
        public void Validate_StrictBeforePath_IsAccepted()
        {
            var (exit, text) = RunCli("validate", "--strict", WriteYaml(Header + Arch9003Rule));

            Assert.Equal(1, exit);
            Assert.Contains("[ARCH9003]", text);
        }

        [Fact]
        public void Validate_ValidYaml_WithStrict_ExitsZero()
        {
            var (exit, text) = RunCli("validate", WriteYaml(Header), "--strict");

            Assert.Equal(0, exit);
            Assert.Contains("OK", text);
        }

        [Fact]
        public void Validate_LockDivergenceWarning_WithStrict_ExitsOne()
        {
            var yaml = WriteYaml(Header);
            File.WriteAllText(Path.Combine(_dir, "arch-rules.lock.yaml"), "schema: arch-rules-lock/v1\nrules:\n");

            Assert.Equal(0, RunCli("validate", yaml).ExitCode);

            var (exit, text) = RunCli("validate", yaml, "--strict");

            Assert.Equal(1, exit);
            Assert.Contains("[ARCH9005]", text);
        }

        [Fact]
        public void Validate_UnknownOption_ExitsOneWithMessage()
        {
            var (exit, text) = RunCli("validate", WriteYaml(Header), "--estrito");

            Assert.Equal(1, exit);
            Assert.Contains("--estrito", text);
        }

        [Fact]
        public void Gen_Arch9008_WithoutStrict_StillGeneratesAndExitsZero()
        {
            var (exit, text) = RunCli("gen", WriteYaml(Header + Arch9008Rule));

            Assert.Equal(0, exit);
            Assert.Contains("AVISO [ARCH9008]", text);
            Assert.True(File.Exists(Path.Combine(_dir, ".globalconfig")));
        }

        [Fact]
        public void Gen_Arch9003_WithStrict_ExitsOne_AndGeneratesNothing()
        {
            var (exit, text) = RunCli("gen", WriteYaml(Header + Arch9003Rule), "--strict");

            Assert.Equal(1, exit);
            Assert.Contains("[ARCH9003]", text);
            Assert.False(File.Exists(Path.Combine(_dir, ".globalconfig")));
            Assert.False(File.Exists(Path.Combine(_dir, "arch-rules.lock.yaml")));
        }

        [Fact]
        public void Gen_ValidYaml_WithStrict_GeneratesAndExitsZero()
        {
            var (exit, _) = RunCli("gen", WriteYaml(Header), "--strict", "--out", Path.Combine(_dir, "out"));

            Assert.Equal(0, exit);
            Assert.True(File.Exists(Path.Combine(_dir, "out", ".globalconfig")));
        }

        [Theory]
        [InlineData("--help")]
        [InlineData("-h")]
        public void Help_DocumentsStrictAndExitCodes(string flag)
        {
            var (exit, text) = RunCli(flag);

            Assert.Equal(0, exit);
            Assert.Contains("--strict", text);
            Assert.Contains("ARCH9008", text);
        }
    }
}
