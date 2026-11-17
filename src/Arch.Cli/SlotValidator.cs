using System.Collections.Generic;
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
    /// Formato, limite e normalização vêm de <see cref="SlotPool"/> (Arch.Config), o mesmo que
    /// Arch.Analyzer usa para decidir ARCH9002 em runtime — CLI e analyzer dão o mesmo veredito
    /// para o mesmo YAML (issue #6). Este validador reusa o mesmo código conceitual como string literal:
    /// o CLI não referencia Arch.Analyzer (que traria Roslyn como dependência sem necessidade
    /// aqui, só para reportar um código de erro).
    /// </summary>
    public static class SlotValidator
    {
        public const string SlotErrorCode = "ARCH9002";

        public static IReadOnlyList<CliDiagnostic> Validate(IReadOnlyList<RuleDefinition> rules)
        {
            var errors = new List<CliDiagnostic>();
            var firstIdBySlot = new Dictionary<int, string>();
            var maxSlot = SlotPool.Format(SlotPool.Count);

            foreach (var rule in rules)
            {
                var slot = rule.Slot;

                // Mesma normalização do analyzer (SlotPool: trim, sem diferenciar caixa) — issue #6.
                switch (SlotPool.Parse(slot, out var number))
                {
                    case SlotParseStatus.InvalidFormat:
                        errors.Add(new CliDiagnostic(
                            SlotErrorCode,
                            $"Regra '{rule.Id}': slot '{slot}' tem formato inválido (esperado '{SlotPool.Prefix}' + 4 dígitos, ex.: 'ARCH0007').",
                            0,
                            0));
                        continue;

                    case SlotParseStatus.OutOfRange:
                        errors.Add(new CliDiagnostic(
                            SlotErrorCode,
                            $"Regra '{rule.Id}': slot '{slot}' está fora do pool reservado ao consumidor ({SlotPool.Format(1)}..{maxSlot}).",
                            0,
                            0));
                        continue;
                }

                // Duplicata comparada pelo número já normalizado: 'ARCH0007' e ' arch0007 ' são o mesmo slot.
                if (firstIdBySlot.TryGetValue(number, out var firstId))
                {
                    errors.Add(new CliDiagnostic(
                        SlotErrorCode,
                        $"Slot '{SlotPool.Format(number)}' duplicado: declarado tanto em '{firstId}' quanto em '{rule.Id}'.",
                        0,
                        0));
                }
                else
                {
                    firstIdBySlot[number] = rule.Id;
                }
            }

            return errors;
        }
    }
}
