using System;
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
    /// A camada-alvo chega em <c>rule.Extra["layer"]</c> (ex.: "Repository") e a convenção exigida
    /// chega em <c>rule.Extra["require"]</c> já serializada como uma única string no formato
    /// "chave=valor" (ex.: "suffix=Repository") — convenção confirmada em
    /// <c>tests/Arch.Config.Tests/ArchConfigParserTests.cs</c> (linhas 114-115 no momento desta
    /// implementação), onde o parser da trilha 1A converte o mapa `require: { suffix: Repository }`
    /// do YAML nessa string única dentro de <see cref="RuleDefinition.Extra"/>.
    ///
    /// O ADR só documenta `suffix` hoje. Este avaliador também reconhece `prefix` com o mesmo
    /// formato de parsing (ex.: "prefix=I") como SUPORTE ANTECIPADO — não é exigido pelo ADR nesta
    /// fase, mas usa exatamente o mesmo mecanismo de "chave=valor" e não custa nada manter pronto.
    /// Qualquer outra chave em `require` é tratada como `suffix` (comportamento de fallback, não
    /// documentado, mas nunca silenciosamente ignorado — o schema de `Arch.Config` é quem decide se
    /// quer ser estrito com chaves desconhecidas).
    /// </summary>
    public sealed class NamingConventionEvaluator : IRuleEvaluator
    {
        /// <summary>Valor de `rules[].type` no YAML tratado por este avaliador.</summary>
        public const string TypeName = "naming-convention";

        public string RuleType => TypeName;

        public void RegisterActions(
            CompilationStartAnalysisContext context,
            RuleDefinition rule,
            ILayerResolver layerResolver,
            DiagnosticDescriptor slotDescriptor)
        {
            string layerName;
            if (!rule.Extra.TryGetValue("layer", out layerName) || string.IsNullOrEmpty(layerName))
            {
                return;
            }

            string requireRaw;
            if (!rule.Extra.TryGetValue("require", out requireRaw) || string.IsNullOrEmpty(requireRaw))
            {
                return;
            }

            string kind;
            string value;
            if (!TryParseRequire(requireRaw, out kind, out value))
            {
                return;
            }

            // RegisterSymbolAction, nunca RegisterSyntaxNodeAction (ADR-001 §5): a convenção de
            // nomenclatura é uma propriedade do SÍMBOLO do tipo, não de uma árvore sintática
            // específica — e é exatamente isso que faz um `partial class` gerar um único callback
            // (Roslyn funde as declarações parciais num só INamedTypeSymbol antes de disparar a
            // ação de símbolo), em vez de um por arquivo/declaração.
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

                    // symbol.Locations[0] (não um loop sobre todas): para um tipo parcial,
                    // Locations traz uma entrada por declaração sintática, mas o símbolo — e,
                    // portanto, a violação de convenção — é um só. Reportar em todas as
                    // localizações duplicaria o diagnóstico por arquivo/parte; a primeira
                    // localização (ordem de declaração no código-fonte) é suficiente e
                    // determinística.
                    symbolContext.ReportDiagnostic(Diagnostic.Create(
                        slotDescriptor,
                        symbol.Locations[0],
                        BuildMessage(rule, symbol.Name, kind, value)));
                },
                SymbolKind.NamedType);
        }

        /// <summary>
        /// Extrai o par chave/valor de uma entrada `require` já serializada como "chave=valor"
        /// (ex.: "suffix=Repository"). Devolve false para qualquer formato inesperado, em vez de
        /// lançar — uma regra mal configurada não deve derrubar o analyzer.
        /// </summary>
        private static bool TryParseRequire(string raw, out string kind, out string value)
        {
            var separatorIndex = raw.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == raw.Length - 1)
            {
                kind = null;
                value = null;
                return false;
            }

            kind = raw.Substring(0, separatorIndex).Trim();
            value = raw.Substring(separatorIndex + 1).Trim();
            return !string.IsNullOrEmpty(kind) && !string.IsNullOrEmpty(value);
        }

        private static bool Satisfies(string typeName, string kind, string value)
        {
            if (string.Equals(kind, "prefix", StringComparison.OrdinalIgnoreCase))
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
