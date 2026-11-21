using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arch.Config;

namespace Arch.Cli
{
    /// <summary>Modo do <c>arch-rules baseline</c>.</summary>
    public enum BaselineMode
    {
        /// <summary>Regenera o baseline de cada diretório com TODOS os resultados do SARIF (pode acrescentar).</summary>
        Regenerate,

        /// <summary>Ratchet: mantém só as entradas existentes que ainda aparecem; nunca acrescenta.</summary>
        Update,

        /// <summary>Não escreve; sai 1 se algum baseline existente tem entrada obsoleta.</summary>
        Check,
    }

    /// <summary>
    /// <c>arch-rules baseline &lt;sarif&gt;... [--update | --check] [--root &lt;dir&gt;] [--strict]</c> (issue #25). O analyzer não faz I/O
    /// (RS1035) e o CLI não compila código, então o baseline sai do log SARIF do build
    /// (<c>-p:ErrorLog=build.sarif,version=2.1</c>): cada violação de slot vira uma entrada
    /// <c>{ rule, file, subject }</c> no <c>arch-rules.baseline.yaml</c> do diretório ancestral mais próximo que
    /// contém <c>arch-rules.yaml</c>, com o caminho relativo a esse diretório. O <c>DiagnosticSuppressor</c> do
    /// pacote lê o arquivo e suprime os diagnósticos que casam. Além dos diretórios que aparecem no SARIF, o
    /// comando descobre todo <c>arch-rules.baseline.yaml</c> sob <c>--root</c> (padrão: diretório atual): um
    /// baseline cujas violações todas foram corrigidas não tem resultado nenhum no SARIF e, sem isso, escaparia
    /// do <c>--check</c>/<c>--update</c>.
    /// </summary>
    public static class BaselineCommand
    {
        private const string ConfigFileName = "arch-rules.yaml";

        private static readonly string[] IgnoredDirectories = { "bin", "obj", ".git", "node_modules" };

        /// <param name="root">Diretório sob o qual os baselines são descobertos; null = diretório atual.</param>
        public static int Run(
            IReadOnlyList<string> sarifPaths,
            BaselineMode mode,
            TextWriter output,
            bool strict = false,
            string root = null)
        {
            try
            {
                return RunCore(sarifPaths, mode, output, strict, root);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                output.WriteLine("ERRO " + ex.Message);
                return 1;
            }
        }

        private static int RunCore(
            IReadOnlyList<string> sarifPaths,
            BaselineMode mode,
            TextWriter output,
            bool strict,
            string root)
        {
            var violations = new List<SarifViolation>();
            var warnings = new List<string>();
            var failed = false;

            foreach (var sarifPath in sarifPaths)
            {
                if (!File.Exists(sarifPath))
                {
                    output.WriteLine($"ERRO SARIF não encontrado: '{sarifPath}'.");
                    failed = true;
                }
                else if (!SarifReader.TryRead(sarifPath, violations, warnings, out var readError))
                {
                    output.WriteLine("ERRO " + readError);
                    failed = true;
                }
            }

            if (failed)
            {
                return 1;
            }

            var byDirectory = GroupByProjectDirectory(violations, warnings);

            if (strict && warnings.Count > 0)
            {
                foreach (var warning in warnings)
                {
                    output.WriteLine("ERRO " + warning);
                }

                return 1;
            }

            foreach (var warning in warnings)
            {
                output.WriteLine("AVISO " + warning);
            }

            var rootDirectory = Path.GetFullPath(string.IsNullOrEmpty(root) ? Directory.GetCurrentDirectory() : root);
            if (!Directory.Exists(rootDirectory))
            {
                output.WriteLine($"ERRO --root '{rootDirectory}' não é um diretório existente.");
                return 1;
            }

            var discovered = DiscoverBaselineDirectories(rootDirectory);
            foreach (var directory in discovered)
            {
                if (!byDirectory.ContainsKey(directory))
                {
                    byDirectory[directory] = new HashSet<BaselineEntry>();
                }
            }

            var directories = byDirectory.Keys.OrderBy(d => d, StringComparer.Ordinal).ToList();
            return mode == BaselineMode.Regenerate
                ? Regenerate(directories, byDirectory, output)
                : UpdateOrCheck(directories, byDirectory, mode, output);
        }

        /// <summary>Diretórios sob <paramref name="root"/> (recursivo) que têm <c>arch-rules.baseline.yaml</c>.</summary>
        private static List<string> DiscoverBaselineDirectories(string root)
        {
            var found = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (File.Exists(Path.Combine(directory, BaselineFile.FileName)))
                {
                    found.Add(directory);
                }

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(directory).ToList();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    var name = Path.GetFileName(child);
                    if (IgnoredDirectories.Contains(name, StringComparer.Ordinal)
                        || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pending.Push(child);
                }
            }

            return found;
        }

        private static Dictionary<string, HashSet<BaselineEntry>> GroupByProjectDirectory(
            List<SarifViolation> violations,
            List<string> warnings)
        {
            var byDirectory = new Dictionary<string, HashSet<BaselineEntry>>(StringComparer.Ordinal);
            var projectDirCache = new Dictionary<string, string>(StringComparer.Ordinal);
            var warnedFiles = new HashSet<string>(StringComparer.Ordinal);

            foreach (var violation in violations)
            {
                var sourceDirectory = Path.GetDirectoryName(violation.SourceFile) ?? string.Empty;
                if (!projectDirCache.TryGetValue(sourceDirectory, out var projectDirectory))
                {
                    projectDirectory = FindProjectDirectory(sourceDirectory);
                    projectDirCache[sourceDirectory] = projectDirectory;
                }

                if (projectDirectory == null)
                {
                    if (warnedFiles.Add(violation.SourceFile))
                    {
                        warnings.Add(
                            $"'{violation.SourceFile}' não tem '{ConfigFileName}' em nenhum diretório ancestral: resultados desse arquivo ignorados.");
                    }

                    continue;
                }

                if (!byDirectory.TryGetValue(projectDirectory, out var entries))
                {
                    entries = new HashSet<BaselineEntry>();
                    byDirectory[projectDirectory] = entries;
                }

                entries.Add(new BaselineEntry(
                    violation.Rule,
                    Path.GetRelativePath(projectDirectory, violation.SourceFile),
                    violation.Subject));
            }

            return byDirectory;
        }

        private static string FindProjectDirectory(string startDirectory)
        {
            for (var directory = startDirectory; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            {
                if (File.Exists(Path.Combine(directory, ConfigFileName)))
                {
                    return directory;
                }
            }

            return null;
        }

        private static int Regenerate(
            List<string> directories,
            Dictionary<string, HashSet<BaselineEntry>> byDirectory,
            TextWriter output)
        {
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, BaselineFile.FileName);
                var count = byDirectory[directory].Count;
                if (!TryWrite(path, BaselineFile.Write(byDirectory[directory]), output))
                {
                    return 1;
                }

                output.WriteLine(count == 0
                    ? $"Gerado: {path} (vazio: nenhuma violação no SARIF para este diretório; entries: [])"
                    : $"Gerado: {path} ({count} entrada(s))");
            }

            if (directories.Count == 0)
            {
                output.WriteLine("Nenhuma violação de slot com archRuleId/archSubject no SARIF e nenhum baseline sob --root: nada gerado.");
            }

            return 0;
        }

        private static int UpdateOrCheck(
            List<string> directories,
            Dictionary<string, HashSet<BaselineEntry>> byDirectory,
            BaselineMode mode,
            TextWriter output)
        {
            var plans = new List<(string Path, List<BaselineEntry> Existing, List<BaselineEntry> Obsolete)>();
            var failed = false;

            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, BaselineFile.FileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    output.WriteLine($"ERRO {path}: {ex.Message}");
                    failed = true;
                    continue;
                }

                var parsed = BaselineFile.Parse(text);
                if (!parsed.IsValid)
                {
                    foreach (var error in parsed.Errors)
                    {
                        output.WriteLine($"ERRO {path}: [{error.Code}] linha {error.Line}, coluna {error.Column}: {error.Message}");
                    }

                    failed = true;
                    continue;
                }

                var current = byDirectory.TryGetValue(directory, out var entriesInSarif)
                    ? entriesInSarif
                    : new HashSet<BaselineEntry>();
                plans.Add((path, parsed.Entries.ToList(), parsed.Entries.Where(e => !current.Contains(e)).ToList()));
            }

            if (failed)
            {
                return 1;
            }

            var hasObsolete = false;
            foreach (var plan in plans)
            {
                if (plan.Obsolete.Count == 0)
                {
                    output.WriteLine(mode == BaselineMode.Check
                        ? $"OK: {plan.Path} ({plan.Existing.Count} entrada(s), nenhuma obsoleta)"
                        : $"Sem alterações: {plan.Path}");
                    continue;
                }

                if (mode == BaselineMode.Check)
                {
                    hasObsolete = true;
                    output.WriteLine($"ERRO {plan.Path}: {plan.Obsolete.Count} entrada(s) obsoleta(s) (a violação não aparece mais no SARIF):");
                }
                else
                {
                    var remaining = plan.Existing.Except(plan.Obsolete).ToList();
                    if (!TryWrite(plan.Path, BaselineFile.Write(remaining), output))
                    {
                        return 1;
                    }

                    output.WriteLine(remaining.Count == 0
                        ? $"Atualizado: {plan.Path} ({plan.Obsolete.Count} entrada(s) obsoleta(s) removida(s); baseline vazio, mantido com entries: []):"
                        : $"Atualizado: {plan.Path} ({plan.Obsolete.Count} entrada(s) obsoleta(s) removida(s)):");
                }

                foreach (var entry in plan.Obsolete.OrderBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Rule, StringComparer.Ordinal))
                {
                    output.WriteLine($"  - {entry.Rule} | {entry.File} | {entry.Subject}");
                }
            }

            if (plans.Count == 0)
            {
                output.WriteLine("Nenhum baseline existente sob --root nem nos diretórios do SARIF: nada a fazer.");
            }

            return hasObsolete ? 1 : 0;
        }

        private static bool TryWrite(string path, string content, TextWriter output)
        {
            try
            {
                File.WriteAllText(path, content);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                output.WriteLine($"ERRO {path}: {ex.Message}");
                return false;
            }
        }
    }
}
