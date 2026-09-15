using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Arch.Config;

namespace Arch.Cli
{
    /// <summary>
    /// Materializa a severidade efetiva das regras HABILITADAS num `.globalconfig` (ADR-001 D3):
    /// "a severidade efetiva não vem do YAML em runtime: vem de `.editorconfig`/`.globalconfig`
    /// sobre o slot". Uma linha `dotnet_diagnostic.&lt;slot&gt;.severity = &lt;valor&gt;` por
    /// regra com `enabled != false`, ordenadas por slot crescente para saída determinística
    /// (diff legível em PR, mesmo espírito do lock file — ver LockFileWriter).
    /// </summary>
    public static class GlobalConfigWriter
    {
        public static string BuildContent(ArchConfig config, IReadOnlyDictionary<string, string> resolvedSeverityByRuleId)
        {
            var sb = new StringBuilder();
            sb.Append("is_global = true\n");
            sb.Append('\n');

            var enabledRulesOrderedBySlot = config.Rules
                .Where(r => r.Enabled && resolvedSeverityByRuleId.ContainsKey(r.Id))
                .OrderBy(r => r.Slot, StringComparer.Ordinal);

            foreach (var rule in enabledRulesOrderedBySlot)
            {
                sb.Append("dotnet_diagnostic.").Append(rule.Slot).Append(".severity = ")
                    .Append(resolvedSeverityByRuleId[rule.Id]).Append('\n');
            }

            return sb.ToString();
        }
    }
}
