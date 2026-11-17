using System;
using System.IO;
using Arch.Cli;
using Xunit;

namespace Arch.Cli.Tests
{
    /// <summary>
    /// Issue #4 (lado do CLI): nenhuma exceção pode escapar de `arch-rules gen`/`validate` como stack
    /// trace bruto — em CI isso é ilegível. Toda falha vira uma linha <c>ERRO [ARCH9001] ...</c> e
    /// código de saída diferente de zero.
    /// </summary>
    public sealed class CliRobustnessTests : IDisposable
    {
        private readonly string _dir;

        public CliRobustnessTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "arch-cli-robust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
        }

        private const string ValidYaml = @"schema: arch-rules/v1
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

        private string Write(string name, string content)
        {
            var path = Path.Combine(_dir, name);
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

        private static void AssertCleanArch9001(int exitCode, string text)
        {
            Assert.NotEqual(0, exitCode);
            Assert.Contains("ERRO [ARCH9001]", text);
            Assert.DoesNotContain("   at ", text);
            Assert.DoesNotContain("Unhandled exception", text);
        }

        [Fact]
        public void Validate_AninhamentoExtremo_ViraArch9001Rapido_SemEstourarAPilha()
        {
            // Revisão B2: 100 mil `[` derrubavam o processo (StackOverflow no carregador da DOM).
            var yamlPath = Write("fundo.yaml", "extends: " + new string('[', 100_000));

            var (exitCode, text) = RunCli("validate", yamlPath);

            AssertCleanArch9001(exitCode, text);
            Assert.Contains("aninhamento", text);
        }

        [Fact]
        public void Gen_ExcecaoInesperada_ViraErroLimpoArch9001_SemStackTrace()
        {
            // `--out` apontando para um ARQUIVO existente: Directory.CreateDirectory lança
            // IOException — falha que nenhum caminho do CLI previa.
            var yamlPath = Write("arch-rules.yaml", ValidYaml);
            var outIsAFile = Write("nao-sou-diretorio", "x");

            var (exitCode, text) = RunCli("gen", yamlPath, "--out", outIsAFile);

            AssertCleanArch9001(exitCode, text);
            Assert.Contains("IOException", text);
        }

        [Fact]
        public void Validate_ArquivoIlegivel_ViraErroLimpoArch9001_SemStackTrace()
        {
            if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            {
                // chmod não se aplica no Windows, e root lê qualquer arquivo.
                return;
            }

            var yamlPath = Write("arch-rules.yaml", ValidYaml);
            File.SetUnixFileMode(yamlPath, UnixFileMode.None);

            var (exitCode, text) = RunCli("validate", yamlPath);

            AssertCleanArch9001(exitCode, text);
        }

        [Theory]
        [InlineData("schema: arch-rules/v99999999999999999999\n")]
        [InlineData("schema: arch-rules/v1\n? [a, b]\n: valor\n")]
        [InlineData("schema: arch-rules/v1\nrules: [\n")]
        public void Validate_YamlMalformado_ViraErroLimpoArch9001_SemStackTrace(string yaml)
        {
            var yamlPath = Write("arch-rules.yaml", yaml);

            var (exitCode, text) = RunCli("validate", yamlPath);

            AssertCleanArch9001(exitCode, text);
        }

        [Fact]
        public void Gen_ExtendsAusente_ContinuaBloqueanteComCliExtends()
        {
            var yamlPath = Write("arch-rules.yaml", ValidYaml.Replace(
                "schema: arch-rules/v1\n", "schema: arch-rules/v1\nextends: [./sumiu.yaml]\n"));

            var (exitCode, text) = RunCli("gen", yamlPath);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("ERRO [CLI-EXTENDS]", text);
            Assert.Contains("sumiu.yaml", text);
            Assert.False(File.Exists(Path.Combine(_dir, ".globalconfig")));
        }
    }
}
