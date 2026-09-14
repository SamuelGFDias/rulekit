using System;
using System.Collections.Generic;
using System.Linq;

namespace Arch.Config.Internal
{
    /// <summary>
    /// Resultado intermediário de parsear um único texto YAML (uma "camada" de `extends`),
    /// antes do merge entre documentos (ADR-001 D2, semântica de `extends`). Nunca exposto
    /// fora de Arch.Config — o contrato público congelado é <see cref="ArchConfigParseResult"/>.
    /// </summary>
    internal sealed class ParsedDocument
    {
        public string Schema { get; set; }

        public IReadOnlyList<string> Extends { get; set; } = Array.Empty<string>();

        public Dictionary<string, LayerDefinition> Layers { get; } = new Dictionary<string, LayerDefinition>(StringComparer.Ordinal);

        /// <summary>Ordem de declaração preservada — importa para o merge de `extends` (regra
        /// nova entra no fim, regra existente é substituída na própria posição).</summary>
        public List<RuleDefinition> Rules { get; } = new List<RuleDefinition>();

        public Dictionary<string, string> Severities { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public string DefaultSeverityName { get; set; }

        public List<SchemaValidationError> Errors { get; } = new List<SchemaValidationError>();

        public bool HasBlockingError => Errors.Any(e => e.IsBlocking);
    }
}
