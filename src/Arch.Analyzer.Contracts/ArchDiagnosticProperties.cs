using System;
using System.Collections.Immutable;
using Arch.Config;
using Microsoft.CodeAnalysis;

namespace Arch.Analyzer.Contracts
{
    /// <summary>
    /// Chaves de <c>Diagnostic.Properties</c> dos diagnósticos de slot (<c>ARCH0001..ARCH0512</c>) e a única
    /// definição do <b>assunto</b> (<see cref="Subject"/>, issue #25): a string estável, sem linha/coluna, que
    /// identifica uma violação dentro do arquivo e permite que o baseline (<c>arch-rules.baseline.yaml</c>) a
    /// case entre builds. Properties é o canal legível por máquina (Code Fix, relatórios, SARIF, baseline); a
    /// mensagem é o canal humano.
    /// </summary>
    public static class ArchDiagnosticProperties
    {
        /// <summary>ID de negócio da regra (<c>rules[].id</c>).</summary>
        public const string RuleId = "archRuleId";

        public const string RuleType = "archRuleType";

        public const string From = "archFrom";

        public const string To = "archTo";

        public const string Help = "archHelp";

        /// <summary>Assunto estável da violação — ver <see cref="SubjectForReference"/> e <see cref="SubjectForType"/>.</summary>
        public const string Subject = "archSubject";

        /// <summary>Separador entre tipo de origem e alvo no assunto de <c>forbidden-call</c>/<c>must-route-through</c>.</summary>
        public const string SubjectArrow = " -> ";

        /// <summary>
        /// Formato de exibição de tipos no assunto: nome qualificado por namespace e tipos contêineres, com
        /// parâmetros de tipo genéricos (<c>Ns.Outer.Cache&lt;T&gt;</c>), sem <c>global::</c> e sem
        /// anotações de nulabilidade — não depende de idioma nem de <c>using</c>.
        /// </summary>
        private static readonly SymbolDisplayFormat TypeFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

        /// <summary>
        /// Assunto de <c>naming-convention</c>/<c>max-dependencies</c>: o nome qualificado do tipo
        /// (definição original, então <c>Cache&lt;int&gt;</c> e <c>Cache&lt;T&gt;</c> coincidem).
        /// </summary>
        public static string SubjectForType(INamedTypeSymbol type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            return type.OriginalDefinition.ToDisplayString(TypeFormat);
        }

        /// <summary>
        /// Assunto de <c>forbidden-call</c>/<c>must-route-through</c>:
        /// <c>&lt;tipo de origem&gt; -&gt; &lt;tipo alvo&gt;.&lt;membro&gt;</c>, ou só o tipo alvo quando não há
        /// membro (criação de objeto).
        /// </summary>
        public static string SubjectForReference(INamedTypeSymbol sourceType, INamedTypeSymbol targetType, string memberName)
        {
            var target = SubjectForType(targetType);
            return SubjectForType(sourceType)
                + SubjectArrow
                + (string.IsNullOrEmpty(memberName) ? target : target + "." + memberName);
        }

        /// <summary>
        /// Properties comuns de um diagnóstico de slot de <paramref name="rule"/>; o avaliador acrescenta o
        /// que lhe é próprio (<c>archFrom</c>/<c>archTo</c>, <c>archSubject</c>).
        /// </summary>
        public static ImmutableDictionary<string, string>.Builder CreateBuilder(RuleDefinition rule, string defaultRuleType)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            builder[RuleId] = rule.Id;
            builder[RuleType] = string.IsNullOrEmpty(rule.Type) ? defaultRuleType : rule.Type;

            if (!string.IsNullOrEmpty(rule.Help))
            {
                builder[Help] = rule.Help;
            }

            return builder;
        }
    }
}
