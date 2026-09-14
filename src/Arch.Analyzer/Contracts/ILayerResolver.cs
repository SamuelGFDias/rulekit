using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Arch.Analyzer.Contracts
{
    /// <summary>
    /// Classifica um <see cref="INamedTypeSymbol"/> nas camadas declaradas em `layers:` do
    /// arch-rules.yaml (ADR-001 D2), aplicando os critérios OR-combinados
    /// (namespace/nameSuffix/baseType/implements/attribute) e respeitando `exclude`.
    ///
    /// Contrato congelado na Fase 0 (spike de viabilidade) para permitir que a Fase 1B
    /// (analyzer-core) e a Fase 2 (avaliadores de regra) sejam paralelizadas contra uma
    /// interface estável — ver PLANO-implementacao.md, Fase 0 e Fase 1.
    ///
    /// Implementação esperada (Fase 1B, fora do escopo deste spike): globs de namespace
    /// compilados para um trie de prefixos e classificação memoizada em
    /// ConcurrentDictionary&lt;INamedTypeSymbol, LayerSet&gt; com SymbolEqualityComparer.Default
    /// (ADR-001 §5, Performance).
    /// </summary>
    public interface ILayerResolver
    {
        /// <summary>
        /// Camadas (por nome, chave de <c>ArchConfig.Layers</c>) às quais o símbolo pertence.
        /// Pode conter mais de um nome se os critérios de match não forem mutuamente
        /// exclusivos; vazio se nenhuma camada casar ou se o símbolo cair em algum `exclude`.
        /// Nunca retorna null.
        /// </summary>
        IReadOnlyCollection<string> GetLayers(INamedTypeSymbol symbol);

        /// <summary>
        /// Atalho para regras que só precisam saber se o símbolo pertence a uma camada
        /// específica (ex.: `forbidden-call` checando `to` a cada invocação) sem alocar a
        /// coleção completa de <see cref="GetLayers"/>.
        /// </summary>
        bool IsInLayer(INamedTypeSymbol symbol, string layerName);
    }
}
