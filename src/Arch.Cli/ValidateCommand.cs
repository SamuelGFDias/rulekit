using System.Collections.Generic;
using System.IO;

namespace Arch.Cli
{
    /// <summary>
    /// `arch-rules validate &lt;caminho-do-yaml&gt;`: mesmo parsing e mesmas validações de
    /// <see cref="GenCommand"/> (extends resolvido, slots, severidades), mas SEM gerar nenhum
    /// arquivo — só imprime erros/sucesso e usa o código de saída (0 = ok, != 0 = erro) para uso
    /// em CI. Avisos: ARCH9008 (regra malformada descartada) FALHA o comando (saída 1) — é sempre erro do
    /// usuário; ARCH9003 (tipo desconhecido, possivelmente de versão futura) é só aviso; com
    /// <c>--strict</c> QUALQUER aviso falha. Se já existir um `arch-rules.lock.yaml` no mesmo diretório do YAML, compara com o
    /// que seria gerado agora e AVISA (não falha, salvo com <c>--strict</c>) sobre divergência — o cenário do ARCH9005
    /// (ADR-001 D3): "o YAML e o `.globalconfig` podem divergir se alguém editar um sem
    /// regenerar o outro".
    /// </summary>
    public static class ValidateCommand
    {
        public const string LockFileName = "arch-rules.lock.yaml";

        public static int Run(string yamlPath, TextWriter output, bool strict = false)
        {
            if (!File.Exists(yamlPath))
            {
                output.WriteLine($"Arquivo não encontrado: '{yamlPath}'.");
                return 1;
            }

            var result = ConfigPipeline.Run(yamlPath);

            var fatalWarnings = new List<CliDiagnostic>();

            foreach (var warning in result.Warnings)
            {
                if (ConfigPipeline.Result.IsFatalWarning(warning, strict, failOnMalformedRule: true))
                {
                    fatalWarnings.Add(warning);
                }
                else
                {
                    output.WriteLine("AVISO " + warning);
                }
            }

            if (!result.Success)
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

            var yamlDir = Path.GetDirectoryName(Path.GetFullPath(yamlPath)) ?? ".";
            var lockPath = Path.Combine(yamlDir, LockFileName);

            if (File.Exists(lockPath))
            {
                var expectedEntries = LockFileWriter.BuildEntries(result.Config, result.ResolvedSeverityByRuleId);
                var expectedContent = LockFileWriter.BuildContent(expectedEntries);
                var actualContent = File.ReadAllText(lockPath);

                if (Normalize(expectedContent) != Normalize(actualContent))
                {
                    var divergence = new CliDiagnostic(
                        "ARCH9005",
                        "'arch-rules.lock.yaml' existente diverge do que seria gerado agora a partir do YAML " +
                        "(ADR-001 D3). Rode 'arch-rules gen' para regenerar.",
                        0,
                        0);

                    if (ConfigPipeline.Result.IsFatalWarning(divergence, strict, failOnMalformedRule: true))
                    {
                        fatalWarnings.Add(divergence);
                    }
                    else
                    {
                        output.WriteLine("AVISO " + divergence);
                    }
                }
            }

            if (fatalWarnings.Count > 0)
            {
                foreach (var warning in fatalWarnings)
                {
                    output.WriteLine("ERRO " + warning);
                }

                return 1;
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
