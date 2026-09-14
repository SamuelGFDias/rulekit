using System;
using System.Collections.Generic;

namespace Arch.Config.Internal
{
    /// <summary>
    /// Aplica a semântica de composição de `extends` (ADR-001 D2): merge por chave, onde o
    /// arquivo que faz `extends` vence; `layers` e `rules` fazem merge por nome/`id` (o item do
    /// documento de maior precedência substitui o de menor, na mesma posição de declaração — uma
    /// regra desligada com `enabled: false` continua na lista, só marcada). Recebe os documentos
    /// já parseados individualmente (<see cref="ArchConfigDocumentParser"/>), em ordem crescente
    /// de precedência (o último elemento é o que "faz extends" dos anteriores e vence qualquer
    /// conflito).
    /// </summary>
    internal static class ArchConfigMerger
    {
        public static ParsedDocument Merge(IReadOnlyList<ParsedDocument> documentsInPrecedenceOrder)
        {
            if (documentsInPrecedenceOrder == null || documentsInPrecedenceOrder.Count == 0)
            {
                throw new ArgumentException("Ao menos um documento é necessário.", nameof(documentsInPrecedenceOrder));
            }

            if (documentsInPrecedenceOrder.Count == 1)
            {
                return documentsInPrecedenceOrder[0];
            }

            var merged = new ParsedDocument();
            var ruleIndexById = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var doc in documentsInPrecedenceOrder)
            {
                // Documentos com erro bloqueante não têm conteúdo confiável para mesclar — só as
                // mensagens de erro importam nesse caso (o chamador nunca expõe Config quando há
                // erro bloqueante em qualquer camada).
                foreach (var error in doc.Errors)
                {
                    merged.Errors.Add(error);
                }

                if (doc.HasBlockingError)
                {
                    continue;
                }

                merged.Schema = doc.Schema;

                foreach (var layerKv in doc.Layers)
                {
                    merged.Layers[layerKv.Key] = layerKv.Value;
                }

                foreach (var severityKv in doc.Severities)
                {
                    merged.Severities[severityKv.Key] = severityKv.Value;
                }

                if (doc.DefaultSeverityName != null)
                {
                    merged.DefaultSeverityName = doc.DefaultSeverityName;
                }

                foreach (var rule in doc.Rules)
                {
                    if (ruleIndexById.TryGetValue(rule.Id, out var existingIndex))
                    {
                        merged.Rules[existingIndex] = rule;
                    }
                    else
                    {
                        ruleIndexById[rule.Id] = merged.Rules.Count;
                        merged.Rules.Add(rule);
                    }
                }
            }

            return merged;
        }
    }
}
