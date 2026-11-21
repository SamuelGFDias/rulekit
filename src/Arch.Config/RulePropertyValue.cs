using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Arch.Config
{
    /// <summary>
    /// Valor imutável e tipado de um campo específico de regra (issue #17, B2). O parser produz a
    /// forma declarada no <see cref="RuleTypeCatalog"/>; uma <see cref="RuleDefinition"/> montada só
    /// com <c>extra</c> (texto) carrega valores <see cref="RuleFieldKind.Scalar"/> e os acessores de
    /// <see cref="RuleDefinition"/> reinterpretam o texto quando pedido em outra forma.
    /// </summary>
    public sealed class RulePropertyValue
    {
        private static readonly IReadOnlyList<string> NoItems = Array.Empty<string>();

        public RuleFieldKind Kind { get; }

        /// <summary>Texto do escalar (<see cref="RuleFieldKind.Scalar"/>); null nas demais formas.</summary>
        public string Scalar { get; }

        /// <summary>Itens (<see cref="RuleFieldKind.List"/> e <see cref="RuleFieldKind.KeyList"/>); vazio nas demais formas.</summary>
        public IReadOnlyList<string> Items { get; }

        /// <summary>Valor inteiro (<see cref="RuleFieldKind.Integer"/>); 0 nas demais formas.</summary>
        public int Integer { get; }

        /// <summary>Chave do par (<see cref="RuleFieldKind.KeyValue"/> e <see cref="RuleFieldKind.KeyList"/>); null nas demais formas.</summary>
        public string Key { get; }

        /// <summary>Valor do par (<see cref="RuleFieldKind.KeyValue"/>); null nas demais formas.</summary>
        public string Value { get; }

        private RulePropertyValue(RuleFieldKind kind, string scalar, IReadOnlyList<string> items, int integer, string key, string value)
        {
            Kind = kind;
            Scalar = scalar;
            Items = items ?? NoItems;
            Integer = integer;
            Key = key;
            Value = value;
        }

        public static RulePropertyValue FromScalar(string value)
            => new RulePropertyValue(RuleFieldKind.Scalar, value ?? string.Empty, null, 0, null, null);

        public static RulePropertyValue FromList(IEnumerable<string> items)
            => new RulePropertyValue(
                RuleFieldKind.List,
                null,
                Array.AsReadOnly((items ?? Enumerable.Empty<string>()).Select(i => i ?? string.Empty).ToArray()),
                0,
                null,
                null);

        public static RulePropertyValue FromInteger(int value)
            => new RulePropertyValue(RuleFieldKind.Integer, null, null, value, null, null);

        public static RulePropertyValue FromKeyValue(string key, string value)
            => new RulePropertyValue(
                RuleFieldKind.KeyValue,
                null,
                null,
                0,
                key ?? throw new ArgumentNullException(nameof(key)),
                value ?? throw new ArgumentNullException(nameof(value)));

        /// <summary>Par chave → lista (<see cref="RuleFieldKind.KeyList"/>), ex.: <c>differ</c> → <c>[Module, Tenant]</c>.</summary>
        public static RulePropertyValue FromKeyList(string key, IEnumerable<string> items)
            => new RulePropertyValue(
                RuleFieldKind.KeyList,
                null,
                Array.AsReadOnly((items ?? Enumerable.Empty<string>()).Select(i => i ?? string.Empty).ToArray()),
                0,
                key ?? throw new ArgumentNullException(nameof(key)),
                null);

        /// <summary>
        /// Serialização texto usada em <see cref="RuleDefinition.Extra"/> (contrato de AGENTS.md §8):
        /// escalar como está; lista com <c>string.Join(",", ...)</c> sem espaço; inteiro em cultura
        /// invariante; par como <c>chave=valor</c>; par chave → lista como <c>chave=A,B</c>
        /// (ex.: <c>differ=Module,Tenant</c>).
        /// </summary>
        public string ToExtraString()
        {
            switch (Kind)
            {
                case RuleFieldKind.List:
                    return string.Join(",", Items);
                case RuleFieldKind.Integer:
                    return Integer.ToString(CultureInfo.InvariantCulture);
                case RuleFieldKind.KeyValue:
                    return Key + "=" + Value;
                case RuleFieldKind.KeyList:
                    return Key + "=" + string.Join(",", Items);
                default:
                    return Scalar ?? string.Empty;
            }
        }

        public override string ToString() => ToExtraString();
    }
}
