using System.Threading;
using Arch.Config;
using Microsoft.CodeAnalysis;

namespace Arch.Analyzer
{
    /// <summary>
    /// ARCH9005 (ADR-001 D3, issue #8): compara a severidade DECLARADA no YAML para uma regra com a
    /// severidade EFETIVA do slot dela na compilação, e descreve a divergência.
    ///
    /// <para><b>Declarada:</b> <see cref="SeverityVocabulary.Resolve"/> (<c>severity</c> da regra ou
    /// <c>defaults.severity</c>, mapeado por <c>severities:</c>). Só um valor
    /// <see cref="SeverityResolutionStatus.Resolved"/> entra na comparação: sem severidade declarada
    /// (nem default) não há o que comparar, e nome desconhecido/valor inválido é erro de
    /// configuração que o CLI (<c>arch-rules validate</c>) reporta — o analyzer não duplica.</para>
    ///
    /// <para><b>Efetiva</b>, na precedência do compilador (Roslyn <c>CSharpDiagnosticFilter</c>):
    /// (1) <see cref="CompilationOptions.SpecificDiagnosticOptions"/> — <c>/warnaserror:ID</c>,
    /// <c>/nowarn:ID</c>, ruleset; (2) <see cref="SyntaxTreeOptionsProvider.TryGetGlobalDiagnosticValue"/>
    /// — é onde o <c>.globalconfig</c> gerado por <c>arch-rules gen</c> aterrissa; (3) o default do
    /// descriptor do slot. Um valor <see cref="ReportDiagnostic.Default"/> em (1) ou (2) significa
    /// "não configurado" e cai para o próximo nível.</para>
    ///
    /// <para><b>Limitações (deliberadas):</b> <c>.editorconfig</c> POR ÁRVORE
    /// (<c>[*.cs] dotnet_diagnostic.X.severity</c>) não é considerado — ele pode dar severidades
    /// diferentes a arquivos diferentes, e não existe uma única "efetiva" do slot para comparar; a
    /// fonte que o gerador produz é o <c>.globalconfig</c>. A opção geral
    /// (<see cref="CompilationOptions.GeneralDiagnosticOption"/>, <c>TreatWarningsAsErrors</c>) também
    /// não entra: ela muda todos os warnings, não o slot, e não indica <c>.globalconfig</c>
    /// desatualizado.</para>
    /// </summary>
    internal static class SeverityDivergence
    {
        /// <summary>
        /// Mensagem de ARCH9005 para <paramref name="rule"/>, ou <c>null</c> quando não há
        /// divergência (ou nada declarado para comparar).
        /// </summary>
        internal static string Describe(
            Compilation compilation,
            ArchConfig config,
            RuleDefinition rule,
            DiagnosticDescriptor slotDescriptor,
            CancellationToken cancellationToken)
        {
            var declared = SeverityVocabulary.Resolve(config, rule);
            if (declared.Status != SeverityResolutionStatus.Resolved)
            {
                return null;
            }

            ReportDiagnostic declaredReport;
            if (!TryFromAnalyzerConfigValue(declared.Value, out declaredReport))
            {
                return null;
            }

            var effective = GetEffective(compilation, slotDescriptor, cancellationToken);
            if (effective == declaredReport)
            {
                return null;
            }

            return "Regra '" + rule.Id + "' (slot " + slotDescriptor.Id + "): severidade declarada no arch-rules.yaml é '" +
                declared.Value + "' (nome '" + declared.Name + "'), mas a efetiva nesta compilação é '" +
                ToAnalyzerConfigValue(effective) + "'. O .globalconfig está ausente ou desatualizado — rode " +
                "'arch-rules gen' para regenerá-lo (ADR-001 D3).";
        }

        /// <summary>Severidade efetiva do slot (ver a precedência no XML doc da classe).</summary>
        internal static ReportDiagnostic GetEffective(
            Compilation compilation,
            DiagnosticDescriptor slotDescriptor,
            CancellationToken cancellationToken)
        {
            ReportDiagnostic value;

            if (compilation.Options.SpecificDiagnosticOptions.TryGetValue(slotDescriptor.Id, out value)
                && value != ReportDiagnostic.Default)
            {
                return value;
            }

            var provider = compilation.Options.SyntaxTreeOptionsProvider;
            if (provider != null
                && provider.TryGetGlobalDiagnosticValue(slotDescriptor.Id, cancellationToken, out value)
                && value != ReportDiagnostic.Default)
            {
                return value;
            }

            if (!slotDescriptor.IsEnabledByDefault)
            {
                return ReportDiagnostic.Suppress;
            }

            switch (slotDescriptor.DefaultSeverity)
            {
                case DiagnosticSeverity.Error:
                    return ReportDiagnostic.Error;
                case DiagnosticSeverity.Info:
                    return ReportDiagnostic.Info;
                case DiagnosticSeverity.Hidden:
                    return ReportDiagnostic.Hidden;
                default:
                    return ReportDiagnostic.Warn;
            }
        }

        private static bool TryFromAnalyzerConfigValue(string value, out ReportDiagnostic report)
        {
            switch (value)
            {
                case SeverityVocabulary.Error:
                    report = ReportDiagnostic.Error;
                    return true;
                case SeverityVocabulary.Warning:
                    report = ReportDiagnostic.Warn;
                    return true;
                case SeverityVocabulary.Suggestion:
                    report = ReportDiagnostic.Info;
                    return true;
                case SeverityVocabulary.Silent:
                    report = ReportDiagnostic.Hidden;
                    return true;
                case SeverityVocabulary.None:
                    report = ReportDiagnostic.Suppress;
                    return true;
                default:
                    report = ReportDiagnostic.Default;
                    return false;
            }
        }

        /// <summary>Nome no vocabulário do AnalyzerConfig — o mesmo que o usuário escreve no YAML.</summary>
        private static string ToAnalyzerConfigValue(ReportDiagnostic report)
        {
            switch (report)
            {
                case ReportDiagnostic.Error:
                    return SeverityVocabulary.Error;
                case ReportDiagnostic.Warn:
                    return SeverityVocabulary.Warning;
                case ReportDiagnostic.Info:
                    return SeverityVocabulary.Suggestion;
                case ReportDiagnostic.Hidden:
                    return SeverityVocabulary.Silent;
                case ReportDiagnostic.Suppress:
                    return SeverityVocabulary.None;
                default:
                    return "default";
            }
        }
    }
}
