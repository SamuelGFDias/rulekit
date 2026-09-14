using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Arch.Config.Internal
{
    /// <summary>
    /// Parseia um único texto YAML (uma "camada" de `extends`) para um <see cref="ParsedDocument"/>,
    /// aplicando a sintaxe completa de ADR-001 D2 e o versionamento de schema de D4. Não resolve
    /// `extends` (isso é <see cref="ArchConfigMerger"/>, orquestrado por
    /// <see cref="ArchConfigParser"/>) nem faz I/O.
    ///
    /// Chave desconhecida em qualquer nível é sempre erro de schema bloqueante (ARCH9001) — ADR-001
    /// D1. Isso vale também para os campos "extra" de `rules[]` (from/to/through/layer/require/
    /// max/countBy): o conjunto é fixo nesta MAJOR (v1); um campo fora dele é rejeitado, não
    /// silenciosamente aceito no `RuleDefinition.Extra` — manter esse bag "aberto" de fato
    /// contradiria D1. Estender o conjunto é uma mudança MINOR aditiva de schema.
    /// </summary>
    internal static class ArchConfigDocumentParser
    {
        private static readonly HashSet<string> KnownRuleTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "forbidden-call", "must-route-through", "naming-convention", "max-dependencies"
        };

        private static readonly HashSet<string> KnownExtraKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "from", "to", "through", "layer", "require", "max", "countBy"
        };

        private static readonly HashSet<string> AllowedTopLevelKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "schema", "extends", "layers", "severities", "defaults", "rules"
        };

        private static readonly HashSet<string> AllowedLayerKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "match", "exclude"
        };

        private static readonly HashSet<string> AllowedCriterionKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "namespace", "nameSuffix", "baseType", "implements", "attribute"
        };

        private static readonly HashSet<string> AllowedRuleCoreKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "slot", "type", "enabled", "severity", "message", "help"
        };

        private static readonly Regex SchemaPattern = new Regex(@"^arch-rules/v(\d+)$", RegexOptions.CultureInvariant);

        public static ParsedDocument Parse(string yamlText)
        {
            var result = new ParsedDocument();

            var subsetError = YamlSubsetGuard.Validate(yamlText);
            if (subsetError != null)
            {
                result.Errors.Add(subsetError);
                return result;
            }

            YamlStream stream;
            try
            {
                stream = new YamlStream();
                stream.Load(new StringReader(yamlText ?? string.Empty));
            }
            catch (YamlException ex)
            {
                Blocking(result, "YAML sintaticamente inválido: " + ex.Message, ex.Start);
                return result;
            }

            if (stream.Documents.Count == 0 || !(stream.Documents[0].RootNode is YamlMappingNode root))
            {
                // Documento vazio/raiz não-mapeamento não tem uma posição de nó útil para apontar
                // além do próprio início do arquivo.
                Blocking(result, "Documento YAML vazio ou a raiz não é um mapeamento.", 0, 0);
                return result;
            }

            foreach (var kv in root.Children)
            {
                var key = ((YamlScalarNode)kv.Key).Value;
                if (!AllowedTopLevelKeys.Contains(key))
                {
                    Blocking(result, $"Chave desconhecida no nível raiz: '{key}'.", kv.Key.Start);
                    return result;
                }
            }

            if (!ParseSchema(root, result))
            {
                return result;
            }

            if (!ParseExtends(root, result))
            {
                return result;
            }

            if (!ParseLayers(root, result))
            {
                return result;
            }

            if (!ParseSeverities(root, result))
            {
                return result;
            }

            if (!ParseDefaults(root, result))
            {
                return result;
            }

            ParseRules(root, result);

            return result;
        }

        private static bool ParseSchema(YamlMappingNode root, ParsedDocument result)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode("schema"), out var schemaNode))
            {
                // Chave ausente: não há um nó no documento para apontar, então usamos 0/0
                // (documentado no contrato de SchemaValidationError.Line/Column).
                Blocking(result, "Chave obrigatória 'schema' ausente (esperado, ex.: 'arch-rules/v1').", 0, 0);
                return false;
            }

            if (!(schemaNode is YamlScalarNode schemaScalar))
            {
                Blocking(result, "'schema' deve ser um valor escalar, ex.: 'arch-rules/v1'.", schemaNode.Start);
                return false;
            }

            var match = SchemaPattern.Match(schemaScalar.Value ?? string.Empty);
            if (!match.Success)
            {
                Blocking(result, $"'schema' inválido: '{schemaScalar.Value}'. Formato esperado: 'arch-rules/vN'.", schemaScalar.Start);
                return false;
            }

            var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (major != 1)
            {
                Blocking(
                    result,
                    $"'schema' com MAJOR não suportada: 'arch-rules/v{major}'. Esta versão da lib só entende a major v1 (ADR-001 D4).",
                    schemaScalar.Start);
                return false;
            }

            result.Schema = schemaScalar.Value;
            return true;
        }

        private static bool ParseExtends(YamlMappingNode root, ParsedDocument result)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode("extends"), out var extendsNode))
            {
                return true;
            }

            if (!(extendsNode is YamlSequenceNode extendsSeq))
            {
                Blocking(result, "'extends' deve ser uma lista de caminhos.", extendsNode.Start);
                return false;
            }

            var list = new List<string>();
            foreach (var item in extendsSeq.Children)
            {
                if (!(item is YamlScalarNode itemScalar))
                {
                    Blocking(result, "Cada item de 'extends' deve ser um caminho escalar.", item.Start);
                    return false;
                }

                list.Add(itemScalar.Value);
            }

            result.Extends = list;
            return true;
        }

        private static bool ParseLayers(YamlMappingNode root, ParsedDocument result)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode("layers"), out var layersNode))
            {
                return true;
            }

            if (!(layersNode is YamlMappingNode layersMap))
            {
                Blocking(result, "'layers' deve ser um mapeamento nome -> definição de camada.", layersNode.Start);
                return false;
            }

            foreach (var layerKv in layersMap.Children)
            {
                var layerName = ((YamlScalarNode)layerKv.Key).Value;

                if (!(layerKv.Value is YamlMappingNode layerDef))
                {
                    Blocking(result, $"Camada '{layerName}' deve ser um mapeamento com 'match'/'exclude'.", layerKv.Value.Start);
                    return false;
                }

                foreach (var lk in layerDef.Children)
                {
                    var lkName = ((YamlScalarNode)lk.Key).Value;
                    if (!AllowedLayerKeys.Contains(lkName))
                    {
                        Blocking(result, $"Chave desconhecida na camada '{layerName}': '{lkName}'.", lk.Key.Start);
                        return false;
                    }
                }

                if (!layerDef.Children.TryGetValue(new YamlScalarNode("match"), out var matchNode))
                {
                    Blocking(result, $"Camada '{layerName}' requer 'match'.", layerDef.Start);
                    return false;
                }

                if (!TryParseCriteria(result, layerName, "match", matchNode, out var match))
                {
                    return false;
                }

                var exclude = (IReadOnlyList<LayerMatchCriterion>)Array.Empty<LayerMatchCriterion>();
                if (layerDef.Children.TryGetValue(new YamlScalarNode("exclude"), out var excludeNode))
                {
                    if (!TryParseCriteria(result, layerName, "exclude", excludeNode, out exclude))
                    {
                        return false;
                    }
                }

                result.Layers[layerName] = new LayerDefinition(layerName, match, exclude);
            }

            return true;
        }

        private static bool TryParseCriteria(
            ParsedDocument result,
            string layerName,
            string fieldName,
            YamlNode node,
            out IReadOnlyList<LayerMatchCriterion> criteria)
        {
            criteria = Array.Empty<LayerMatchCriterion>();

            if (!(node is YamlSequenceNode seq))
            {
                Blocking(result, $"'{fieldName}' da camada '{layerName}' deve ser uma lista.", node.Start);
                return false;
            }

            var list = new List<LayerMatchCriterion>();
            foreach (var item in seq.Children)
            {
                if (!(item is YamlMappingNode itemMap))
                {
                    Blocking(
                        result,
                        $"Item de '{fieldName}' da camada '{layerName}' deve ser um mapeamento com exatamente um critério.",
                        item.Start);
                    return false;
                }

                string ns = null, suffix = null, baseType = null, implementsName = null, attribute = null;
                var filled = 0;

                foreach (var kv in itemMap.Children)
                {
                    var key = ((YamlScalarNode)kv.Key).Value;
                    if (!AllowedCriterionKeys.Contains(key))
                    {
                        Blocking(
                            result,
                            $"Chave desconhecida em critério de '{fieldName}' da camada '{layerName}': '{key}'.",
                            kv.Key.Start);
                        return false;
                    }

                    var value = (kv.Value as YamlScalarNode)?.Value;
                    filled++;
                    switch (key)
                    {
                        case "namespace": ns = value; break;
                        case "nameSuffix": suffix = value; break;
                        case "baseType": baseType = value; break;
                        case "implements": implementsName = value; break;
                        case "attribute": attribute = value; break;
                    }
                }

                if (filled != 1)
                {
                    Blocking(
                        result,
                        $"Cada critério de '{fieldName}' da camada '{layerName}' deve preencher exatamente um campo (encontrados: {filled}).",
                        itemMap.Start);
                    return false;
                }

                list.Add(new LayerMatchCriterion(ns, suffix, baseType, implementsName, attribute));
            }

            criteria = list;
            return true;
        }

        private static bool ParseSeverities(YamlMappingNode root, ParsedDocument result)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode("severities"), out var severitiesNode))
            {
                return true;
            }

            if (!(severitiesNode is YamlMappingNode sevMap))
            {
                Blocking(result, "'severities' deve ser um mapeamento nome -> severidade.", severitiesNode.Start);
                return false;
            }

            foreach (var kv in sevMap.Children)
            {
                var name = ((YamlScalarNode)kv.Key).Value;
                if (!(kv.Value is YamlScalarNode valueScalar))
                {
                    Blocking(result, $"Severidade '{name}' deve ter um valor escalar.", kv.Value.Start);
                    return false;
                }

                result.Severities[name] = valueScalar.Value;
            }

            return true;
        }

        private static bool ParseDefaults(YamlMappingNode root, ParsedDocument result)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode("defaults"), out var defaultsNode))
            {
                return true;
            }

            if (!(defaultsNode is YamlMappingNode defMap))
            {
                Blocking(result, "'defaults' deve ser um mapeamento.", defaultsNode.Start);
                return false;
            }

            foreach (var kv in defMap.Children)
            {
                var key = ((YamlScalarNode)kv.Key).Value;
                if (key != "severity")
                {
                    Blocking(result, $"Chave desconhecida em 'defaults': '{key}'.", kv.Key.Start);
                    return false;
                }

                result.DefaultSeverityName = (kv.Value as YamlScalarNode)?.Value;
            }

            return true;
        }

        private static void ParseRules(YamlMappingNode root, ParsedDocument result)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode("rules"), out var rulesNode))
            {
                return;
            }

            if (!(rulesNode is YamlSequenceNode rulesSeq))
            {
                Blocking(result, "'rules' deve ser uma lista.", rulesNode.Start);
                return;
            }

            foreach (var item in rulesSeq.Children)
            {
                if (!(item is YamlMappingNode ruleMap))
                {
                    Blocking(result, "Cada item de 'rules' deve ser um mapeamento.", item.Start);
                    return;
                }

                if (!TryParseRule(result, ruleMap))
                {
                    return;
                }
            }
        }

        private static bool TryParseRule(ParsedDocument result, YamlMappingNode ruleMap)
        {
            string id = null, slot = null, type = null, severity = null, message = null, help = null;
            var enabled = true;
            var extra = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var kv in ruleMap.Children)
            {
                var key = ((YamlScalarNode)kv.Key).Value;

                if (AllowedRuleCoreKeys.Contains(key))
                {
                    var scalarValue = kv.Value as YamlScalarNode;
                    switch (key)
                    {
                        case "id":
                            id = scalarValue?.Value;
                            break;
                        case "slot":
                            slot = scalarValue?.Value;
                            break;
                        case "type":
                            type = scalarValue?.Value;
                            break;
                        case "enabled":
                            if (scalarValue == null || !bool.TryParse(scalarValue.Value, out enabled))
                            {
                                Blocking(result, "'enabled' da regra deve ser um booleano ('true'/'false').", kv.Value.Start);
                                return false;
                            }

                            break;
                        case "severity":
                            severity = scalarValue?.Value;
                            break;
                        case "message":
                            message = scalarValue?.Value;
                            break;
                        case "help":
                            help = scalarValue?.Value;
                            break;
                    }
                }
                else if (KnownExtraKeys.Contains(key))
                {
                    extra[key] = StringifyNode(kv.Value);
                }
                else
                {
                    Blocking(
                        result,
                        $"Chave desconhecida na regra{(id != null ? $" '{id}'" : string.Empty)}: '{key}'.",
                        kv.Key.Start);
                    return false;
                }
            }

            if (id == null)
            {
                Blocking(result, "Regra sem 'id'.", ruleMap.Start);
                return false;
            }

            if (slot == null)
            {
                Blocking(result, $"Regra '{id}' sem 'slot'.", ruleMap.Start);
                return false;
            }

            if (type == null)
            {
                Blocking(result, $"Regra '{id}' sem 'type'.", ruleMap.Start);
                return false;
            }

            if (!KnownRuleTypes.Contains(type))
            {
                // ARCH9003: não bloqueante (ADR-001 D4, MINOR aditivo) — a regra é descartada de
                // Rules, mas o parsing do documento continua e as demais regras seguem valendo.
                result.Errors.Add(new SchemaValidationError(
                    "ARCH9003",
                    $"Regra '{id}': type '{type}' desconhecido — requer uma versão mais nova da lib. Regra ignorada.",
                    ruleMap.Start.Line,
                    ruleMap.Start.Column,
                    isBlocking: false));
                return true;
            }

            result.Rules.Add(new RuleDefinition(id, slot, type, enabled, severity, message, help, extra));
            return true;
        }

        private static string StringifyNode(YamlNode node)
        {
            switch (node)
            {
                case YamlScalarNode scalar:
                    return scalar.Value ?? string.Empty;
                case YamlSequenceNode seq:
                    return string.Join(",", seq.Children.Select(StringifyNode));
                case YamlMappingNode map:
                    return string.Join(
                        ",",
                        map.Children
                            .Select(kv => $"{StringifyNode(kv.Key)}={StringifyNode(kv.Value)}")
                            .OrderBy(s => s, StringComparer.Ordinal));
                default:
                    return string.Empty;
            }
        }

        private static void Blocking(ParsedDocument result, string message, Mark mark)
            => Blocking(result, message, mark.Line, mark.Column);

        private static void Blocking(ParsedDocument result, string message, int line, int column)
            => result.Errors.Add(new SchemaValidationError("ARCH9001", message, line, column, isBlocking: true));
    }
}
