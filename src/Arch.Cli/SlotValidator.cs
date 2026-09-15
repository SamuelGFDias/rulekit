using System.Collections.Generic;
using System.Text.RegularExpressions;
using Arch.Config;

namespace Arch.Cli
{
    /// <summary>
    /// Valida os slots declarados em `rules[].slot` (AGENTS.md seção 2 / ADR-001 D2 "pool de
    /// slots"): v1 exige que o consumidor declare `slot:` manualmente em cada regra (ADR-001
    /// Action Item #5, Opção C) — este validador não aloca nada, só confere que cada slot já
    /// declarado (a) casa com o formato `ARCH` + 4 dígitos, (b) está dentro do pool reservado ao
    /// consumidor (`ARCH0001`..`ARCH0512` — a faixa `ARCH9xxx` é reservada ao motor e nunca
    /// alocável por configuração, ADR-001 D4) e (c) é único entre todas as regras.
    ///
    /// Arch.Config.Internal.ArchConfigDocumentParser não faz essa checagem (aceita `slot` como
    /// string livre) — em runtime, quem detectaria slot fora do pool/duplicado é Arch.Analyzer,
    /// com o código ARCH9002. Este validador reusa o mesmo código conceitual como string literal:
    /// o CLI não referencia Arch.Analyzer (que traria Roslyn como dependência sem necessidade
    /// aqui, só para reportar um código de erro).
    /// </summary>
    public static class SlotValidator
    {
        public const string SlotErrorCode = "ARCH9002";

        private static readonly Regex SlotPattern = new Regex(@"^ARCH(\d{4})$");

        public static IReadOnlyList<CliDiagnostic> Validate(IReadOnlyList<RuleDefinition> rules)
        {
            var errors = new List<CliDiagnostic>();
            var firstIdBySlot = new Dictionary<string, string>();

            foreach (var rule in rules)
            {
                var slot = rule.Slot;
                var match = SlotPattern.Match(slot ?? string.Empty);

                if (!match.Success)
                {
                    errors.Add(new CliDiagnostic(
                        SlotErrorCode,
                        $"Regra '{rule.Id}': slot '{slot}' tem formato inválido (esperado 'ARCH' + 4 dígitos, ex.: 'ARCH0007').",
                        0,
                        0));
                    continue;
                }

                var number = int.Parse(match.Groups[1].Value);
                if (number < 1 || number > 512)
                {
                    errors.Add(new CliDiagnostic(
                        SlotErrorCode,
                        $"Regra '{rule.Id}': slot '{slot}' está fora do pool reservado ao consumidor (ARCH0001..ARCH0512).",
                        0,
                        0));
                    continue;
                }

                if (firstIdBySlot.TryGetValue(slot, out var firstId))
                {
                    errors.Add(new CliDiagnostic(
                        SlotErrorCode,
                        $"Slot '{slot}' duplicado: declarado tanto em '{firstId}' quanto em '{rule.Id}'.",
                        0,
                        0));
                }
                else
                {
                    firstIdBySlot[slot] = rule.Id;
                }
            }

            return errors;
        }
    }
}
