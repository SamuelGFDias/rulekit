using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Arch.Config
{
    /// <summary>
    /// Um critério de match de camada (`layers.<nome>.match[]` / `.exclude[]` no YAML,
    /// ADR-001 D2). Os critérios dentro de uma mesma lista são combinados por OR; exatamente
    /// um dos campos abaixo é preenchido por instância — o schema do YAML valida isso na
    /// Fase 1A (Arch.Config), este tipo aqui só carrega o resultado já validado.
    /// </summary>
    public sealed class LayerMatchCriterion
    {
        /// <summary>Glob de namespace, ex.: "Acme.Api.Controllers.**".</summary>
        public string Namespace { get; }

        /// <summary>Sufixo do nome do tipo, ex.: "Controller".</summary>
        public string NameSuffix { get; }

        /// <summary>Nome totalmente qualificado do tipo base (resolvido via GetTypeByMetadataName).</summary>
        public string BaseType { get; }

        /// <summary>Nome totalmente qualificado de interface implementada.</summary>
        public string Implements { get; }

        /// <summary>Nome totalmente qualificado de um atributo aplicado ao tipo.</summary>
        public string Attribute { get; }

        /// <summary>
        /// Padrão do nome do assembly que contém o tipo (<c>ContainingAssembly.Identity.Name</c>),
        /// ex.: "Acme.Infrastructure.**". Mesma semântica de curinga de <see cref="Namespace"/>
        /// (<c>*</c> = um segmento, <c>**</c> = zero ou mais, sem curinga = nome exato); os segmentos
        /// são os separados por ponto no nome do assembly.
        /// </summary>
        public string Assembly { get; }

        public LayerMatchCriterion(
            string @namespace = null,
            string nameSuffix = null,
            string baseType = null,
            string implements = null,
            string attribute = null,
            string assembly = null)
        {
            Namespace = @namespace;
            NameSuffix = nameSuffix;
            BaseType = baseType;
            Implements = implements;
            Attribute = attribute;
            Assembly = assembly;
        }
    }

    /// <summary>
    /// Uma camada declarada em `layers:` (ADR-001 D2), já com `extends`/merge resolvido.
    /// </summary>
    public sealed class LayerDefinition
    {
        public string Name { get; }
        public IReadOnlyList<LayerMatchCriterion> Match { get; }
        public IReadOnlyList<LayerMatchCriterion> Exclude { get; }

        /// <summary>
        /// Nomes capturados por placeholders <c>[Nome]</c> nos critérios <c>namespace</c>/<c>assembly</c>
        /// de <see cref="Match"/> (distintos, na ordem de declaração). <c>exclude</c> não captura e não
        /// entra aqui.
        /// </summary>
        public IReadOnlyList<string> CaptureNames { get; }

        public LayerDefinition(string name, IReadOnlyList<LayerMatchCriterion> match, IReadOnlyList<LayerMatchCriterion> exclude)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Match = match ?? Array.Empty<LayerMatchCriterion>();
            Exclude = exclude ?? Array.Empty<LayerMatchCriterion>();
            CaptureNames = CollectCaptureNames(Match);
        }

        private static IReadOnlyList<string> CollectCaptureNames(IReadOnlyList<LayerMatchCriterion> match)
        {
            var names = new List<string>();
            foreach (var criterion in match)
            {
                if (criterion == null)
                {
                    continue;
                }

                foreach (var pattern in new[] { criterion.Namespace, criterion.Assembly })
                {
                    foreach (var name in SegmentPattern.GetCaptureNames(pattern))
                    {
                        if (!names.Contains(name))
                        {
                            names.Add(name);
                        }
                    }
                }
            }

            return names.Count == 0 ? (IReadOnlyList<string>)Array.Empty<string>() : names.ToArray();
        }
    }

    /// <summary>
    /// Uma regra declarada em `rules:` (ADR-001 D2/D4). `Slot` é o ID técnico do pool
    /// (ex.: "ARCH0007", já pré-declarado em SupportedDiagnostics); `Id` é o ID de negócio
    /// da organização (ex.: "ACME-001"), que nunca chega ao Roslyn — só à mensagem e a
    /// Diagnostic.Properties. `Type` seleciona o IRuleEvaluator responsável (Fase 2).
    ///
    /// <para>Campos específicos de cada `type` (from/to/through/layer/require/max/countBy) chegam
    /// tipados em <see cref="Properties"/> (forma declarada em <see cref="RuleTypeCatalog"/>) e, por
    /// compatibilidade, também como texto em <see cref="Extra"/> — sempre derivado do mesmo dado:
    /// lista vira <c>"A,B"</c> (sem espaço), par vira <c>"chave=valor"</c>. Quem constrói a regra só
    /// com <c>extra</c> recebe <see cref="Properties"/> escalares derivadas dele; os acessores
    /// (<see cref="GetString"/>, <see cref="GetList"/>, <see cref="TryGetInt"/>,
    /// <see cref="TryGetKeyValue"/>) reinterpretam o texto nesse caso.</para>
    /// </summary>
    public sealed class RuleDefinition
    {
        public string Id { get; }
        public string Slot { get; }
        public string Type { get; }
        public bool Enabled { get; }
        public string SeverityName { get; }
        public string MessageTemplate { get; }
        public string Help { get; }

        /// <summary>Campos específicos do tipo, em texto (derivado de <see cref="Properties"/>).</summary>
        public IReadOnlyDictionary<string, string> Extra { get; }

        /// <summary>Campos específicos do tipo, tipados (issue #17, B2). Chaves sensíveis a caixa.</summary>
        public IReadOnlyDictionary<string, RulePropertyValue> Properties { get; }

        /// <summary>
        /// Linha 1-based do item da regra no YAML que a declarou (o documento de índice
        /// <see cref="SourceDocumentIndex"/>); 0 quando desconhecida (regra montada à mão).
        /// Preenchida pelo parser — é o que permite a quem reporta um diagnóstico sobre a regra
        /// (ex.: ARCH9002/ARCH9005 no analyzer) apontar a linha certa (issue #7).
        /// </summary>
        public int SourceLine { get; internal set; }

        /// <summary>Coluna 1-based correspondente a <see cref="SourceLine"/>; 0 quando desconhecida.</summary>
        public int SourceColumn { get; internal set; }

        /// <summary>
        /// Índice, na lista passada a <see cref="ArchConfigParser.Parse(IReadOnlyList{string})"/>, do
        /// texto que declarou a regra (com `extends`, a regra vencedora pode vir de um arquivo base);
        /// -1 quando desconhecido.
        /// </summary>
        public int SourceDocumentIndex { get; internal set; } = -1;

        public RuleDefinition(
            string id,
            string slot,
            string type,
            bool enabled,
            string severityName,
            string messageTemplate,
            string help,
            IReadOnlyDictionary<string, string> extra)
            : this(id, slot, type, enabled, severityName, messageTemplate, help, extra, properties: null)
        {
        }

        /// <summary>
        /// Monta uma regra a partir de campos já tipados (é o que o parser usa). Fábrica em vez de
        /// sobrecarga de construtor para não tornar ambígua uma chamada existente que passe
        /// <c>null</c> literal como <c>extra</c>.
        /// </summary>
        public static RuleDefinition FromProperties(
            string id,
            string slot,
            string type,
            bool enabled,
            string severityName,
            string messageTemplate,
            string help,
            IReadOnlyDictionary<string, RulePropertyValue> properties)
            => new RuleDefinition(id, slot, type, enabled, severityName, messageTemplate, help, extra: null, properties: properties ?? EmptyProperties);

        private static readonly IReadOnlyDictionary<string, RulePropertyValue> EmptyProperties =
            new ReadOnlyDictionary<string, RulePropertyValue>(new Dictionary<string, RulePropertyValue>());

        private RuleDefinition(
            string id,
            string slot,
            string type,
            bool enabled,
            string severityName,
            string messageTemplate,
            string help,
            IReadOnlyDictionary<string, string> extra,
            IReadOnlyDictionary<string, RulePropertyValue> properties)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Slot = slot ?? throw new ArgumentNullException(nameof(slot));
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Enabled = enabled;
            SeverityName = severityName;
            MessageTemplate = messageTemplate;
            Help = help;

            var typed = new Dictionary<string, RulePropertyValue>(StringComparer.Ordinal);
            var text = new Dictionary<string, string>(StringComparer.Ordinal);

            if (properties != null)
            {
                foreach (var kv in properties)
                {
                    if (kv.Key == null || kv.Value == null)
                    {
                        continue;
                    }

                    typed[kv.Key] = kv.Value;
                    text[kv.Key] = kv.Value.ToExtraString();
                }
            }
            else if (extra != null)
            {
                foreach (var kv in extra)
                {
                    if (kv.Key == null)
                    {
                        continue;
                    }

                    typed[kv.Key] = RulePropertyValue.FromScalar(kv.Value);
                    text[kv.Key] = kv.Value ?? string.Empty;
                }
            }

            Properties = new ReadOnlyDictionary<string, RulePropertyValue>(typed);
            Extra = new ReadOnlyDictionary<string, string>(text);
        }

        /// <summary>
        /// Texto do campo (escalar como está; outras formas na serialização de <see cref="Extra"/>),
        /// ou null quando o campo não existe.
        /// </summary>
        public string GetString(string name)
        {
            return name != null && Properties.TryGetValue(name, out var value) ? value.ToExtraString() : null;
        }

        /// <summary>
        /// Itens do campo. Um escalar é lido como a serialização de <see cref="Extra"/>
        /// (separado por vírgula, itens aparados, vazios descartados). Campo ausente: lista vazia.
        /// </summary>
        public IReadOnlyList<string> GetList(string name)
        {
            if (name == null || !Properties.TryGetValue(name, out var value))
            {
                return Array.Empty<string>();
            }

            if (value.Kind == RuleFieldKind.List)
            {
                return value.Items;
            }

            return value.ToExtraString()
                .Split(',')
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToArray();
        }

        /// <summary>Valor inteiro do campo; um escalar é aceito se for um inteiro em cultura invariante.</summary>
        public bool TryGetInt(string name, out int result)
        {
            result = 0;
            if (name == null || !Properties.TryGetValue(name, out var value))
            {
                return false;
            }

            if (value.Kind == RuleFieldKind.Integer)
            {
                result = value.Integer;
                return true;
            }

            return value.Kind == RuleFieldKind.Scalar
                && int.TryParse(value.Scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        /// <summary>Par chave/valor do campo; um escalar é aceito no formato <c>chave=valor</c> (aparados).</summary>
        public bool TryGetKeyValue(string name, out string key, out string value)
        {
            key = null;
            value = null;
            if (name == null || !Properties.TryGetValue(name, out var property))
            {
                return false;
            }

            if (property.Kind == RuleFieldKind.KeyValue)
            {
                key = property.Key;
                value = property.Value;
                return true;
            }

            return property.Kind == RuleFieldKind.Scalar && TryParseKeyValue(property.Scalar, out key, out value);
        }

        /// <summary>
        /// Par chave → lista do campo (<see cref="RuleFieldKind.KeyList"/>, ex.: <c>when: { differ: [Module] }</c>);
        /// um escalar é aceito na forma de <see cref="Extra"/> (<c>chave=A,B</c>, itens aparados, vazios
        /// descartados). Falha se a lista ficar vazia.
        /// </summary>
        public bool TryGetKeyList(string name, out string key, out IReadOnlyList<string> items)
        {
            key = null;
            items = Array.Empty<string>();
            if (name == null || !Properties.TryGetValue(name, out var property))
            {
                return false;
            }

            if (property.Kind == RuleFieldKind.KeyList)
            {
                key = property.Key;
                items = property.Items;
                return items.Count > 0;
            }

            if (property.Kind != RuleFieldKind.Scalar || !TryParseKeyValue(property.Scalar, out var parsedKey, out var parsedValue))
            {
                return false;
            }

            var parsed = parsedValue
                .Split(',')
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToArray();
            if (parsed.Length == 0)
            {
                return false;
            }

            key = parsedKey;
            items = parsed;
            return true;
        }

        internal static bool TryParseKeyValue(string raw, out string key, out string value)
        {
            key = null;
            value = null;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            var separator = raw.IndexOf('=');
            if (separator <= 0 || separator == raw.Length - 1)
            {
                return false;
            }

            var k = raw.Substring(0, separator).Trim();
            var v = raw.Substring(separator + 1).Trim();
            if (k.Length == 0 || v.Length == 0)
            {
                return false;
            }

            key = k;
            value = v;
            return true;
        }
    }

    /// <summary>
    /// Modelo em memória de um arch-rules.yaml já resolvido: `extends` mesclado, schema
    /// validado, camadas e regras prontas para o ILayerResolver/IRuleEvaluator consumirem
    /// (ADR-001 D2/D3/D4). É o objeto produzido uma vez por compilação dentro de
    /// RegisterCompilationStartAction e cacheado em ConditionalWeakTable&lt;SourceText, ArchConfig&gt;
    /// chaveado pela instância do SourceText do AdditionalFile (ADR-001 §5, Performance —
    /// nunca por SourceText.GetChecksum(), que é struct e não serve de chave de
    /// ConditionalWeakTable, nem por caminho, que não reflete o conteúdo).
    /// </summary>
    public sealed class ArchConfig
    {
        /// <summary>"arch-rules/v1" — primeira chave obrigatória do YAML (ADR-001 D4).</summary>
        public string Schema { get; }

        public IReadOnlyDictionary<string, LayerDefinition> Layers { get; }

        public IReadOnlyList<RuleDefinition> Rules { get; }

        /// <summary>Bloco `severities:` — nome livre da organização -> valor aceito por
        /// `dotnet_diagnostic.&lt;slot&gt;.severity` ("error"/"warning"/"suggestion"/"silent"/"none").</summary>
        public IReadOnlyDictionary<string, string> Severities { get; }

        /// <summary>Chave em <see cref="Severities"/> usada quando uma regra não declara `severity`.</summary>
        public string DefaultSeverityName { get; }

        public ArchConfig(
            string schema,
            IReadOnlyDictionary<string, LayerDefinition> layers,
            IReadOnlyList<RuleDefinition> rules,
            IReadOnlyDictionary<string, string> severities,
            string defaultSeverityName)
        {
            Schema = schema ?? throw new ArgumentNullException(nameof(schema));
            Layers = layers ?? throw new ArgumentNullException(nameof(layers));
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            Severities = severities ?? throw new ArgumentNullException(nameof(severities));
            DefaultSeverityName = defaultSeverityName;
        }
    }
}
