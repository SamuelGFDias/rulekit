using System;
using System.Collections.Generic;
using System.Linq;
using Arch.Config;

namespace Arch.Cli
{
    /// <summary>
    /// Resolve a severidade efetiva de cada regra — nome livre do consumidor em `severities:`
    /// (ou `defaults.severity` quando a regra não declara `severity`) mapeado ao valor aceito por
    /// `dotnet_diagnostic.&lt;slot&gt;.severity` — e valida o vocabulário de destino (ADR-001 D3):
    /// só <c>error</c>/<c>warning</c>/<c>suggestion</c>/<c>silent</c>/<c>none</c> são aceitos.
    /// `info`/`hidden` são valores de <c>DiagnosticSeverity</c>, não de AnalyzerConfig — nunca
    /// emitidos por este gerador, mesmo que apareçam por engano no YAML. `severities:` já é
    /// repassado como veio quando usa esses nomes diretamente (não há conversão a fazer); o que
    /// existe aqui é validação, não tradução.
    ///
    /// Arch.Config.Internal.ArchConfigDocumentParser não valida esse vocabulário — aceita o valor
    /// de `severities:` como string livre (ver ArchConfigDocumentParser.ParseSeverities) — por
    /// isso esta validação vive no CLI, não no parser.
    /// </summary>
    public static class SeverityResolver
    {
        /// <summary>Código de diagnóstico do CLI para severidade não resolvível/inválida. Não é
        /// um código ARCH9xxx: essa faixa é reservada aos erros do motor (schema/slot,
        /// ADR-001 D4), e esta validação é responsabilidade exclusiva desta trilha (arch-rules
        /// gen/validate), não do parser nem do analyzer.</summary>
        public const string InvalidSeverityCode = "CLI-SEVERITY";

        public static readonly HashSet<string> AllowedAnalyzerConfigValues =
            new HashSet<string>(StringComparer.Ordinal) { "error", "warning", "suggestion", "silent", "none" };

        public static IReadOnlyDictionary<string, string> Resolve(ArchConfig config, out IReadOnlyList<CliDiagnostic> errors)
        {
            var resolved = new Dictionary<string, string>();
            var errorList = new List<CliDiagnostic>();

            foreach (var rule in config.Rules)
            {
                var severityName = rule.SeverityName ?? config.DefaultSeverityName;

                if (severityName == null)
                {
                    errorList.Add(new CliDiagnostic(
                        InvalidSeverityCode,
                        $"Regra '{rule.Id}': nenhuma 'severity' declarada e nenhum 'defaults.severity' definido no YAML.",
                        0,
                        0));
                    continue;
                }

                if (!config.Severities.TryGetValue(severityName, out var value))
                {
                    errorList.Add(new CliDiagnostic(
                        InvalidSeverityCode,
                        $"Regra '{rule.Id}': severidade '{severityName}' não está declarada no bloco 'severities:'.",
                        0,
                        0));
                    continue;
                }

                if (!AllowedAnalyzerConfigValues.Contains(value))
                {
                    errorList.Add(new CliDiagnostic(
                        InvalidSeverityCode,
                        $"Regra '{rule.Id}': severities.{severityName} = '{value}' não é um valor válido de " +
                        "AnalyzerConfig. Use apenas error/warning/suggestion/silent/none (nunca info/hidden — ADR-001 D3).",
                        0,
                        0));
                    continue;
                }

                resolved[rule.Id] = value;
            }

            errors = errorList;
            return resolved;
        }
    }
}
