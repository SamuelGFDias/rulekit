using System;
using System.Collections.Generic;

namespace Arch.Analyzer.Contracts
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

        public LayerMatchCriterion(
            string @namespace = null,
            string nameSuffix = null,
            string baseType = null,
            string implements = null,
            string attribute = null)
        {
            Namespace = @namespace;
            NameSuffix = nameSuffix;
            BaseType = baseType;
            Implements = implements;
            Attribute = attribute;
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

        public LayerDefinition(string name, IReadOnlyList<LayerMatchCriterion> match, IReadOnlyList<LayerMatchCriterion> exclude)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Match = match ?? Array.Empty<LayerMatchCriterion>();
            Exclude = exclude ?? Array.Empty<LayerMatchCriterion>();
        }
    }

    /// <summary>
    /// Uma regra declarada em `rules:` (ADR-001 D2/D4). `Slot` é o ID técnico do pool
    /// (ex.: "ARCH0007", já pré-declarado em SupportedDiagnostics); `Id` é o ID de negócio
    /// da organização (ex.: "ACME-001"), que nunca chega ao Roslyn — só à mensagem e a
    /// Diagnostic.Properties. `Type` seleciona o IRuleEvaluator responsável (Fase 2).
    /// `Extra` carrega os campos específicos de cada `type` (from/to/through/max/countBy/...)
    /// sem acoplar este contrato a todos os tipos de regra futuros.
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
        public IReadOnlyDictionary<string, string> Extra { get; }

        public RuleDefinition(
            string id,
            string slot,
            string type,
            bool enabled,
            string severityName,
            string messageTemplate,
            string help,
            IReadOnlyDictionary<string, string> extra)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Slot = slot ?? throw new ArgumentNullException(nameof(slot));
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Enabled = enabled;
            SeverityName = severityName;
            MessageTemplate = messageTemplate;
            Help = help;
            Extra = extra ?? new Dictionary<string, string>();
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
