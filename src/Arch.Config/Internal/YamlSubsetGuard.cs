using System;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Arch.Config.Internal
{
    /// <summary>
    /// Valida o subset restrito de YAML aceito pela lib (ADR-001 D1): sem anchors (&amp;) nem
    /// aliases (*), sem multi-documento ("---" repetido) e sem tags customizadas. Roda em cima
    /// dos eventos de baixo nível de <see cref="Parser"/> (não da DOM de <c>YamlStream</c>)
    /// porque só ali dá para distinguir uma tag explícita do usuário (evento carrega a tag
    /// literal do texto) de uma tag resolvida implicitamente por padrão pelo YamlDotNet ao
    /// materializar o DOM — a DOM sempre preenche <c>YamlNode.Tag</c>, então checar lá dentro
    /// acusaria falso positivo em todo escalar comum.
    ///
    /// <para>Também impõe LIMITES de entrada, ANTES de qualquer <c>YamlStream.Load</c> (revisão B2): texto
    /// acima de <see cref="MaxTextLength"/> caracteres e aninhamento de mapping/sequence acima de
    /// <see cref="MaxNestingDepth"/>. Sem eles, `[` repetido 100 mil vezes fazia o carregador da DOM
    /// recursivo estourar a pilha (StackOverflow não é capturável: derruba o processo do compilador
    /// ou do CLI). Como o <see cref="Parser"/> é orientado a eventos e iterativo, o guard para no
    /// primeiro evento que estoura o limite, sem ler o resto.</para>
    /// </summary>
    internal static class YamlSubsetGuard
    {
        /// <summary>Tamanho máximo do texto de um arquivo YAML: 1 MiB (1_048_576 caracteres).</summary>
        public const int MaxTextLength = 1_048_576;

        /// <summary>Aninhamento máximo de mapping/sequence (o mapping raiz conta como nível 1).</summary>
        public const int MaxNestingDepth = 64;

        /// <summary>Retorna um <see cref="SchemaValidationError"/> bloqueante (ARCH9001) na
        /// primeira violação do subset ou dos limites encontrada, ou null quando o texto é aceitável.</summary>
        public static SchemaValidationError Validate(string yamlText)
            => Validate(yamlText, limitsOnly: false);

        /// <summary>
        /// Só os limites de tamanho e profundidade (e a conversão de falha do scanner em ARCH9001), sem as
        /// regras do subset — é o que <see cref="ArchConfigDocumentParser.PeekExtends"/> precisa para não
        /// derrubar o processo, preservando a decisão de ele não validar mais nada.
        /// </summary>
        public static SchemaValidationError ValidateLimits(string yamlText)
            => Validate(yamlText, limitsOnly: true);

        private static SchemaValidationError Validate(string yamlText, bool limitsOnly)
        {
            var text = yamlText ?? string.Empty;

            if (text.Length > MaxTextLength)
            {
                var (line, column) = PositionOf(text, MaxTextLength);
                return new SchemaValidationError(
                    "ARCH9001",
                    $"Arquivo YAML acima do tamanho máximo de 1 MiB ({MaxTextLength} caracteres): " +
                    $"o excedente começa aqui; o arquivo tem {text.Length} caracteres.",
                    line,
                    column,
                    isBlocking: true);
            }

            var parser = new Parser(new StringReader(text));
            var documentStartCount = 0;
            var depth = 0;

            try
            {
                while (parser.MoveNext())
                {
                    var evt = parser.Current;

                    if (evt is MappingStart || evt is SequenceStart)
                    {
                        depth++;
                        if (depth > MaxNestingDepth)
                        {
                            return Blocking(
                                $"Profundidade de aninhamento YAML acima do limite de {MaxNestingDepth} níveis (mapping/sequence).",
                                evt.Start);
                        }
                    }
                    else if (evt is MappingEnd || evt is SequenceEnd)
                    {
                        depth--;
                    }

                    if (limitsOnly)
                    {
                        continue;
                    }

                    if (evt is DocumentStart)
                    {
                        documentStartCount++;
                        if (documentStartCount > 1)
                        {
                            return Blocking(
                                "YAML multi-documento (\"---\" repetido) não é suportado neste subset (ADR-001 D1).",
                                evt.Start);
                        }
                    }
                    else if (evt is AnchorAlias)
                    {
                        return Blocking(
                            "Alias YAML (*nome) não é suportado neste subset (ADR-001 D1).",
                            evt.Start);
                    }
                    else if (evt is NodeEvent nodeEvent)
                    {
                        if (!nodeEvent.Anchor.IsEmpty)
                        {
                            return Blocking(
                                "Anchor YAML (&nome) não é suportado neste subset (ADR-001 D1).",
                                nodeEvent.Start);
                        }

                        if (!nodeEvent.Tag.IsEmpty)
                        {
                            return Blocking(
                                $"Tag YAML customizada ('{nodeEvent.Tag.Value}') não é suportada neste subset (ADR-001 D1).",
                                nodeEvent.Start);
                        }
                    }
                }
            }
            catch (YamlException ex)
            {
                return Blocking("YAML sintaticamente inválido: " + ex.Message, ex.Start);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // O scanner/parser do YamlDotNet também lança tipos que não são YamlException em entrada
                // malformada (ex.: InvalidOperationException para `...` seguido de conteúdo). Mesma
                // classificação de um erro de sintaxe, com a melhor posição disponível: a do último
                // evento lido (ou 1:1 quando nenhum chegou a ser produzido).
                var last = parser.Current;
                return Blocking(
                    "YAML sintaticamente inválido (" + ex.GetType().Name + "): " + ex.Message,
                    last != null ? last.End : Mark.Empty);
            }

            return null;
        }

        /// <summary>Linha/coluna (1-based) do caractere de índice <paramref name="index"/> em <paramref name="text"/>.</summary>
        private static (int Line, int Column) PositionOf(string text, int index)
        {
            var line = 1;
            var column = 1;

            for (var i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return (line, column);
        }

        private static SchemaValidationError Blocking(string message, Mark mark)
            => new SchemaValidationError(
                "ARCH9001", message, System.Math.Max(mark.Line, 1), System.Math.Max(mark.Column, 1), isBlocking: true);
    }
}
