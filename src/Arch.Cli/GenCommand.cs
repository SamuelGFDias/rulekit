using System.IO;

namespace Arch.Cli
{
    /// <summary>
    /// `arch-rules gen &lt;caminho-do-yaml&gt; [--out &lt;diretorio&gt;]`: LÊ os slots já
    /// declarados no YAML (v1 não aloca nada — ADR-001 Action Item #5, Opção C), VALIDA que são
    /// únicos e dentro do pool, e MATERIALIZA a severidade efetiva num `.globalconfig` +
    /// `arch-rules.lock.yaml`. Qualquer erro bloqueante (schema, extends, slot, severidade) sai
    /// com código de saída != 0 e não gera nenhum arquivo.
    /// </summary>
    public static class GenCommand
    {
        public static int Run(string yamlPath, string outDir, TextWriter output)
        {
            if (!File.Exists(yamlPath))
            {
                output.WriteLine($"Arquivo não encontrado: '{yamlPath}'.");
                return 1;
            }

            var result = ConfigPipeline.Run(yamlPath);

            foreach (var warning in result.Warnings)
            {
                output.WriteLine("AVISO " + warning);
            }

            if (!result.Success)
            {
                foreach (var error in result.BlockingErrors)
                {
                    output.WriteLine("ERRO " + error);
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
