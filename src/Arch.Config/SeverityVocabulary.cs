using System;
using System.Collections.Generic;
using System.Linq;

namespace Arch.Config
{
    /// <summary>Resultado de <see cref="SeverityVocabulary.Resolve"/>.</summary>
    public enum SeverityResolutionStatus
    {
        /// <summary>Severidade resolvida para um valor aceito pelo AnalyzerConfig.</summary>
        Resolved,

        /// <summary>A regra não declara <c>severity</c> e não há <c>defaults.severity</c>.</summary>
        NotDeclared,

        /// <summary>O nome usado não existe no bloco <c>severities:</c>.</summary>
        UnknownName,

        /// <summary>O nome existe, mas mapeia para um valor fora do vocabulário do AnalyzerConfig.</summary>
        InvalidValue,
    }

    /// <summary>Severidade efetiva de uma regra (ADR-001 D3).</summary>
    public sealed class SeverityResolution
    {
        public SeverityResolutionStatus Status { get; }

        /// <summary>Nome livre usado (da regra ou de <c>defaults.severity</c>); null em <see cref="SeverityResolutionStatus.NotDeclared"/>.</summary>
        public string Name { get; }

        /// <summary>Valor mapeado em <c>severities:</c>; preenchido em <see cref="SeverityResolutionStatus.Resolved"/> e <see cref="SeverityResolutionStatus.InvalidValue"/>.</summary>
        public string Value { get; }

        public SeverityResolution(SeverityResolutionStatus status, string name, string value)
        {
            Status = status;
            Name = name;
            Value = value;
        }
    }

    /// <summary>
    /// Vocabulário de severidade de destino (ADR-001 D3) — valores aceitos por
    /// <c>dotnet_diagnostic.&lt;slot&gt;.severity</c> — e a resolução nome livre → valor. Fonte única
    /// para o CLI e para quem mais precisar validar <c>severities:</c> (issue #19).
    /// <c>info</c>/<c>hidden</c> são valores de <c>DiagnosticSeverity</c>, não de AnalyzerConfig, e
    /// por isso não fazem parte do vocabulário. Comparação ordinal (como o gerador sempre fez).
    /// </summary>
    public static class SeverityVocabulary
    {
        public const string Error = "error";
        public const string Warning = "warning";
        public const string Suggestion = "suggestion";
        public const string Silent = "silent";
        public const string None = "none";

        /// <summary>Valores aceitos, na ordem do mais para o menos severo.</summary>
        public static IReadOnlyList<string> AllowedValues { get; } =
            Array.AsReadOnly(new[] { Error, Warning, Suggestion, Silent, None });

        public static bool IsValid(string value)
            => value != null && AllowedValues.Contains(value, StringComparer.Ordinal);

        /// <summary>
        /// Resolve a severidade efetiva de <paramref name="rule"/>: <c>severity</c> da regra, ou
        /// <c>defaults.severity</c>, mapeado pelo bloco <c>severities:</c> de <paramref name="config"/>.
        /// </summary>
        public static SeverityResolution Resolve(ArchConfig config, RuleDefinition rule)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            var name = rule.SeverityName ?? config.DefaultSeverityName;
            if (name == null)
            {
                return new SeverityResolution(SeverityResolutionStatus.NotDeclared, null, null);
            }

            if (!config.Severities.TryGetValue(name, out var value))
            {
                return new SeverityResolution(SeverityResolutionStatus.UnknownName, name, null);
            }

            return IsValid(value)
                ? new SeverityResolution(SeverityResolutionStatus.Resolved, name, value)
                : new SeverityResolution(SeverityResolutionStatus.InvalidValue, name, value);
        }
    }
}
