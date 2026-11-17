using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arch.Config;

namespace Arch.Cli
{
    /// <summary>
    /// Adaptador de DISCO para <see cref="ExtendsChainResolver"/> (Arch.Config — o mesmo resolvedor
    /// que o analyzer usa contra @(AdditionalFiles), issue #18). Aqui ficam só as decisões do CLI:
    /// caminhos absolutos via <see cref="Path.GetFullPath(string)"/>, o loader lê do disco
    /// (<c>null</c> quando o arquivo não existe) e a POLÍTICA de erro — todo problema de cadeia
    /// (ausente, ciclo, profundidade) é bloqueante para `gen`/`validate`, com o código
    /// <see cref="ExtendsErrorCode"/>. A extração de `extends:` é a de
    /// <see cref="ArchConfigParser.PeekExtends"/>: o CLI não parseia YAML diretamente.
    /// </summary>
    public static class ExtendsResolver
    {
        /// <summary>Código de diagnóstico do CLI para problema de cadeia de `extends`.</summary>
        public const string ExtendsErrorCode = "CLI-EXTENDS";

        /// <summary>
        /// Resolve a cadeia a partir de <paramref name="yamlPath"/>. Exceções de I/O ao ler um arquivo
        /// existente (permissão, diretório no lugar de arquivo) propagam — o chamador
        /// (<see cref="ConfigPipeline"/>) as converte em erro limpo.
        /// </summary>
        public static ExtendsChainResult Resolve(string yamlPath)
        {
            var rootPath = Path.GetFullPath(yamlPath);

            return ExtendsChainResolver.Resolve(
                rootPath,
                File.ReadAllText(rootPath),
                path => File.Exists(path) ? File.ReadAllText(path) : null,
                // `relative` já chega normalizado por ExtendsChainResolver.NormalizeDeclaredPath (trim + `\`
                // → `/`), a mesma normalização do analyzer — revisão B1.
                (declaringPath, relative) =>
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(declaringPath) ?? ".", relative)),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Mensagens dos problemas da cadeia, na forma que o CLI sempre imprimiu.</summary>
        public static IReadOnlyList<CliDiagnostic> ToDiagnostics(IReadOnlyList<ExtendsChainProblem> problems)
        {
            var diagnostics = new List<CliDiagnostic>(problems.Count);

            foreach (var problem in problems)
            {
                string message;
                switch (problem.Kind)
                {
                    case ExtendsChainProblemKind.Cycle:
                        var cycle = string.Join(
                            " -> ",
                            problem.Stack.Select(Path.GetFileName).Append(Path.GetFileName(problem.ResolvedPath)));
                        message = $"Ciclo de 'extends' detectado: {cycle}.";
                        break;

                    case ExtendsChainProblemKind.DepthExceeded:
                        message =
                            $"Cadeia de 'extends' longa demais ao tentar resolver '{problem.DeclaredPath}' a partir de " +
                            $"'{problem.DeclaringPath}' (limite: {ExtendsChainResolver.DefaultMaxDepth} níveis / " +
                            $"{ExtendsChainResolver.DefaultMaxTexts} arquivos).";
                        break;

                    default:
                        message = $"Arquivo referenciado em 'extends' não encontrado: '{problem.ResolvedPath}'.";
                        break;
                }

                diagnostics.Add(new CliDiagnostic(ExtendsErrorCode, message, 0, 0));
            }

            return diagnostics;
        }
    }
}
