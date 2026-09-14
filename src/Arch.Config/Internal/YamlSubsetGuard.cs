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
    /// </summary>
    internal static class YamlSubsetGuard
    {
        /// <summary>Retorna um <see cref="SchemaValidationError"/> bloqueante (ARCH9001) na
        /// primeira violação do subset encontrada, ou null quando o texto é aceitável.</summary>
        public static SchemaValidationError Validate(string yamlText)
        {
            var parser = new Parser(new StringReader(yamlText ?? string.Empty));
            var documentStartCount = 0;

            try
            {
                while (parser.MoveNext())
                {
                    var evt = parser.Current;

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

            return null;
        }

        private static SchemaValidationError Blocking(string message, Mark mark)
            => new SchemaValidationError("ARCH9001", message, mark.Line, mark.Column, isBlocking: true);
    }
}
