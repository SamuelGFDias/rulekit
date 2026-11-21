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
    /// Chave desconhecida no nível raiz, de camada, de critério ou de `defaults` é erro de schema
    /// bloqueante (ARCH9001) — ADR-001 D1. Dentro de `rules[]`, as chaves CORE (id/slot/type/
    /// enabled/severity/message/help) são validadas sempre; os campos específicos do tipo só depois
    /// de resolver o `type` contra <see cref="RuleTypeCatalog"/> (issues #2, #3, #5, #17):
    /// <list type="bullet">
    /// <item>`type` desconhecido (comparado com trim e sem diferenciar caixa): ARCH9003 não
    /// bloqueante e a regra é descartada SEM examinar os campos dela — política MINOR-aditiva de D4.</item>
    /// <item>`type` conhecido com campo obrigatório ausente, chave que o tipo não declara ou valor
    /// inválido: ARCH9008 não bloqueante (um por regra, citando todos os campos), regra descartada,
    /// demais regras continuam valendo.</item>
    /// </list>
    /// Nenhum caminho lança exceção para YAML malformado: chave complexa (não escalar) vira ARCH9001.
    /// </summary>
    internal static class ArchConfigDocumentParser
    {
        internal const string InvalidRuleFieldsCode = "ARCH9008";
        internal const string UnknownRuleTypeCode = "ARCH9003";

        private static readonly RuleTypeCatalog Catalog = RuleTypeCatalog.Default;

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
            "namespace", "nameSuffix", "baseType", "implements", "attribute", "assembly"
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
                if (!TryGetKey(result, kv, "no nível raiz", out var key))
                {
                    return result;
                }

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

            // TryParse: o regex aceita dígitos ilimitados; um major que não cabe em int é só mais um
            // major não suportado, nunca uma OverflowException (issue #4).
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                || major != 1)
            {
                Blocking(
                    result,
                    $"'schema' com MAJOR não suportada: '{schemaScalar.Value}'. Esta versão da lib só entende a major v1 (ADR-001 D4).",
                    schemaScalar.Start);
                return false;
            }

            result.Schema = schemaScalar.Value;
            return true;
        }

        /// <summary>
        /// Extrai SÓ a lista de `extends:` de um texto YAML, reaproveitando integralmente
        /// <see cref="ParseExtends"/> — a extração não é duplicada, para que a forma aceita de
        /// `extends` (lista de escalares) tenha uma única fonte de verdade.
        ///
        /// <para>Deliberadamente NÃO roda as regras de subset do <see cref="YamlSubsetGuard"/> (só os limites de
        /// tamanho/profundidade, para não estourar a pilha) nem valida o resto do
        /// schema: quem chama (Arch.Analyzer) precisa desta informação ANTES de ter a cadeia
        /// completa de textos, e a validação de verdade acontece depois, em
        /// <see cref="ArchConfigParser.Parse(IReadOnlyList{string})"/>, sobre a cadeia inteira. Um
        /// YAML inválido demais para ter o `extends` extraído devolve lista vazia — o erro real
        /// (ARCH9001, com linha/coluna) aparece de qualquer forma quando o texto entrar em
        /// <c>Parse</c>.</para>
        /// </summary>
        internal static IReadOnlyList<string> PeekExtends(string yamlText)
        {
            try
            {
                // Só os LIMITES (tamanho/profundidade) — sem as regras do subset: carregar a DOM de um
                // texto com aninhamento extremo estoura a pilha (revisão B2), e isso derrubaria o
                // processo do compilador antes de o Parse real poder reportar o ARCH9001.
                if (YamlSubsetGuard.ValidateLimits(yamlText) != null)
                {
                    return Array.Empty<string>();
                }

                var stream = new YamlStream();
                stream.Load(new StringReader(yamlText ?? string.Empty));

                if (stream.Documents.Count == 0 || !(stream.Documents[0].RootNode is YamlMappingNode root))
                {
                    return Array.Empty<string>();
                }

                var probe = new ParsedDocument();
                if (!ParseExtends(root, probe))
                {
                    // `extends` presente mas malformado (não é lista, item não escalar): não há o
                    // que resolver. O erro bloqueante correspondente será reportado pelo Parse real.
                    return Array.Empty<string>();
                }

                return probe.Extends ?? Array.Empty<string>();
            }
            catch (Exception)
            {
                // YAML sintaticamente inválido (YamlException) ou qualquer outra falha do modelo de
                // representação. Ver o parágrafo sobre isso no XML doc acima: nunca é papel deste
                // método reportar erro.
                return Array.Empty<string>();
            }
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
                if (!TryGetKey(result, layerKv, "em 'layers'", out var layerName))
                {
                    return false;
                }

                if (!(layerKv.Value is YamlMappingNode layerDef))
                {
                    Blocking(result, $"Camada '{layerName}' deve ser um mapeamento com 'match'/'exclude'.", layerKv.Value.Start);
                    return false;
                }

                foreach (var lk in layerDef.Children)
                {
                    if (!TryGetKey(result, lk, $"na camada '{layerName}'", out var lkName))
                    {
                        return false;
                    }

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

                string ns = null, suffix = null, baseType = null, implementsName = null, attribute = null, assemblyName = null;
                var filled = 0;

                foreach (var kv in itemMap.Children)
                {
                    if (!TryGetKey(result, kv, $"em critério de '{fieldName}' da camada '{layerName}'", out var key))
                    {
                        return false;
                    }

                    if (!AllowedCriterionKeys.Contains(key))
                    {
                        Blocking(
                            result,
                            $"Chave desconhecida em critério de '{fieldName}' da camada '{layerName}': '{key}'.",
                            kv.Key.Start);
                        return false;
                    }

                    var value = (kv.Value as YamlScalarNode)?.Value;

                    // Placeholders `[Nome]` só existem em namespace/assembly: malformado ou repetido é
                    // erro de schema bloqueante, ancorado no valor (issue #24).
                    if ((key == "namespace" || key == "assembly")
                        && !SegmentPattern.TryParse(value, out _, out var patternProblem))
                    {
                        Blocking(
                            result,
                            $"Critério '{key}' de '{fieldName}' da camada '{layerName}': {patternProblem}.",
                            kv.Value.Start);
                        return false;
                    }

                    filled++;
                    switch (key)
                    {
                        case "namespace": ns = value; break;
                        case "nameSuffix": suffix = value; break;
                        case "baseType": baseType = value; break;
                        case "implements": implementsName = value; break;
                        case "attribute": attribute = value; break;
                        case "assembly": assemblyName = value; break;
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

                list.Add(new LayerMatchCriterion(ns, suffix, baseType, implementsName, attribute, assemblyName));
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
                if (!TryGetKey(result, kv, "em 'severities'", out var name))
                {
                    return false;
                }
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
                if (!TryGetKey(result, kv, "em 'defaults'", out var key))
                {
                    return false;
                }

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

            // Campos específicos do tipo: só coletados aqui. A validação deles depende do `type`
            // resolvido — se o tipo for desconhecido, eles nem são examinados (issue #2).
            var typeFields = new List<KeyValuePair<string, YamlNode>>();

            foreach (var kv in ruleMap.Children)
            {
                if (!TryGetKey(result, kv, $"na regra{(id != null ? $" '{id}'" : string.Empty)}", out var key))
                {
                    return false;
                }

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
                else
                {
                    typeFields.Add(new KeyValuePair<string, YamlNode>(key, kv.Value));
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

            if (!Catalog.TryGetType(type, out var typeSchema))
            {
                // ARCH9003: não bloqueante (ADR-001 D4, MINOR aditivo) — a regra é descartada de
                // Rules sem examinar seus campos, e as demais regras seguem valendo.
                result.Errors.Add(new SchemaValidationError(
                    UnknownRuleTypeCode,
                    $"Regra '{id}': type '{type}' desconhecido — requer uma versão mais nova da lib. Regra ignorada.",
                    ruleMap.Start.Line,
                    ruleMap.Start.Column,
                    isBlocking: false));
                return true;
            }

            var problems = new List<string>();
            var properties = new Dictionary<string, RulePropertyValue>(StringComparer.Ordinal);

            foreach (var field in typeFields)
            {
                if (!typeSchema.TryGetField(field.Key, out var fieldSchema))
                {
                    problems.Add($"chave '{field.Key}' não é aceita pelo type '{typeSchema.Name}'");
                    continue;
                }

                if (TryConvertField(fieldSchema, field.Value, out var value, out var problem))
                {
                    properties[fieldSchema.Name] = value;
                }
                else
                {
                    problems.Add($"'{fieldSchema.Name}' {problem}");
                }
            }

            var missing = typeSchema.Fields
                .Where(f => f.Required && typeFields.All(tf => !string.Equals(tf.Key, f.Name, StringComparison.Ordinal)))
                .Select(f => $"'{f.Name}'")
                .ToList();
            if (missing.Count > 0)
            {
                problems.Insert(0, (missing.Count == 1 ? "campo obrigatório ausente: " : "campos obrigatórios ausentes: ")
                    + string.Join(", ", missing));
            }

            if (problems.Count > 0)
            {
                // ARCH9008: não bloqueante — a regra não seria avaliada como o autor espera, então é
                // descartada de forma RUIDOSA (issue #5), sem derrubar as demais.
                result.Errors.Add(new SchemaValidationError(
                    InvalidRuleFieldsCode,
                    $"Regra '{id}' (type '{typeSchema.Name}') ignorada: {string.Join("; ", problems)}.",
                    ruleMap.Start.Line,
                    ruleMap.Start.Column,
                    isBlocking: false));
                return true;
            }

            // Slot do pool é guardado na forma canônica (ARCHnnnn), para que CLI e analyzer gerem e
            // consultem a mesma chave; fora do pool fica como veio, e ARCH9002 é decidido adiante.
            var normalizedSlot = SlotPool.TryNormalize(slot, out var canonicalSlot) ? canonicalSlot : slot;

            var rule = RuleDefinition.FromProperties(
                id, normalizedSlot, typeSchema.Name, enabled, severity, message, help, properties);
            rule.SourceLine = (int)ruleMap.Start.Line;
            rule.SourceColumn = (int)ruleMap.Start.Column;
            result.Rules.Add(rule);
            return true;
        }

        /// <summary>
        /// Converte o nó YAML de um campo para a forma declarada no catálogo. Devolve false com uma
        /// descrição curta do problema (sem o nome do campo) quando o valor não serve.
        /// </summary>
        private static bool TryConvertField(RuleFieldSchema field, YamlNode node, out RulePropertyValue value, out string problem)
        {
            value = null;
            problem = null;

            switch (field.Kind)
            {
                case RuleFieldKind.Scalar:
                {
                    var text = (node as YamlScalarNode)?.Value?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        problem = "deve ser um valor escalar não vazio";
                        return false;
                    }

                    if (!field.IsAllowedValue(text))
                    {
                        problem = $"tem valor '{text}' inválido (aceitos: {string.Join(", ", field.AllowedValues)})";
                        return false;
                    }

                    value = RulePropertyValue.FromScalar(text);
                    return true;
                }

                case RuleFieldKind.List:
                {
                    var items = new List<string>();
                    if (node is YamlScalarNode single)
                    {
                        items.Add(single.Value?.Trim());
                    }
                    else if (node is YamlSequenceNode seq)
                    {
                        foreach (var item in seq.Children)
                        {
                            items.Add((item as YamlScalarNode)?.Value?.Trim());
                        }
                    }
                    else
                    {
                        problem = "deve ser uma lista de valores escalares";
                        return false;
                    }

                    if (items.Count == 0 || items.Any(string.IsNullOrEmpty))
                    {
                        problem = "deve ser uma lista não vazia de valores escalares não vazios";
                        return false;
                    }

                    var notAllowed = items.FirstOrDefault(i => !field.IsAllowedValue(i));
                    if (notAllowed != null)
                    {
                        problem = $"tem item '{notAllowed}' inválido (aceitos: {string.Join(", ", field.AllowedValues)})";
                        return false;
                    }

                    value = RulePropertyValue.FromList(items);
                    return true;
                }

                case RuleFieldKind.Integer:
                {
                    var text = (node as YamlScalarNode)?.Value?.Trim();
                    if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                        || number < 0)
                    {
                        problem = $"deve ser um inteiro não negativo (encontrado: '{DescribeNode(node)}')";
                        return false;
                    }

                    value = RulePropertyValue.FromInteger(number);
                    return true;
                }

                case RuleFieldKind.KeyValue:
                {
                    string key, val;
                    if (node is YamlMappingNode map)
                    {
                        if (map.Children.Count != 1
                            || !(map.Children.First().Key is YamlScalarNode keyNode)
                            || !(map.Children.First().Value is YamlScalarNode valueNode)
                            || string.IsNullOrWhiteSpace(keyNode.Value)
                            || string.IsNullOrWhiteSpace(valueNode.Value))
                        {
                            problem = "deve ser um mapeamento com exatamente um par chave: valor (ex.: { suffix: Repository })";
                            return false;
                        }

                        key = keyNode.Value.Trim();
                        val = valueNode.Value.Trim();
                    }
                    else if (!(node is YamlScalarNode scalar) || !RuleDefinition.TryParseKeyValue(scalar.Value, out key, out val))
                    {
                        problem = "deve ser um mapeamento com exatamente um par chave: valor (ex.: { suffix: Repository })";
                        return false;
                    }

                    if (!field.IsAllowedValue(key))
                    {
                        problem = $"tem chave '{key}' inválida (aceitas: {string.Join(", ", field.AllowedValues)})";
                        return false;
                    }

                    value = RulePropertyValue.FromKeyValue(key, val);
                    return true;
                }

                case RuleFieldKind.KeyList:
                {
                    const string shape = "deve ser um mapeamento com exatamente uma chave cujo valor é uma lista de nomes (ex.: { differ: [Module] })";
                    string key = null;
                    var items = new List<string>();

                    if (node is YamlMappingNode map)
                    {
                        if (map.Children.Count != 1
                            || !(map.Children.First().Key is YamlScalarNode keyNode)
                            || string.IsNullOrWhiteSpace(keyNode.Value))
                        {
                            problem = shape;
                            return false;
                        }

                        key = keyNode.Value.Trim();
                        var valueNode = map.Children.First().Value;
                        if (valueNode is YamlScalarNode single)
                        {
                            items.Add(single.Value?.Trim());
                        }
                        else if (valueNode is YamlSequenceNode seq)
                        {
                            foreach (var item in seq.Children)
                            {
                                items.Add((item as YamlScalarNode)?.Value?.Trim());
                            }
                        }
                        else
                        {
                            problem = shape;
                            return false;
                        }
                    }
                    else if (node is YamlScalarNode legacy && RuleDefinition.TryParseKeyValue(legacy.Value, out var legacyKey, out var legacyValue))
                    {
                        key = legacyKey;
                        items.AddRange(legacyValue.Split(',').Select(i => i.Trim()));
                    }
                    else
                    {
                        problem = shape;
                        return false;
                    }

                    if (items.Count == 0 || items.Any(string.IsNullOrEmpty))
                    {
                        problem = "deve ter uma lista não vazia de nomes não vazios";
                        return false;
                    }

                    if (!field.IsAllowedValue(key))
                    {
                        problem = $"tem chave '{key}' inválida (aceitas: {string.Join(", ", field.AllowedValues)})";
                        return false;
                    }

                    value = RulePropertyValue.FromKeyList(key, items);
                    return true;
                }

                default:
                    problem = "tem uma forma não suportada";
                    return false;
            }
        }

        private static string DescribeNode(YamlNode node)
            => node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : StringifyNode(node);

        /// <summary>
        /// Lê a chave de um par de mapeamento. Uma chave complexa (`? [a, b] : v`, sequência ou
        /// mapeamento como chave) não existe neste schema e vira ARCH9001 bloqueante — nunca um
        /// InvalidCastException (issue #4).
        /// </summary>
        private static bool TryGetKey(ParsedDocument result, KeyValuePair<YamlNode, YamlNode> kv, string where, out string key)
        {
            if (kv.Key is YamlScalarNode scalar && scalar.Value != null)
            {
                key = scalar.Value;
                return true;
            }

            key = null;
            Blocking(result, $"Chave complexa (não escalar) {where} não é suportada neste schema.", kv.Key.Start);
            return false;
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
