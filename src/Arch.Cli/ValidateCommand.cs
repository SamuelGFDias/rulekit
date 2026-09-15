using System.IO;

namespace Arch.Cli
{
    /// <summary>
    /// `arch-rules validate &lt;caminho-do-yaml&gt;`: mesmo parsing e mesmas validações de
    /// <see cref="GenCommand"/> (extends resolvido, slots, severidades), mas SEM gerar nenhum
    /// arquivo — só imprime erros/sucesso e usa o código de saída (0 = ok, != 0 = erro) para uso
    /// em CI. Se já existir um `arch-rules.lock.yaml` no mesmo diretório do YAML, compara com o
    /// que seria gerado agora e AVISA (não falha) sobre divergência — o cenário do ARCH9005
    /// (ADR-001 D3): "o YAML e o `.globalconfig` podem divergir se alguém editar um sem
    /// regenerar o outro".
    /// </summary>
    public static class ValidateCommand
    {
        public const string LockFileName = "arch-rules.lock.yaml";

        public static int Run(string yamlPath, TextWriter output)
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

            var yamlDir = Path.GetDirectoryName(Path.GetFullPath(yamlPath)) ?? ".";
            var lockPath = Path.Combine(yamlDir, LockFileName);

            if (File.Exists(lockPath))
            {
                var expectedEntries = LockFileWriter.BuildEntries(result.Config, result.ResolvedSeverityByRuleId);
                var expectedContent = LockFileWriter.BuildContent(expectedEntries);
                var actualContent = File.ReadAllText(lockPath);

                if (Normalize(expectedContent) != Normalize(actualContent))
                {
                    output.WriteLine(
                        "AVISO [ARCH9005] linha 0, coluna 0: 'arch-rules.lock.yaml' existente diverge do que seria " +
                        "gerado agora a partir do YAML (ADR-001 D3). Rode 'arch-rules gen' para regenerar.");
                }
            }

            output.WriteLine("OK: configuração válida.");
            return 0;
        }

        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n").Trim();
        }
    }
}
