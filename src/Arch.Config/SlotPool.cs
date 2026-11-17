using System.Globalization;

namespace Arch.Config
{
    /// <summary>Resultado de <see cref="SlotPool.Parse"/>.</summary>
    public enum SlotParseStatus
    {
        /// <summary>Slot do pool consumível (ARCH0001..ARCH0512).</summary>
        Valid,

        /// <summary>Não tem a forma <c>ARCH</c> + 4 dígitos ASCII (após trim, sem diferenciar caixa).</summary>
        InvalidFormat,

        /// <summary>Tem a forma certa, mas está fora de ARCH0001..ARCH0512 (ex.: a faixa ARCH9xxx do motor).</summary>
        OutOfRange,
    }

    /// <summary>
    /// Pool de slots técnicos (AGENTS.md §2, ADR-001 D2 Opção C) — fonte única do tamanho do pool e
    /// da normalização de <c>rules[].slot</c>, usada pelo parser, pelo analyzer (ArchDiagnostics) e
    /// pelo CLI (SlotValidator), para que os três deem o mesmo veredito (issue #6).
    /// Normalização: trim + sem diferenciar caixa (<c>" arch0007 "</c> ≡ <c>ARCH0007</c>).
    /// </summary>
    public static class SlotPool
    {
        /// <summary>Tamanho do pool (ADR-001 §3: "512 cobre qualquer política realista").</summary>
        public const int Count = 512;

        /// <summary>Prefixo dos IDs de slot.</summary>
        public const string Prefix = "ARCH";

        private const int DigitCount = 4;

        /// <summary>Classifica <paramref name="raw"/>; <paramref name="number"/> é o número do slot
        /// quando a forma é válida (mesmo fora do pool), senão 0.</summary>
        public static SlotParseStatus Parse(string raw, out int number)
        {
            number = 0;
            var text = raw?.Trim();
            if (text == null
                || text.Length != Prefix.Length + DigitCount
                || !text.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                return SlotParseStatus.InvalidFormat;
            }

            var value = 0;
            for (var i = Prefix.Length; i < text.Length; i++)
            {
                var c = text[i];
                // Só dígitos ASCII: `\d`/char.IsDigit aceitariam dígitos de outros scripts.
                if (c < '0' || c > '9')
                {
                    return SlotParseStatus.InvalidFormat;
                }

                value = (value * 10) + (c - '0');
            }

            number = value;
            return value >= 1 && value <= Count ? SlotParseStatus.Valid : SlotParseStatus.OutOfRange;
        }

        /// <summary>true quando <paramref name="raw"/> é um slot do pool; <paramref name="number"/> em 1..<see cref="Count"/>.</summary>
        public static bool TryParse(string raw, out int number)
        {
            if (Parse(raw, out number) == SlotParseStatus.Valid)
            {
                return true;
            }

            number = 0;
            return false;
        }

        /// <summary>true quando <paramref name="raw"/> é um slot do pool; <paramref name="canonical"/> no formato <c>ARCHnnnn</c>.</summary>
        public static bool TryNormalize(string raw, out string canonical)
        {
            if (TryParse(raw, out var number))
            {
                canonical = Format(number);
                return true;
            }

            canonical = null;
            return false;
        }

        /// <summary>ID canônico do slot de número <paramref name="number"/> (ex.: 7 → <c>ARCH0007</c>).</summary>
        public static string Format(int number)
            => Prefix + number.ToString("D4", CultureInfo.InvariantCulture);
    }
}
