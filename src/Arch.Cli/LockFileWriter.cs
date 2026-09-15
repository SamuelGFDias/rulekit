using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Arch.Config;

namespace Arch.Cli
{
    /// <summary>
    /// Uma linha do lock file: retrato de UMA regra (habilitada ou não — <see cref="Enabled"/>
    /// reflete o estado real) após a resolução de `extends` e severidade.
    /// </summary>
    public sealed class LockEntry
    {
        public string Id { get; }
        public string Slot { get; }
        public string Type { get; }

        /// <summary>Severidade efetiva já mapeada ao vocabulário do AnalyzerConfig, ou
        /// <c>null</c> quando não foi possível resolver (o comando `gen` não chega a gerar o lock
        /// file nesse caso — ver <see cref="ConfigPipeline"/> — mas o tipo aceita null para não
        /// impor essa regra de negócio ao writer).</summary>
        public string Severity { get; }

        public bool Enabled { get; }

        public LockEntry(string id, string slot, string type, string severity, bool enabled)
        {
            Id = id;
            Slot = slot;
            Type = type;
            Severity = severity;
            Enabled = enabled;
        }
    }

    /// <summary>
    /// Gera o "lock file" `arch-rules.lock.yaml` (prompt da trilha 3A / ADR-001 Action Item #5):
    /// um retrato determinístico e ORDENADO POR SLOT CRESCENTE de `id -> slot -> severidade
    /// efetiva` para TODAS as regras (habilitadas ou não, com `enabled` refletindo o estado) —
    /// útil para diff legível em PR e, junto com <see cref="ValidateCommand"/>, para detectar
    /// divergência entre o YAML e o `.globalconfig` gerado anteriormente (cenário do ARCH9005,
    /// ADR-001 D3 "Consequência a aceitar").
    /// </summary>
    public static class LockFileWriter
    {
        public static IReadOnlyList<LockEntry> BuildEntries(ArchConfig config, IReadOnlyDictionary<string, string> resolvedSeverityByRuleId)
        {
            return config.Rules
                .Select(r => new LockEntry(
                    r.Id,
                    r.Slot,
                    r.Type,
                    resolvedSeverityByRuleId.TryGetValue(r.Id, out var severity) ? severity : null,
                    r.Enabled))
                .OrderBy(e => e.Slot, StringComparer.Ordinal)
                .ToList();
        }

        public static string BuildContent(IReadOnlyList<LockEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("schema: arch-rules-lock/v1\n");
            sb.Append("rules:\n");

            foreach (var entry in entries)
            {
                sb.Append("  - id: ").Append(entry.Id).Append('\n');
                sb.Append("    slot: ").Append(entry.Slot).Append('\n');
                sb.Append("    type: ").Append(entry.Type).Append('\n');
                sb.Append("    severity: ").Append(entry.Severity ?? "null").Append('\n');
                sb.Append("    enabled: ").Append(entry.Enabled ? "true" : "false").Append('\n');
            }

            return sb.ToString();
        }
    }
}
