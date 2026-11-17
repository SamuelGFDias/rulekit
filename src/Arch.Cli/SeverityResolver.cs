using System;
using System.Collections.Generic;
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
    /// O vocabulário e a resolução nome → valor vivem em <see cref="SeverityVocabulary"/>
    /// (Arch.Config, fonte única — issue #19); aqui fica só a tradução para diagnósticos do CLI.
    /// </summary>
    public static class SeverityResolver
    {
        /// <summary>Código de diagnóstico do CLI para severidade não resolvível/inválida. Não é
        /// um código ARCH9xxx: essa faixa é reservada aos erros do motor (schema/slot,
        /// ADR-001 D4), e esta validação é responsabilidade exclusiva desta trilha (arch-rules
        /// gen/validate), não do parser nem do analyzer.</summary>
        public const string InvalidSeverityCode = "CLI-SEVERITY";

        /// <summary>Vocabulário aceito — derivado de <see cref="SeverityVocabulary"/> (fonte única, issue #19).</summary>
        public static readonly HashSet<string> AllowedAnalyzerConfigValues =
            new HashSet<string>(SeverityVocabulary.AllowedValues, StringComparer.Ordinal);

        public static IReadOnlyDictionary<string, string> Resolve(ArchConfig config, out IReadOnlyList<CliDiagnostic> errors)
        {
            var resolved = new Dictionary<string, string>();
            var errorList = new List<CliDiagnostic>();

            foreach (var rule in config.Rules)
            {
                var resolution = SeverityVocabulary.Resolve(config, rule);

                switch (resolution.Status)
                {
                    case SeverityResolutionStatus.NotDeclared:
                        errorList.Add(new CliDiagnostic(
                            InvalidSeverityCode,
                            $"Regra '{rule.Id}': nenhuma 'severity' declarada e nenhum 'defaults.severity' definido no YAML.",
                            0,
                            0));
                        break;

                    case SeverityResolutionStatus.UnknownName:
                        errorList.Add(new CliDiagnostic(
                            InvalidSeverityCode,
                            $"Regra '{rule.Id}': severidade '{resolution.Name}' não está declarada no bloco 'severities:'.",
                            0,
                            0));
                        break;

                    case SeverityResolutionStatus.InvalidValue:
                        errorList.Add(new CliDiagnostic(
                            InvalidSeverityCode,
                            $"Regra '{rule.Id}': severities.{resolution.Name} = '{resolution.Value}' não é um valor válido de " +
                            $"AnalyzerConfig. Use apenas {string.Join("/", SeverityVocabulary.AllowedValues)} (nunca info/hidden — ADR-001 D3).",
                            0,
                            0));
                        break;

                    default:
                        resolved[rule.Id] = resolution.Value;
                        break;
                }
            }

            errors = errorList;
            return resolved;
        }
    }
}
