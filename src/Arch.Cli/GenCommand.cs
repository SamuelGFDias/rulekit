using System.IO;
using System.Linq;

namespace Arch.Cli
{
    /// <summary>
    /// `arch-rules gen &lt;caminho-do-yaml&gt; [--out &lt;diretorio&gt;]`: LÊ os slots já
    /// declarados no YAML (v1 não aloca nada — ADR-001 Action Item #5, Opção C), VALIDA que são
    /// únicos e dentro do pool, e MATERIALIZA a severidade efetiva num `.globalconfig` +
    /// `arch-rules.lock.yaml`. Qualquer erro bloqueante (schema, extends, slot, severidade) sai
    /// com código de saída != 0 e não gera nenhum arquivo. Avisos (ARCH9003/ARCH9008) não impedem a
    /// geração, salvo com <c>--strict</c> (qualquer aviso vira erro e nada é gerado).
    /// </summary>
    public static class GenCommand
    {
        public static int Run(string yamlPath, string outDir, TextWriter output, bool strict = false)
        {
            if (!File.Exists(yamlPath))
            {
                output.WriteLine($"Arquivo não encontrado: '{yamlPath}'.");
                return 1;
            }

            var result = ConfigPipeline.Run(yamlPath);

            // `gen` gera mesmo com avisos (inclusive ARCH9008, que só falha o `validate`); com
            // `--strict`, qualquer aviso falha o comando e nada é gerado.
            var fatalWarnings = result.Warnings
                .Where(w => ConfigPipeline.Result.IsFatalWarning(w, strict, failOnMalformedRule: false))
                .ToList();

            foreach (var warning in result.Warnings.Except(fatalWarnings))
            {
                output.WriteLine("AVISO " + warning);
            }

            if (!result.Success || fatalWarnings.Count > 0)
            {
                foreach (var error in result.BlockingErrors)
                {
                    output.WriteLine("ERRO " + error);
                }

                foreach (var warning in fatalWarnings)
                {
                    output.WriteLine("ERRO " + warning);
                }

                return 1;
            }

            var effectiveOutDir = string.IsNullOrEmpty(outDir)
                ? Path.GetDirectoryName(Path.GetFullPath(yamlPath))
                : outDir;

            Directory.CreateDirectory(effectiveOutDir!);

            var globalConfigContent = GlobalConfigWriter.BuildContent(result.Config, result.ResolvedSeverityByRuleId);
            var globalConfigPath = Path.Combine(effectiveOutDir, ".globalconfig");
            File.WriteAllText(globalConfigPath, globalConfigContent);

            var lockEntries = LockFileWriter.BuildEntries(result.Config, result.ResolvedSeverityByRuleId);
            var lockContent = LockFileWriter.BuildContent(lockEntries);
            var lockPath = Path.Combine(effectiveOutDir, "arch-rules.lock.yaml");
            File.WriteAllText(lockPath, lockContent);

            output.WriteLine($"Gerado: {globalConfigPath}");
            output.WriteLine($"Gerado: {lockPath}");

            return 0;
        }
    }
}
