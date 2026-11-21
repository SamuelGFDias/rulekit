using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Microsoft.CodeAnalysis;

namespace Arch.Analyzer
{
    /// <summary>
    /// Implementação de <see cref="ILayerResolver"/> (ADR-001 D2 + §5).
    ///
    /// Tudo que é caro acontece UMA VEZ, no compilation-start, dentro de <see cref="Create"/>:
    /// <list type="bullet">
    ///   <item><description>globs de namespace viram <see cref="NamespaceTrie"/> — não há regex avaliada por símbolo;</description></item>
    ///   <item><description>`implements`/`baseType`/`attribute` viram <see cref="INamedTypeSymbol"/> via
    ///   <see cref="Compilation.GetTypeByMetadataName(string)"/>, uma única vez por NOME (compartilhado
    ///   entre camadas); o que não resolver sai em <c>unresolvedTypeNames</c> para virar UM ARCH9004,
    ///   nunca um por símbolo.</description></item>
    /// </list>
    ///
    /// No caminho quente (por símbolo) resta apenas: subir a cadeia de namespaces do próprio símbolo
    /// (sem <c>ToDisplayString</c>, que alocaria a string completa a cada consulta), comparar sufixo
    /// ordinal, e percorrer base types/interfaces/atributos comparando com
    /// <see cref="SymbolEqualityComparer.Default"/>. O resultado é memoizado em
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> — a segunda consulta do mesmo tipo não custa
    /// nada além do lookup.
    ///
    /// Thread-safety: a instância é criada no compilation-start e consultada em paralelo pelas ações
    /// registradas (<c>EnableConcurrentExecution</c>). Todo o estado construído é imutável depois de
    /// <see cref="Create"/>; a única estrutura mutável é o memo, concorrente por construção.
    /// </summary>
    internal sealed class LayerResolver : ILayerResolver
    {
        private static readonly string[] NoLayers = new string[0];
        private static readonly string[] NoNamespaceSegments = new string[0];
        private static readonly IReadOnlyDictionary<string, string> NoCaptures =
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

        private readonly CompiledLayer[] _layers;

        /// <summary>true se algum critério usa `assembly` — evita quebrar o nome do assembly à toa.</summary>
        private readonly bool _usesAssembly;

        /// <summary>
        /// Memoização símbolo→camadas (ADR-001 §5). A chave é <see cref="ISymbol"/>, e não
        /// <see cref="INamedTypeSymbol"/>, por uma restrição real do BCL:
        /// <c>SymbolEqualityComparer</c> implementa <c>IEqualityComparer&lt;ISymbol&gt;</c> e
        /// <c>IEqualityComparer&lt;T&gt;</c> é INVARIANTE — um
        /// <c>ConcurrentDictionary&lt;INamedTypeSymbol, …&gt;</c> não aceitaria
        /// <c>SymbolEqualityComparer.Default</c>. Usar o comparador default de referência aqui seria
        /// o bug clássico: dois <c>INamedTypeSymbol</c> do mesmo tipo vindos de compilações/threads
        /// diferentes não são a mesma instância, e o memo nunca acertaria.
        /// </summary>
        private readonly ConcurrentDictionary<ISymbol, Classification> _memo =
            new ConcurrentDictionary<ISymbol, Classification>(SymbolEqualityComparer.Default);

        /// <summary>Delegate cacheado: evita alocar um closure novo a cada <see cref="GetOrAdd"/>.</summary>
        private readonly Func<ISymbol, Classification> _classify;

        private LayerResolver(CompiledLayer[] layers)
        {
            _layers = layers;
            for (var i = 0; i < layers.Length; i++)
            {
                if (layers[i].Match.UsesAssembly || layers[i].Exclude.UsesAssembly)
                {
                    _usesAssembly = true;
                    break;
                }
            }

            _classify = symbol => Classify((INamedTypeSymbol)symbol);
        }

        /// <summary>
        /// Compila as camadas de <paramref name="config"/> contra <paramref name="compilation"/>.
        /// </summary>
        /// <param name="unresolvedTypeNames">
        /// Nomes de `implements`/`baseType`/`attribute` que <c>GetTypeByMetadataName</c> não resolveu,
        /// deduplicados e em ordem estável. O chamador reporta ARCH9004 uma vez por nome (ADR-001 §5);
        /// o critério correspondente é simplesmente ignorado — não casa nada e não derruba a camada.
        /// </param>
        internal static LayerResolver Create(
            ArchConfig config,
            Compilation compilation,
            out ImmutableArray<string> unresolvedTypeNames)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (compilation == null)
            {
                throw new ArgumentNullException(nameof(compilation));
            }

            // Resolução por NOME, compartilhada entre todas as camadas: o mesmo
            // "Acme.Application.IService" citado em três camadas custa uma única chamada a
            // GetTypeByMetadataName (que varre as referências de metadados da compilação).
            var resolvedByName = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
            var unresolved = new List<string>();
            var unresolvedSeen = new HashSet<string>(StringComparer.Ordinal);

            Func<string, INamedTypeSymbol> resolve = name =>
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }

                var key = name.Trim();

                if (resolvedByName.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                var symbol = compilation.GetTypeByMetadataName(key);
                resolvedByName.Add(key, symbol);

                if (symbol == null && unresolvedSeen.Add(key))
                {
                    unresolved.Add(key);
                }

                return symbol;
            };

            var layers = new List<CompiledLayer>(config.Layers?.Count ?? 0);

            if (config.Layers != null)
            {
                foreach (var entry in config.Layers)
                {
                    var definition = entry.Value;
                    if (definition == null)
                    {
                        continue;
                    }

                    // A chave do dicionário é a identidade da camada no YAML; Name é redundante mas
                    // pode divergir se o parser normalizar — a chave vence, é o que `from`/`to` citam.
                    var name = entry.Key ?? definition.Name;

                    layers.Add(new CompiledLayer(
                        name,
                        CompiledCriteria.Build(definition.Match, resolve),
                        CompiledCriteria.Build(definition.Exclude, resolve)));
                }
            }

            unresolvedTypeNames = unresolved.Count == 0
                ? ImmutableArray<string>.Empty
                : ImmutableArray.CreateRange(unresolved);

            return new LayerResolver(layers.ToArray());
        }

        /// <inheritdoc />
        public IReadOnlyCollection<string> GetLayers(INamedTypeSymbol symbol)
        {
            if (symbol == null || _layers.Length == 0)
            {
                return NoLayers;
            }

            // Genéricos construídos (Foo<int>) classificam igual à definição original (Foo<T>):
            // normalizar antes do memo evita uma entrada por instanciação.
            var key = (ISymbol)(symbol.OriginalDefinition ?? symbol);

            return _memo.GetOrAdd(key, _classify).Layers;
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> GetCaptures(INamedTypeSymbol symbol, string layerName)
        {
            if (symbol == null || string.IsNullOrEmpty(layerName) || _layers.Length == 0)
            {
                return NoCaptures;
            }

            var classification = _memo.GetOrAdd((ISymbol)(symbol.OriginalDefinition ?? symbol), _classify);

            IReadOnlyDictionary<string, string> captures;
            return classification.Captures != null && classification.Captures.TryGetValue(layerName, out captures)
                ? captures
                : NoCaptures;
        }

        /// <inheritdoc />
        public bool IsInLayer(INamedTypeSymbol symbol, string layerName)
        {
            if (symbol == null || string.IsNullOrEmpty(layerName) || _layers.Length == 0)
            {
                return false;
            }

            // Reaproveita o memo de GetLayers: a partir da segunda consulta do mesmo símbolo isto é
            // um lookup + varredura de um array de 1-2 posições, sem alocar (o array memoizado é
            // devolvido por referência, nunca copiado).
            var layers = _memo.GetOrAdd((ISymbol)(symbol.OriginalDefinition ?? symbol), _classify).Layers;

            for (var i = 0; i < layers.Length; i++)
            {
                if (string.Equals(layers[i], layerName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Resultado memoizado por símbolo: camadas e, por camada, as capturas (null se nenhuma).</summary>
        private sealed class Classification
        {
            internal readonly string[] Layers;
            internal readonly Dictionary<string, IReadOnlyDictionary<string, string>> Captures;

            internal Classification(string[] layers, Dictionary<string, IReadOnlyDictionary<string, string>> captures)
            {
                Layers = layers;
                Captures = captures;
            }
        }

        private static readonly Classification NoClassification = new Classification(NoLayers, null);

        private Classification Classify(INamedTypeSymbol symbol)
        {
            var namespaceSegments = GetNamespaceSegments(symbol);
            var assemblySegments = _usesAssembly ? GetAssemblySegments(symbol) : NoNamespaceSegments;
            List<string> matched = null;
            Dictionary<string, IReadOnlyDictionary<string, string>> captures = null;

            for (var i = 0; i < _layers.Length; i++)
            {
                var layer = _layers[i];

                if (!layer.Match.Matches(symbol, namespaceSegments, assemblySegments))
                {
                    continue;
                }

                // `exclude` é avaliado DEPOIS do match e tem a palavra final (ADR-001 D2): um símbolo
                // que casa em `match` e também em `exclude` não pertence à camada.
                if (layer.Exclude.Matches(symbol, namespaceSegments, assemblySegments))
                {
                    continue;
                }

                (matched ?? (matched = new List<string>(2))).Add(layer.Name);

                IReadOnlyDictionary<string, string> layerCaptures;
                if (layer.Match.TryCapture(namespaceSegments, assemblySegments, out layerCaptures))
                {
                    (captures ?? (captures = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)))
                        [layer.Name] = layerCaptures;
                }
            }

            return matched == null ? NoClassification : new Classification(matched.ToArray(), captures);
        }

        /// <summary>
        /// Segmentos do namespace do símbolo, do mais externo ao mais interno. Sobe a cadeia de
        /// <see cref="ISymbol.ContainingNamespace"/> em vez de formatar o nome completo: um
        /// <c>ToDisplayString()</c> alocaria uma string por consulta no caminho quente, e um
        /// <c>Split('.')</c> depois alocaria outro array — aqui o único array é o resultado.
        /// Tipo aninhado herda o namespace do tipo que o contém, que é o comportamento desejado.
        /// </summary>
        private static string[] GetNamespaceSegments(INamedTypeSymbol symbol)
        {
            var containing = symbol.ContainingNamespace;

            var depth = 0;
            for (var ns = containing; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
            {
                depth++;
            }

            if (depth == 0)
            {
                return NoNamespaceSegments; // namespace global.
            }

            var segments = new string[depth];
            var index = depth - 1;
            for (var ns = containing; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
            {
                segments[index--] = ns.Name;
            }

            return segments;
        }

        /// <summary>
        /// Segmentos (separados por ponto) de <c>ContainingAssembly.Identity.Name</c>. Funciona igual
        /// para tipos de código-fonte e de metadata. Sem assembly (não ocorre em tipos reais):
        /// nenhum segmento, e portanto nenhum critério `assembly` casa.
        /// </summary>
        private static string[] GetAssemblySegments(INamedTypeSymbol symbol)
        {
            var name = symbol.ContainingAssembly?.Identity.Name;
            return string.IsNullOrEmpty(name) ? NoNamespaceSegments : name.Split('.');
        }

        private sealed class CompiledLayer
        {
            internal readonly string Name;
            internal readonly CompiledCriteria Match;
            internal readonly CompiledCriteria Exclude;

            internal CompiledLayer(string name, CompiledCriteria match, CompiledCriteria exclude)
            {
                Name = name;
                Match = match;
                Exclude = exclude;
            }
        }

        /// <summary>
        /// Uma lista de <see cref="LayerMatchCriterion"/> (`match` ou `exclude`) já compilada.
        /// Critérios são combinados por OR (ADR-001 D2): basta um casar.
        /// </summary>
        private sealed class CompiledCriteria
        {
            private static readonly INamedTypeSymbol[] NoTypes = new INamedTypeSymbol[0];
            private static readonly CapturingPattern[] NoCapturePatterns = new CapturingPattern[0];

            private readonly NamespaceTrie _namespaces;
            private readonly NamespaceTrie _assemblies;
            private readonly CapturingPattern[] _captures; // namespace e assembly, na ordem declarada
            private readonly string[] _nameSuffixes;
            private readonly INamedTypeSymbol[] _baseTypes;
            private readonly INamedTypeSymbol[] _interfaces;
            private readonly INamedTypeSymbol[] _attributes;
            private readonly bool _isEmpty;

            private readonly bool _usesAssemblyCapture;

            internal bool UsesAssembly
            {
                get { return !_assemblies.IsEmpty || _usesAssemblyCapture; }
            }

            private CompiledCriteria(
                NamespaceTrie namespaces,
                NamespaceTrie assemblies,
                CapturingPattern[] captures,
                string[] nameSuffixes,
                INamedTypeSymbol[] baseTypes,
                INamedTypeSymbol[] interfaces,
                INamedTypeSymbol[] attributes)
            {
                _namespaces = namespaces;
                _assemblies = assemblies;
                _captures = captures;
                for (var i = 0; i < captures.Length; i++)
                {
                    if (captures[i].IsAssembly)
                    {
                        _usesAssemblyCapture = true;
                    }
                }

                _nameSuffixes = nameSuffixes;
                _baseTypes = baseTypes;
                _interfaces = interfaces;
                _attributes = attributes;
                _isEmpty = namespaces.IsEmpty
                    && assemblies.IsEmpty
                    && captures.Length == 0
                    && nameSuffixes.Length == 0
                    && baseTypes.Length == 0
                    && interfaces.Length == 0
                    && attributes.Length == 0;
            }

            internal static CompiledCriteria Build(
                IReadOnlyList<LayerMatchCriterion> criteria,
                Func<string, INamedTypeSymbol> resolve)
            {
                var namespaces = new NamespaceTrie();
                var assemblies = new NamespaceTrie();
                List<CapturingPattern> captures = null;
                List<string> suffixes = null;
                List<INamedTypeSymbol> baseTypes = null;
                List<INamedTypeSymbol> interfaces = null;
                List<INamedTypeSymbol> attributes = null;

                if (criteria != null)
                {
                    for (var i = 0; i < criteria.Count; i++)
                    {
                        var criterion = criteria[i];
                        if (criterion == null)
                        {
                            continue;
                        }

                        AddPattern(criterion.Namespace, false, namespaces, ref captures);

                        AddPattern(criterion.Assembly, true, assemblies, ref captures);

                        if (!string.IsNullOrWhiteSpace(criterion.NameSuffix))
                        {
                            (suffixes ?? (suffixes = new List<string>(2))).Add(criterion.NameSuffix.Trim());
                        }

                        AddResolved(criterion.BaseType, resolve, ref baseTypes);
                        AddResolved(criterion.Implements, resolve, ref interfaces);
                        AddResolved(criterion.Attribute, resolve, ref attributes);
                    }
                }

                return new CompiledCriteria(
                    namespaces,
                    assemblies,
                    captures == null ? NoCapturePatterns : captures.ToArray(),
                    suffixes == null ? new string[0] : suffixes.ToArray(),
                    baseTypes == null ? NoTypes : baseTypes.ToArray(),
                    interfaces == null ? NoTypes : interfaces.ToArray(),
                    attributes == null ? NoTypes : attributes.ToArray());
            }

            /// <summary>
            /// Padrão sem placeholder vai para o trie (caminho quente sem alocação); com placeholder,
            /// para a lista de <see cref="CapturingPattern"/> NA ORDEM DECLARADA (é a ordem que decide
            /// qual captura vale). Padrão inválido (config montada à mão) cai no trie, onde um segmento
            /// com colchete é só um literal que não casa nada.
            /// </summary>
            private static void AddPattern(string pattern, bool isAssembly, NamespaceTrie trie, ref List<CapturingPattern> captures)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    return;
                }

                var capturing = CapturingPattern.TryCreate(pattern, isAssembly);
                if (capturing == null)
                {
                    trie.Add(pattern.Trim());
                    return;
                }

                (captures ?? (captures = new List<CapturingPattern>(2))).Add(capturing);
            }

            /// <summary>
            /// Capturas do primeiro critério com placeholder que casa, na ordem declarada (namespace e
            /// assembly numa só sequência, na ordem do YAML). false quando nenhum casa — inclusive quando
            /// o `match` foi satisfeito por um critério sem placeholder.
            /// </summary>
            internal bool TryCapture(
                string[] namespaceSegments,
                string[] assemblySegments,
                out IReadOnlyDictionary<string, string> captures)
            {
                for (var i = 0; i < _captures.Length; i++)
                {
                    var pattern = _captures[i];
                    captures = pattern.Capture(pattern.IsAssembly ? assemblySegments : namespaceSegments);
                    if (captures != null)
                    {
                        return true;
                    }
                }

                captures = null;
                return false;
            }

            private static void AddResolved(
                string metadataName,
                Func<string, INamedTypeSymbol> resolve,
                ref List<INamedTypeSymbol> target)
            {
                if (string.IsNullOrWhiteSpace(metadataName))
                {
                    return;
                }

                var resolved = resolve(metadataName);
                if (resolved == null)
                {
                    // Nome não resolvido já foi registrado para ARCH9004 por `resolve`. O critério
                    // some da lista: não casa nada, e não invalida os demais critérios da camada.
                    return;
                }

                (target ?? (target = new List<INamedTypeSymbol>(2))).Add(resolved);
            }

            internal bool Matches(INamedTypeSymbol symbol, string[] namespaceSegments, string[] assemblySegments)
            {
                if (_isEmpty)
                {
                    return false;
                }

                if (_namespaces.Matches(namespaceSegments))
                {
                    return true;
                }

                if (_assemblies.Matches(assemblySegments))
                {
                    return true;
                }

                for (var i = 0; i < _captures.Length; i++)
                {
                    var pattern = _captures[i];
                    if (pattern.Matches(pattern.IsAssembly ? assemblySegments : namespaceSegments))
                    {
                        return true;
                    }
                }

                for (var i = 0; i < _nameSuffixes.Length; i++)
                {
                    if (symbol.Name.EndsWith(_nameSuffixes[i], StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                if (_baseTypes.Length > 0)
                {
                    for (var baseType = symbol.BaseType; baseType != null; baseType = baseType.BaseType)
                    {
                        if (ContainsSymbol(_baseTypes, baseType.OriginalDefinition))
                        {
                            return true;
                        }
                    }
                }

                if (_interfaces.Length > 0)
                {
                    // AllInterfaces já inclui as herdadas transitivamente — é o que "implements"
                    // significa para quem escreve a política.
                    var allInterfaces = symbol.AllInterfaces;
                    for (var i = 0; i < allInterfaces.Length; i++)
                    {
                        if (ContainsSymbol(_interfaces, allInterfaces[i].OriginalDefinition))
                        {
                            return true;
                        }
                    }
                }

                if (_attributes.Length > 0)
                {
                    var applied = symbol.GetAttributes();
                    for (var i = 0; i < applied.Length; i++)
                    {
                        var attributeClass = applied[i].AttributeClass;
                        if (attributeClass != null && ContainsSymbol(_attributes, attributeClass.OriginalDefinition))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private static bool ContainsSymbol(INamedTypeSymbol[] candidates, INamedTypeSymbol symbol)
            {
                for (var i = 0; i < candidates.Length; i++)
                {
                    // SymbolEqualityComparer.Default, nunca ==: símbolos equivalentes podem ser
                    // instâncias diferentes (compilações distintas, referências de metadados).
                    if (SymbolEqualityComparer.Default.Equals(candidates[i], symbol))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
