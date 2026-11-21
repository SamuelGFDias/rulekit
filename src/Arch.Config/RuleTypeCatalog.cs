using System;
using System.Collections.Generic;
using System.Linq;

namespace Arch.Config
{
    /// <summary>Nomes canônicos (minúsculos) dos tipos de regra de v1 (ADR-001 D2).</summary>
    public static class RuleTypeNames
    {
        public const string ForbiddenCall = "forbidden-call";
        public const string MustRouteThrough = "must-route-through";
        public const string NamingConvention = "naming-convention";
        public const string MaxDependencies = "max-dependencies";
    }

    /// <summary>
    /// Nomes dos campos específicos de tipo em <c>rules[]</c> — a única fonte desses literais
    /// (parser, avaliadores e mensagens leem daqui).
    /// </summary>
    public static class RuleFieldNames
    {
        public const string From = "from";
        public const string To = "to";
        public const string Through = "through";
        public const string Layer = "layer";
        public const string Require = "require";
        public const string Max = "max";
        public const string CountBy = "countBy";
        public const string When = "when";
    }

    /// <summary>Valores aceitos em <c>require</c> (naming-convention) e <c>countBy</c> (max-dependencies).</summary>
    public static class RuleFieldValues
    {
        public const string RequireSuffix = "suffix";
        public const string RequirePrefix = "prefix";

        public const string CountByConstructorParameters = "constructor-parameters";
        public const string CountByFields = "fields";
        public const string CountByAll = "all";

        /// <summary>Chaves aceitas em <c>when</c> (forbidden-call, must-route-through).</summary>
        public const string WhenSame = "same";
        public const string WhenDiffer = "differ";
    }

    /// <summary>Forma do valor de um campo de regra no YAML.</summary>
    public enum RuleFieldKind
    {
        /// <summary>Escalar não vazio (ex.: <c>from: Controller</c>).</summary>
        Scalar,

        /// <summary>Lista de escalares não vazios (ex.: <c>through: [Service]</c>). Um escalar solto
        /// é aceito como lista de um item.</summary>
        List,

        /// <summary>Inteiro não negativo (ex.: <c>max: 7</c>).</summary>
        Integer,

        /// <summary>Mapeamento com exatamente um par chave → escalar (ex.: <c>require: { suffix: Repository }</c>).
        /// A forma legada escalar <c>chave=valor</c> também é aceita.</summary>
        KeyValue,

        /// <summary>Mapeamento com exatamente uma chave → lista de escalares não vazios (ex.:
        /// <c>when: { differ: [Module, Tenant] }</c>); um escalar solto como valor é uma lista de um item.
        /// A forma legada escalar <c>chave=A,B</c> também é aceita. Em <see cref="RulePropertyValue"/>:
        /// <c>Key</c> + <c>Items</c>; em <c>Extra</c>: <c>"chave=A,B"</c>.</summary>
        KeyList,
    }

    /// <summary>Declaração de um campo específico de um tipo de regra.</summary>
    public sealed class RuleFieldSchema
    {
        public string Name { get; }

        public RuleFieldKind Kind { get; }

        public bool Required { get; }

        /// <summary>
        /// Valores aceitos (comparação ordinal), ou vazio quando qualquer valor da forma
        /// <see cref="Kind"/> serve. Para <see cref="RuleFieldKind.KeyValue"/> e
        /// <see cref="RuleFieldKind.KeyList"/>, restringe a CHAVE.
        /// </summary>
        public IReadOnlyList<string> AllowedValues { get; }

        public RuleFieldSchema(string name, RuleFieldKind kind, bool required, params string[] allowedValues)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Kind = kind;
            Required = required;
            AllowedValues = allowedValues ?? Array.Empty<string>();
        }

        /// <summary>true quando <paramref name="value"/> é aceito por <see cref="AllowedValues"/>.</summary>
        public bool IsAllowedValue(string value)
            => AllowedValues.Count == 0 || AllowedValues.Contains(value, StringComparer.Ordinal);
    }

    /// <summary>Declaração de um tipo de regra: nome canônico + campos específicos.</summary>
    public sealed class RuleTypeSchema
    {
        private readonly Dictionary<string, RuleFieldSchema> _fieldsByName;

        public string Name { get; }

        public IReadOnlyList<RuleFieldSchema> Fields { get; }

        public RuleTypeSchema(string name, params RuleFieldSchema[] fields)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("O nome do tipo de regra não pode ser vazio.", nameof(name));
            }

            Name = name.Trim().ToLowerInvariant();
            Fields = fields ?? Array.Empty<RuleFieldSchema>();

            // Nomes de campo são sensíveis a caixa, como toda chave do YAML (`countBy`, não `countby`).
            _fieldsByName = new Dictionary<string, RuleFieldSchema>(StringComparer.Ordinal);
            foreach (var field in Fields)
            {
                _fieldsByName.Add(field.Name, field);
            }
        }

        public bool TryGetField(string name, out RuleFieldSchema field)
        {
            if (name == null)
            {
                field = null;
                return false;
            }

            return _fieldsByName.TryGetValue(name, out field);
        }
    }

    /// <summary>
    /// Catálogo de tipos de regra (issue #17): fonte de verdade do que é um <c>type</c> válido e de
    /// quais campos cada tipo aceita/exige. O parser valida contra ele; <c>RuleEvaluatorRegistry</c>
    /// no analyzer é só despacho, e um teste garante que os dois não divergem.
    /// </summary>
    public sealed class RuleTypeCatalog
    {
        private readonly Dictionary<string, RuleTypeSchema> _typesByName;

        /// <summary>Os 4 tipos de v1 (ADR-001 D2). Campos e obrigatoriedade espelham o que os
        /// avaliadores de <c>Arch.Rules</c> exigem para reportar algo.</summary>
        public static RuleTypeCatalog Default { get; } = new RuleTypeCatalog(
            new RuleTypeSchema(
                RuleTypeNames.ForbiddenCall,
                new RuleFieldSchema(RuleFieldNames.From, RuleFieldKind.Scalar, required: true),
                new RuleFieldSchema(RuleFieldNames.To, RuleFieldKind.Scalar, required: true),
                // Condição sobre as capturas [Nome] das camadas from/to (issue #24).
                new RuleFieldSchema(RuleFieldNames.When, RuleFieldKind.KeyList, required: false, RuleFieldValues.WhenSame, RuleFieldValues.WhenDiffer)),
            new RuleTypeSchema(
                RuleTypeNames.MustRouteThrough,
                new RuleFieldSchema(RuleFieldNames.From, RuleFieldKind.Scalar, required: true),
                new RuleFieldSchema(RuleFieldNames.To, RuleFieldKind.Scalar, required: true),
                // Só alimenta a mensagem ({through}); o avaliador detecta sem ele.
                new RuleFieldSchema(RuleFieldNames.Through, RuleFieldKind.List, required: false),
                new RuleFieldSchema(RuleFieldNames.When, RuleFieldKind.KeyList, required: false, RuleFieldValues.WhenSame, RuleFieldValues.WhenDiffer)),
            new RuleTypeSchema(
                RuleTypeNames.NamingConvention,
                new RuleFieldSchema(RuleFieldNames.Layer, RuleFieldKind.Scalar, required: true),
                new RuleFieldSchema(
                    RuleFieldNames.Require,
                    RuleFieldKind.KeyValue,
                    required: true,
                    RuleFieldValues.RequireSuffix,
                    RuleFieldValues.RequirePrefix)),
            new RuleTypeSchema(
                RuleTypeNames.MaxDependencies,
                new RuleFieldSchema(RuleFieldNames.Layer, RuleFieldKind.Scalar, required: true),
                new RuleFieldSchema(RuleFieldNames.Max, RuleFieldKind.Integer, required: true),
                new RuleFieldSchema(
                    RuleFieldNames.CountBy,
                    RuleFieldKind.Scalar,
                    required: true,
                    RuleFieldValues.CountByConstructorParameters,
                    RuleFieldValues.CountByFields,
                    RuleFieldValues.CountByAll)));

        public IReadOnlyList<RuleTypeSchema> Types { get; }

        public RuleTypeCatalog(params RuleTypeSchema[] types)
        {
            Types = types ?? Array.Empty<RuleTypeSchema>();
            _typesByName = new Dictionary<string, RuleTypeSchema>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in Types)
            {
                _typesByName.Add(type.Name, type);
            }
        }

        /// <summary>Nomes canônicos de todos os tipos do catálogo.</summary>
        public IEnumerable<string> TypeNames => Types.Select(t => t.Name);

        /// <summary>
        /// Resolve <paramref name="type"/> com trim e sem diferenciar caixa
        /// (<c>" Forbidden-Call "</c> → schema de <c>forbidden-call</c>).
        /// </summary>
        public bool TryGetType(string type, out RuleTypeSchema schema)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                schema = null;
                return false;
            }

            return _typesByName.TryGetValue(type.Trim(), out schema);
        }
    }
}
