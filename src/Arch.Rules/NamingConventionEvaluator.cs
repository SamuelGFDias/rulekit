using System;
using System.Linq;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Arch.Rules
{
    /// <summary>
    /// Avaliador do tipo de regra `naming-convention` (ADR-001 §4 D2, Trilha 2C do PLANO-implementacao.md).
    ///
    /// Exemplo do ADR:
    /// <code>
    /// type: naming-convention
    /// layer: Repository
    /// require: { suffix: Repository }
    /// </code>
    ///
    /// A camada-alvo chega no campo <c>layer</c> (escalar) e a convenção exigida no campo
    /// <c>require</c>, que o catálogo (<see cref="RuleTypeCatalog"/>) declara como par chave/valor
    /// (<c>require: { suffix: Repository }</c>), lido por <see cref="RuleDefinition.TryGetKeyValue"/>
    /// (na forma texto: "suffix=Repository").
    ///
    /// O ADR documenta `suffix`; `prefix` é aceito como SUPORTE ANTECIPADO. O parser rejeita
    /// (ARCH9008) qualquer outra chave; uma regra montada à mão com outra chave cai no fallback de
    /// `suffix` aqui, por defesa.
    /// </summary>
    public sealed class NamingConventionEvaluator : IRuleEvaluator
    {
        /// <summary>Valor de `rules[].type` no YAML tratado por este avaliador.</summary>
        public const string TypeName = RuleTypeNames.NamingConvention;

        public string RuleType => TypeName;

        public void RegisterActions(
            CompilationStartAnalysisContext context,
            RuleDefinition rule,
            ILayerResolver layerResolver,
            DiagnosticDescriptor slotDescriptor)
        {
            // Retornos mudos abaixo são defensivos: o parser já descarta com ARCH9008 a regra sem
            // `layer`/`require` válidos, então só uma RuleDefinition montada à mão chega aqui assim.
            var layerName = rule.GetString(RuleFieldNames.Layer);
            if (string.IsNullOrEmpty(layerName))
            {
                return;
            }

            string kind;
            string value;
            if (!rule.TryGetKeyValue(RuleFieldNames.Require, out kind, out value))
            {
                return;
            }

            // RegisterSymbolAction, nunca RegisterSyntaxNodeAction (ADR-001 §5): a convenção de
            // nomenclatura é uma propriedade do SÍMBOLO do tipo, não de uma árvore sintática
            // específica — e é exatamente isso que faz um `partial class` gerar um único callback
            // (Roslyn funde as declarações parciais num só INamedTypeSymbol antes de disparar a
            // ação de símbolo), em vez de um por arquivo/declaração.
            var properties = ArchDiagnosticProperties.CreateBuilder(rule, TypeName).ToImmutable();

            context.RegisterSymbolAction(
                symbolContext =>
                {
                    var symbol = (INamedTypeSymbol)symbolContext.Symbol;

                    if (!layerResolver.IsInLayer(symbol, layerName))
                    {
                        return;
                    }

                    if (Satisfies(symbol.Name, kind, value))
                    {
                        return;
                    }

                    // A primeira localização (não um loop sobre todas): para um tipo parcial,
                    // Locations traz uma entrada por declaração sintática, mas o símbolo — e,
                    // portanto, a violação de convenção — é um só. Reportar em todas as
                    // localizações duplicaria o diagnóstico por arquivo/parte; a primeira
                    // localização (ordem de declaração no código-fonte) é suficiente e
                    // determinística. FirstOrDefault + Location.None é defesa contra uma lista
                    // vazia (mesmo padrão de MaxDependenciesEvaluator, issue #7).
                    symbolContext.ReportDiagnostic(Diagnostic.Create(
                        slotDescriptor,
                        symbol.Locations.FirstOrDefault() ?? Location.None,
                        properties.SetItem(ArchDiagnosticProperties.Subject, ArchDiagnosticProperties.SubjectForType(symbol)),
                        BuildMessage(rule, symbol.Name, kind, value)));
                },
                SymbolKind.NamedType);
        }

        private static bool Satisfies(string typeName, string kind, string value)
        {
            if (string.Equals(kind, RuleFieldValues.RequirePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return typeName.StartsWith(value, StringComparison.Ordinal);
            }

            // "suffix" é a única convenção documentada no ADR-001 hoje; qualquer chave diferente de
            // "prefix" (incluindo "suffix" propriamente dito) cai aqui.
            return typeName.EndsWith(value, StringComparison.Ordinal);
        }

        internal static string BuildMessage(RuleDefinition rule, string typeName, string kind, string value)
            => rule.Id + ": tipo '" + typeName + "' viola a convenção de nomenclatura (" + kind + "=" + value + ")";
    }
}
