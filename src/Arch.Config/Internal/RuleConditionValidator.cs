using System;
using System.Collections.Generic;
using System.Linq;

namespace Arch.Config.Internal
{
    /// <summary>
    /// Valida o campo <c>when</c> das regras contra as capturas das camadas (issue #24). Roda sobre o
    /// documento JÁ MESCLADO (<see cref="ArchConfigMerger"/>), porque com <c>extends</c> a camada
    /// <c>from</c>/<c>to</c> pode vir de outro arquivo da cadeia que a regra. Cada nome em <c>when</c>
    /// precisa ser capturado por ao menos um critério de <c>match</c> da camada <c>from</c> <b>e</b> por
    /// ao menos um da camada <c>to</c> (uma camada inexistente não captura nada); faltando em qualquer
    /// lado a regra é descartada com <c>ARCH9008</c> não bloqueante, ancorada na linha/coluna e no
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
                var fromCaptures = GetCaptures(merged, from);
                var toCaptures = GetCaptures(merged, to);

                var problems = names
                    .Distinct(StringComparer.Ordinal)
                    .Select(n => (Name: n, Lacking: new[] { (Layer: from, Has: fromCaptures.Contains(n)), (Layer: to, Has: toCaptures.Contains(n)) }
                        .Where(side => !side.Has)
                        .Select(side => side.Layer)
                        .Distinct(StringComparer.Ordinal)
                        .ToList()))
                    .Where(p => p.Lacking.Count > 0)
                    .ToList();
                if (problems.Count == 0)
                {
                    continue;
                }

                merged.Rules.RemoveAt(i);
                merged.Errors.Add(new SchemaValidationError(
                    ArchConfigDocumentParser.InvalidRuleFieldsCode,
                    $"Regra '{rule.Id}' (type '{rule.Type}') ignorada: 'when' exige que cada nome seja capturado por um critério de 'match' das camadas '{from}' e '{to}' (placeholder [Nome] em namespace/assembly), mas "
                        + string.Join("; ", problems.Select(p => $"'{p.Name}' não é capturado por " + string.Join(" nem por ", p.Lacking.Select(l => $"'{l}'")))) + ".",
                    rule.SourceLine,
                    rule.SourceColumn,
                    isBlocking: false)
                {
                    SourceDocumentIndex = rule.SourceDocumentIndex,
                });
            }
        }

        private static HashSet<string> GetCaptures(ParsedDocument merged, string layerName)
        {
            var captured = new HashSet<string>(StringComparer.Ordinal);
            if (layerName != null && merged.Layers.TryGetValue(layerName.Trim(), out var layer))
            {
                foreach (var name in layer.CaptureNames)
                {
                    captured.Add(name);
                }
            }

            return captured;
        }
    }
}
