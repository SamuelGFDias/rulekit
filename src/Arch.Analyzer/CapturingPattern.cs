using System;
using System.Collections.Generic;
using Arch.Config;

namespace Arch.Analyzer
{
    /// <summary>
    /// Padrão de namespace/assembly COM placeholders <c>[Nome]</c> (issue #24), casado por busca com
    /// retrocesso sobre os segmentos do símbolo. Padrões sem placeholder continuam no
    /// <see cref="NamespaceTrie"/> (caminho quente sem alocação); só estes, que precisam devolver o
    /// valor capturado, vêm para cá. Semântica dos curingas idêntica à do trie: <c>*</c> = um
    /// segmento, <c>**</c> = zero ou mais, literal = igualdade ordinal; <c>[Nome]</c> = um segmento
    /// que também é capturado.
    /// </summary>
    internal sealed class CapturingPattern
    {
        private readonly PatternSegment[] _segments;
        private readonly int[] _captureIndex; // por segmento: índice em Names, ou -1
        private readonly string[] _names;

        /// <summary>true: casado contra os segmentos do assembly; false: contra os do namespace.</summary>
        internal bool IsAssembly { get; }

        private CapturingPattern(PatternSegment[] segments, int[] captureIndex, string[] names, bool isAssembly)
        {
            IsAssembly = isAssembly;
            _segments = segments;
            _captureIndex = captureIndex;
            _names = names;
        }

        /// <summary>null quando o padrão é inválido ou não tem nenhum placeholder.</summary>
        internal static CapturingPattern TryCreate(string pattern, bool isAssembly)
        {
            IReadOnlyList<PatternSegment> parsed;
            string error;
            if (string.IsNullOrWhiteSpace(pattern) || !SegmentPattern.TryParse(pattern.Trim(), out parsed, out error))
            {
                return null;
            }

            var segments = new PatternSegment[parsed.Count];
            var captureIndex = new int[parsed.Count];
            var names = new List<string>();

            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = parsed[i];
                if (parsed[i].Kind == PatternSegmentKind.Capture)
                {
                    captureIndex[i] = names.Count;
                    names.Add(parsed[i].Text);
                }
                else
                {
                    captureIndex[i] = -1;
                }
            }

            return names.Count == 0 ? null : new CapturingPattern(segments, captureIndex, names.ToArray(), isAssembly);
        }

        internal bool Matches(string[] segments)
        {
            return Match(0, segments, 0, null);
        }

        /// <summary>Casa e devolve nome → valor capturado; null se não casar.</summary>
        internal IReadOnlyDictionary<string, string> Capture(string[] segments)
        {
            var values = new string[_names.Length];
            if (!Match(0, segments, 0, values))
            {
                return null;
            }

            var result = new Dictionary<string, string>(_names.Length, StringComparer.Ordinal);
            for (var i = 0; i < _names.Length; i++)
            {
                result[_names[i]] = values[i];
            }

            return result;
        }

        private bool Match(int patternIndex, string[] segments, int segmentIndex, string[] values)
        {
            if (patternIndex == _segments.Length)
            {
                return segmentIndex == segments.Length;
            }

            var segment = _segments[patternIndex];

            switch (segment.Kind)
            {
                case PatternSegmentKind.AnyDepth:
                    for (var consumed = segmentIndex; consumed <= segments.Length; consumed++)
                    {
                        if (Match(patternIndex + 1, segments, consumed, values))
                        {
                            return true;
                        }
                    }

                    return false;

                case PatternSegmentKind.AnySegment:
                    return segmentIndex < segments.Length
                        && Match(patternIndex + 1, segments, segmentIndex + 1, values);

                case PatternSegmentKind.Capture:
                    if (segmentIndex >= segments.Length)
                    {
                        return false;
                    }

                    if (values != null)
                    {
                        values[_captureIndex[patternIndex]] = segments[segmentIndex];
                    }

                    return Match(patternIndex + 1, segments, segmentIndex + 1, values);

                default:
                    return segmentIndex < segments.Length
                        && string.Equals(segments[segmentIndex], segment.Text, StringComparison.Ordinal)
                        && Match(patternIndex + 1, segments, segmentIndex + 1, values);
            }
        }
    }
}
