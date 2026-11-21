using System;
using System.Collections.Immutable;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Arch.Analyzer
{
    /// <summary>
    /// Baseline de violações (issue #25): suprime os diagnósticos de slot (<c>ARCH0001..ARCH0512</c>) que
    /// casam com uma entrada do <c>arch-rules.baseline.yaml</c> — regra (<c>archRuleId</c>), arquivo relativo
    /// ao diretório do baseline e assunto estável (<c>archSubject</c>). Todas as ocorrências iguais são
    /// suprimidas (sem contagem). A supressão fica registrada no SARIF; não é silêncio.
    ///
    /// <para>O baseline chega por <c>context.Options.AdditionalFiles</c> (o <c>.targets</c> do pacote o inclui
    /// pelo glob <c>arch-rules.*.yaml</c>) e o texto é analisado uma vez por instância de
    /// <see cref="Microsoft.CodeAnalysis.Text.SourceText"/> (<see cref="BaselineCache"/>) — sem I/O (RS1035).
    /// Baseline ausente ou inválido → nada é suprimido (o <c>ARCH9009</c> é do analyzer).</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class BaselineSuppressor : DiagnosticSuppressor
    {
        public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions
        {
            get { return ArchDiagnostics.BaselineSuppressions; }
        }

        public override void ReportSuppressions(SuppressionAnalysisContext context)
        {
            if (context.ReportedDiagnostics.IsEmpty)
            {
                return;
            }

            var baselineFile = FindBaseline(context.Options.AdditionalFiles);
            if (baselineFile == null)
            {
                return;
            }

            var text = baselineFile.GetText(context.CancellationToken);
            if (text == null)
            {
                return;
            }

            var baseline = BaselineCache.Get(text);
            if (!baseline.IsValid)
            {
                return;
            }

            var baselineDirectory = GetDirectory(ArchRulesAnalyzer.NormalizePath(baselineFile.Path));

            foreach (var diagnostic in context.ReportedDiagnostics)
            {
                string ruleId;
                string subject;
                if (diagnostic.Properties == null
                    || !diagnostic.Properties.TryGetValue(ArchDiagnosticProperties.RuleId, out ruleId)
                    || !diagnostic.Properties.TryGetValue(ArchDiagnosticProperties.Subject, out subject))
                {
                    continue;
                }

                SuppressionDescriptor suppression;
                if (!ArchDiagnostics.TryGetBaselineSuppression(diagnostic.Id, out suppression))
                {
                    continue;
                }

                var filePath = diagnostic.Location.SourceTree?.FilePath;
                string relative;
                if (string.IsNullOrEmpty(filePath) || !TryMakeRelative(baselineDirectory, filePath, out relative))
                {
                    continue;
                }

                if (baseline.Contains(ruleId, relative, subject))
                {
                    context.ReportSuppression(Suppression.Create(suppression, diagnostic));
                }
            }
        }

        private static AdditionalText FindBaseline(ImmutableArray<AdditionalText> additionalFiles)
        {
            // Sem LINQ: roda a cada rodada de diagnósticos.
            for (var i = 0; i < additionalFiles.Length; i++)
            {
                var file = additionalFiles[i];
                if (file != null && string.Equals(GetFileName(file.Path), BaselineFile.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }

            return null;
        }

        private static string GetFileName(string path)
        {
            var normalized = ArchRulesAnalyzer.NormalizePath(path);
            var slash = normalized.LastIndexOf('/');
            return slash < 0 ? normalized : normalized.Substring(slash + 1);
        }

        private static string GetDirectory(string normalizedPath)
        {
            var slash = normalizedPath.LastIndexOf('/');
            return slash < 0 ? string.Empty : slash == 0 ? "/" : normalizedPath.Substring(0, slash);
        }

        /// <summary>
        /// Caminho do fonte relativo ao diretório do baseline (com <c>/</c>); falso se o fonte está fora dele.
        /// </summary>
        internal static bool TryMakeRelative(string baselineDirectory, string sourcePath, out string relative)
        {
            var normalized = ArchRulesAnalyzer.NormalizePath(sourcePath);
            var prefix = baselineDirectory.EndsWith("/", StringComparison.Ordinal) ? baselineDirectory : baselineDirectory + "/";

            if (baselineDirectory.Length == 0)
            {
                relative = normalized;
                return normalized.Length > 0;
            }

            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && normalized.Length > prefix.Length)
            {
                relative = normalized.Substring(prefix.Length);
                return true;
            }

            relative = null;
            return false;
        }
    }
}
