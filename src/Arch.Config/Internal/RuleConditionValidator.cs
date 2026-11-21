using System;
using System.Collections.Generic;
using System.Linq;

namespace Arch.Config.Internal
{
    /// <summary>
    /// Valida o campo <c>when</c> das regras contra as capturas das camadas (issue #24). Roda sobre o
    /// documento JÁ MESCLADO (<see cref="ArchConfigMerger"/>), porque com <c>extends</c> a camada
    /// <c>from</c>/<c>to</c> pode vir de outro arquivo da cadeia que a regra. Um nome em <c>when</c> que
    /// nenhum critério de <c>match</c> das camadas <c>from</c> e <c>to</c> captura (nem uma camada que
    /// não existe) descarta a regra com <c>ARCH9008</c> não bloqueante, ancorada na linha/coluna e no
    /// documento da regra.
    /// </summary>
    internal static class RuleConditionValidator
    {
        public static void Apply(ParsedDocument merged)
        {
            for (var i = merged.Rules.Count - 1; i >= 0; i--)
            {
                var rule = merged.Rules[i];
                if (!rule.TryGetKeyList(RuleFieldNames.When, out _, out var names))
                {
                    continue;
                }

                var from = rule.GetString(RuleFieldNames.From);
                var to = rule.GetString(RuleFieldNames.To);
                var captured = new HashSet<string>(StringComparer.Ordinal);
                AddCaptures(merged, from, captured);
                AddCaptures(merged, to, captured);

                var missing = names.Where(n => !captured.Contains(n)).Distinct(StringComparer.Ordinal).ToList();
                if (missing.Count == 0)
                {
                    continue;
                }

                merged.Rules.RemoveAt(i);
                merged.Errors.Add(new SchemaValidationError(
                    ArchConfigDocumentParser.InvalidRuleFieldsCode,
                    $"Regra '{rule.Id}' (type '{rule.Type}') ignorada: 'when' cita "
                        + string.Join(", ", missing.Select(n => $"'{n}'"))
                        + $" que não é capturado por nenhum critério de 'match' das camadas '{from}' e '{to}' (placeholder [Nome] em namespace/assembly).",
                    rule.SourceLine,
                    rule.SourceColumn,
                    isBlocking: false)
                {
                    SourceDocumentIndex = rule.SourceDocumentIndex,
                });
            }
        }

        private static void AddCaptures(ParsedDocument merged, string layerName, HashSet<string> target)
        {
            if (layerName != null && merged.Layers.TryGetValue(layerName.Trim(), out var layer))
            {
                foreach (var name in layer.CaptureNames)
                {
                    target.Add(name);
                }
            }
        }
    }
}
