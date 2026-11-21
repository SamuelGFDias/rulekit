using System;
using System.Collections.Generic;

namespace Arch.Config
{
    /// <summary>Tipo de um segmento de um padrão de <c>namespace</c>/<c>assembly</c>.</summary>
    public enum PatternSegmentKind
    {
        /// <summary>Texto literal, comparado de forma ordinal.</summary>
        Literal,

        /// <summary><c>*</c>: exatamente um segmento.</summary>
        AnySegment,

        /// <summary><c>**</c>: zero ou mais segmentos.</summary>
        AnyDepth,

        /// <summary><c>[Nome]</c>: exatamente um segmento, cujo valor é capturado sob <c>Nome</c>.</summary>
        Capture,
    }

    /// <summary>Um segmento de um padrão já decomposto por <see cref="SegmentPattern"/>.</summary>
    public sealed class PatternSegment
    {
        public PatternSegmentKind Kind { get; }

        /// <summary>Texto do literal (<see cref="PatternSegmentKind.Literal"/>) ou nome da captura
        /// (<see cref="PatternSegmentKind.Capture"/>); null nos curingas.</summary>
        public string Text { get; }

        internal PatternSegment(PatternSegmentKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }
    }

    /// <summary>
    /// Sintaxe única dos padrões de <c>namespace</c> e <c>assembly</c> de critérios de camada
    /// (issues #23/#24): segmentos separados por ponto, onde cada segmento é um literal, <c>*</c>
    /// (exatamente um segmento), <c>**</c> (zero ou mais) ou <c>[Nome]</c> (exatamente um segmento,
    /// que é capturado). <c>Nome</c> é <c>[A-Za-z][A-Za-z0-9]*</c> e não pode se repetir no mesmo
    /// padrão; um segmento com colchete que não seja exatamente <c>[Nome]</c> é malformado.
    /// Segmentos vazios (<c>Acme..Api</c>, ponto sobrando) são ignorados, como no trie do analyzer.
    ///
    /// <para>Parser e analyzer usam esta mesma decomposição, para darem o mesmo veredito sobre o que
    /// é um padrão válido.</para>
    /// </summary>
    public static class SegmentPattern
    {
        private static readonly IReadOnlyList<string> NoNames = Array.Empty<string>();

        /// <summary>
        /// Decompõe <paramref name="pattern"/>. Devolve false com a descrição do problema (sem o
        /// nome do campo) quando há placeholder malformado ou repetido.
        /// </summary>
        public static bool TryParse(string pattern, out IReadOnlyList<PatternSegment> segments, out string error)
        {
            var list = new List<PatternSegment>();
            segments = list;
            error = null;

            if (string.IsNullOrWhiteSpace(pattern))
            {
                return true;
            }

            HashSet<string> seen = null;

            foreach (var raw in pattern.Split('.'))
            {
                var segment = raw.Trim();
                if (segment.Length == 0)
                {
                    continue;
                }

                if (segment == "**")
                {
                    list.Add(new PatternSegment(PatternSegmentKind.AnyDepth, null));
                }
                else if (segment == "*")
                {
                    list.Add(new PatternSegment(PatternSegmentKind.AnySegment, null));
                }
                else if (segment.IndexOf('[') >= 0 || segment.IndexOf(']') >= 0)
                {
                    if (segment.Length < 3
                        || segment[0] != '['
                        || segment[segment.Length - 1] != ']'
                        || !IsValidCaptureName(segment.Substring(1, segment.Length - 2)))
                    {
                        segments = Array.Empty<PatternSegment>();
                        error = "placeholder malformado '" + segment
                            + "' no padrão '" + pattern.Trim() + "' (esperado '[Nome]' com Nome = letra seguida de letras/dígitos, ocupando o segmento inteiro)";
                        return false;
                    }

                    var name = segment.Substring(1, segment.Length - 2);
                    if (seen == null)
                    {
                        seen = new HashSet<string>(StringComparer.Ordinal);
                    }

                    if (!seen.Add(name))
                    {
                        segments = Array.Empty<PatternSegment>();
                        error = "placeholder '[" + name + "]' repetido no padrão '" + pattern.Trim() + "' (cada nome aparece uma vez por padrão)";
                        return false;
                    }

                    list.Add(new PatternSegment(PatternSegmentKind.Capture, name));
                }
                else
                {
                    list.Add(new PatternSegment(PatternSegmentKind.Literal, segment));
                }
            }

            return true;
        }

        /// <summary>Nomes capturados por <paramref name="pattern"/>, na ordem; vazio se não há ou se o padrão é inválido.</summary>
        public static IReadOnlyList<string> GetCaptureNames(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern.IndexOf('[') < 0)
            {
                return NoNames;
            }

            IReadOnlyList<PatternSegment> segments;
            string error;
            if (!TryParse(pattern, out segments, out error))
            {
                return NoNames;
            }

            List<string> names = null;
            foreach (var segment in segments)
            {
                if (segment.Kind == PatternSegmentKind.Capture)
                {
                    (names ?? (names = new List<string>())).Add(segment.Text);
                }
            }

            return names ?? NoNames;
        }

        /// <summary><c>[A-Za-z][A-Za-z0-9]*</c> (ASCII; nunca <c>char.IsLetter</c>).</summary>
        public static bool IsValidCaptureName(string name)
        {
            if (string.IsNullOrEmpty(name) || !IsAsciiLetter(name[0]))
            {
                return false;
            }

            for (var i = 1; i < name.Length; i++)
            {
                if (!IsAsciiLetter(name[i]) && !(name[i] >= '0' && name[i] <= '9'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    }
}
