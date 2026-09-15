using System;
using System.IO;
using Arch.Cli;
using Xunit;

namespace Arch.Cli.Tests
{
    /// <summary>
    /// Cobertura da Fase 3 (trilha "cli-generator", PLANO-implementacao.md) para
    /// <see cref="GenCommand"/>/<see cref="ValidateCommand"/>. "Sem Roslyn, só testa a lógica do
    /// CLI": este projeto não referencia Arch.Analyzer/Microsoft.CodeAnalysis. Cada teste roda
    /// num diretório temporário isolado (arquivos reais em disco — é o que exercita a resolução
    /// de `extends`, que é I/O de verdade).
    /// </summary>
    public sealed class CliCommandsTests : IDisposable
    {
        private readonly string _dir;

        public CliCommandsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "arch-cli-tests-" + Guid.NewGuid().ToString("N"));
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
                // Best-effort: não falhar o teste por causa de limpeza de diretório temporário.
            }
        }

        private string WriteYaml(string fileName, string content)
        {
            var path = Path.Combine(_dir, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        private const string ValidYaml = @"
schema: arch-rules/v1

layers:
  Controller:
    match:
      - nameSuffix: ""Controller""
  Repository:
    match:
      - nameSuffix: ""Repository""

severities:
  restrita: error
  media: warning

defaults:
  severity: media

rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
    severity: restrita
";

        [Fact]
        public void Gen_ValidYaml_ProducesGlobalConfigAndLockFileCorretos()
        {
            var yamlPath = WriteYaml("arch-rules.yaml", ValidYaml);
            var output = new StringWriter();

            var exitCode = GenCommand.Run(yamlPath, outDir: null, output: output);

            Assert.Equal(0, exitCode);

            var globalConfigPath = Path.Combine(_dir, ".globalconfig");
            var lockPath = Path.Combine(_dir, "arch-rules.lock.yaml");

            Assert.True(File.Exists(globalConfigPath));
            Assert.True(File.Exists(lockPath));

            var globalConfigContent = File.ReadAllText(globalConfigPath);
            Assert.Contains("is_global = true", globalConfigContent);
            Assert.Contains("dotnet_diagnostic.ARCH0001.severity = error", globalConfigContent);

            var lockContent = File.ReadAllText(lockPath);
            Assert.Contains("id: ACME-001", lockContent);
            Assert.Contains("slot: ARCH0001", lockContent);
            Assert.Contains("type: forbidden-call", lockContent);
            Assert.Contains("severity: error", lockContent);
            Assert.Contains("enabled: true", lockContent);
        }

        [Fact]
        public void Gen_ErroBloqueante_NaoGeraArquivosESaiComErro()
        {
            // schema com MAJOR não suportada (v2) é bloqueante (ARCH9001, ADR-001 D4).
            const string yamlComErroBloqueante = @"
schema: arch-rules/v2

rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
";
            var yamlPath = WriteYaml("arch-rules.yaml", yamlComErroBloqueante);
            var output = new StringWriter();

            var exitCode = GenCommand.Run(yamlPath, outDir: null, output: output);

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(Path.Combine(_dir, ".globalconfig")));
            Assert.False(File.Exists(Path.Combine(_dir, "arch-rules.lock.yaml")));
            Assert.Contains("ARCH9001", output.ToString());
        }

        [Fact]
        public void Gen_ExtendsDoisNiveis_MergeCorretoNoGlobalConfig()
        {
            const string grandparentYaml = @"
schema: arch-rules/v1

layers:
  Controller:
    match:
      - nameSuffix: ""Controller""
  Repository:
    match:
      - nameSuffix: ""Repository""

severities:
  baixa: suggestion
  media: warning
  alta: error

defaults:
  severity: baixa

rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
";

            const string parentYaml = @"
schema: arch-rules/v1

extends:
  - ./grandparent.yaml

defaults:
  severity: media
";

            const string childYaml = @"
schema: arch-rules/v1

extends:
  - ./parent.yaml
";

            WriteYaml("grandparent.yaml", grandparentYaml);
            WriteYaml("parent.yaml", parentYaml);
            var childPath = WriteYaml("child.yaml", childYaml);

            var output = new StringWriter();
            var exitCode = GenCommand.Run(childPath, outDir: null, output: output);

            Assert.Equal(0, exitCode);

            var globalConfigContent = File.ReadAllText(Path.Combine(_dir, ".globalconfig"));

            // O default efetivo vem do PAI (media = warning), sobrepondo o do AVÔ (baixa =
            // suggestion) — ACME-001 não declara `severity` própria em nenhum nível.
            Assert.Contains("dotnet_diagnostic.ARCH0001.severity = warning", globalConfigContent);
            Assert.DoesNotContain("suggestion", globalConfigContent);
        }

        [Fact]
        public void Gen_CicloDeExtends_FalhaComErroClaroSemTravar()
        {
            const string aYaml = @"
schema: arch-rules/v1

extends:
  - ./b.yaml
";
            const string bYaml = @"
schema: arch-rules/v1

extends:
  - ./a.yaml
";

            var aPath = WriteYaml("a.yaml", aYaml);
            WriteYaml("b.yaml", bYaml);

            var output = new StringWriter();

            // A chamada precisa retornar (não travar em recursão infinita) e reportar um erro
            // claro em vez de estourar StackOverflowException.
            var exitCode = GenCommand.Run(aPath, outDir: null, output: output);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("iclo", output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(_dir, ".globalconfig")));
        }

        [Fact]
        public void Gen_SlotForaDoPoolESlotDuplicado_FalhaESemGerarArquivos()
        {
            const string yamlComSlotsInvalidos = @"
schema: arch-rules/v1

layers:
  Controller:
    match:
      - nameSuffix: ""Controller""
  Repository:
    match:
      - nameSuffix: ""Repository""

severities:
  media: warning

defaults:
  severity: media

rules:
  - id: ACME-001
    slot: ARCH0002
    type: forbidden-call
    from: Controller
    to: Repository

  - id: ACME-002
    slot: ARCH0002
    type: forbidden-call
    from: Controller
    to: Repository

  - id: ACME-003
    slot: ARCH0513
    type: forbidden-call
    from: Controller
    to: Repository
";
            var yamlPath = WriteYaml("arch-rules.yaml", yamlComSlotsInvalidos);
            var output = new StringWriter();

            var exitCode = GenCommand.Run(yamlPath, outDir: null, output: output);

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(Path.Combine(_dir, ".globalconfig")));
            Assert.False(File.Exists(Path.Combine(_dir, "arch-rules.lock.yaml")));

            var text = output.ToString();
            Assert.Contains("ARCH9002", text);
            Assert.Contains("duplicado", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("fora do pool", text, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("info")]
        [InlineData("hidden")]
        public void Gen_SeveridadeInvalida_ErroDeValidacaoNaoAceitoSilenciosamente(string valorInvalido)
        {
            var yamlComSeveridadeInvalida = $@"
schema: arch-rules/v1

layers:
  Controller:
    match:
      - nameSuffix: ""Controller""
  Repository:
    match:
      - nameSuffix: ""Repository""

severities:
  restrita: {valorInvalido}

defaults:
  severity: restrita

rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
";
            var yamlPath = WriteYaml("arch-rules.yaml", yamlComSeveridadeInvalida);
            var output = new StringWriter();

            var exitCode = GenCommand.Run(yamlPath, outDir: null, output: output);

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(Path.Combine(_dir, ".globalconfig")));
            Assert.False(File.Exists(Path.Combine(_dir, "arch-rules.lock.yaml")));
            Assert.Contains("CLI-SEVERITY", output.ToString());
        }

        [Fact]
        public void Validate_YamlCorreto_ExitZeroSemGerarArquivos()
        {
            var yamlPath = WriteYaml("arch-rules.yaml", ValidYaml);
            var output = new StringWriter();

            var exitCode = ValidateCommand.Run(yamlPath, output);

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(Path.Combine(_dir, ".globalconfig")));
            Assert.False(File.Exists(Path.Combine(_dir, "arch-rules.lock.yaml")));
            Assert.Contains("OK", output.ToString());
        }
    }
}
