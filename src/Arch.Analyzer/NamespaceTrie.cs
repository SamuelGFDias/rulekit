using System;
using System.Collections.Generic;

namespace Arch.Analyzer
{
    /// <summary>
    /// Trie de prefixos sobre SEGMENTOS de namespace, construída uma única vez por lista de
    /// critérios no compilation-start e consultada por símbolo (ADR-001 §5: "globs de namespace
    /// compilados para um trie de prefixos, não regex avaliada por símbolo").
    ///
    /// Por que não regex: o analyzer roda a cada tecla. Um <c>Regex</c> por glob avaliado por
    /// símbolo custa backtracking e alocação de <c>Match</c>; o trie compartilha os prefixos comuns
    /// entre globs da mesma lista (`Acme.Api.**`, `Acme.Api.Controllers.**`, `Acme.Domain.**`
    /// descem pelo mesmo nó `Acme`) e a consulta é comparação ordinal de string por segmento, sem
    /// alocar nada no caminho quente.
    ///
    /// Sintaxe suportada (ADR-001 D2):
    /// <list type="bullet">
    ///   <item><description>segmento literal — casa exatamente aquele segmento (ordinal, case-sensitive como o C#);</description></item>
    ///   <item><description><c>*</c> — casa exatamente UM segmento (`Acme.*.Tests` casa `Acme.Web.Tests`, não `Acme.Web.Api.Tests`);</description></item>
    ///   <item><description><c>**</c> — casa ZERO ou mais segmentos (`Acme.Api.**` casa `Acme.Api` e `Acme.Api.Controllers.V2`).</description></item>
    /// </list>
    /// Um glob sem curinga casa exatamente aquele namespace, nunca os descendentes — `Acme.Api`
    /// não inclui `Acme.Api.Controllers`. Isso é deliberado: prefixo implícito seria impossível de
    /// restringir depois.
    /// </summary>
    internal sealed class NamespaceTrie
    {
        private readonly TrieNode _root = new TrieNode();

        /// <summary>true quando nenhum glob foi adicionado — permite pular a consulta por completo.</summary>
        internal bool IsEmpty { get; private set; } = true;

        internal void Add(string glob)
        {
            if (string.IsNullOrWhiteSpace(glob))
            {
                return;
            }

            var node = _root;

            foreach (var rawSegment in glob.Split('.'))
            {
                var segment = rawSegment.Trim();
                if (segment.Length == 0)
                {
                    // Tolerante a `Acme..Api` / ponto sobrando no fim: segmento vazio não
                    // acrescenta nível. A validação estrita do glob é do schema (Arch.Config).
                    continue;
                }

                node = node.GetOrAddChild(segment);
            }

            node.IsTerminal = true;
            IsEmpty = false;
        }

        /// <summary>
        /// true se o namespace representado por <paramref name="segments"/> (já quebrado em
        /// segmentos, sem alocação de string nova por consulta) casa com algum glob da trie.
        /// </summary>
        internal bool Matches(string[] segments)
        {
            if (IsEmpty)
            {
                return false;
            }

            return Matches(_root, segments ?? Array.Empty<string>(), 0);
        }

        private static bool Matches(TrieNode node, string[] segments, int index)
        {
            if (node.IsTerminal && index == segments.Length)
            {
                return true;
            }

            if (node.AnyDepth != null)
            {
                // `**` consome zero ou mais segmentos: tenta todos os cortes possíveis. O custo é
                // O(segmentos) só nos globs que usam `**` no meio; com `**` no fim (o caso normal)
                // a primeira tentativa já resolve porque o nó seguinte é terminal.
                for (var consumed = index; consumed <= segments.Length; consumed++)
                {
                    if (Matches(node.AnyDepth, segments, consumed))
                    {
                        return true;
                    }
                }
            }

            if (index == segments.Length)
            {
                return false;
            }

            if (node.Literals != null
                && node.Literals.TryGetValue(segments[index], out var literalChild)
                && Matches(literalChild, segments, index + 1))
            {
                return true;
            }

            return node.AnySegment != null && Matches(node.AnySegment, segments, index + 1);
        }

        private sealed class TrieNode
        {
            /// <summary>Filhos por segmento literal. Ordinal: namespace em C# é case-sensitive.</summary>
            internal Dictionary<string, TrieNode> Literals;

            /// <summary>Filho do curinga <c>*</c> (exatamente um segmento).</summary>
            internal TrieNode AnySegment;

            /// <summary>Filho do curinga <c>**</c> (zero ou mais segmentos).</summary>
            internal TrieNode AnyDepth;

            /// <summary>true quando algum glob termina exatamente neste nó.</summary>
            internal bool IsTerminal;

            internal TrieNode GetOrAddChild(string segment)
            {
                if (segment == "**")
                {
                    return AnyDepth ?? (AnyDepth = new TrieNode());
                }

                if (segment == "*")
                {
                    return AnySegment ?? (AnySegment = new TrieNode());
                }

                if (Literals == null)
                {
                    Literals = new Dictionary<string, TrieNode>(StringComparer.Ordinal);
                }

                if (!Literals.TryGetValue(segment, out var child))
                {
                    child = new TrieNode();
                    Literals.Add(segment, child);
                }

                return child;
            }
        }
    }
}
