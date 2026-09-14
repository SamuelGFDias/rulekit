using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Arch.Analyzer.Contracts
{
    /// <summary>
    /// Um avaliador por tipo de regra (`forbidden-call`, `must-route-through`,
    /// `naming-convention`, `max-dependencies` — ADR-001 D2). Cada tipo de regra implementa
    /// esta interface em um arquivo próprio sob <c>src/Arch.Rules/</c> (Fase 2), o que é o que
    /// torna a paralelização por isolamento de arquivo segura (PLANO-implementacao.md, Fase 2).
    ///
    /// Contrato congelado na Fase 0 (spike de viabilidade). Uma implementação concreta não é
    /// necessária aqui — só a assinatura, para desbloquear os prompts delegados da Fase 2 sem
    /// esperar a Fase 1B terminar.
    /// </summary>
    public interface IRuleEvaluator
    {
        /// <summary>
        /// Valor de `rules[].type` no YAML que este avaliador trata (ex.: "forbidden-call").
        /// Usado por RegisterCompilationStartAction para rotear cada <see cref="RuleDefinition"/>
        /// resolvida ao avaliador correto.
        /// </summary>
        string RuleType { get; }

        /// <summary>
        /// Registra, dentro do compilation-start já em andamento, as ações Roslyn necessárias
        /// para esta regra (RegisterOperationAction, RegisterSymbolAction etc. — nunca varredura
        /// sintática de árvore completa, ADR-001 §5). Chamado uma vez por <see cref="RuleDefinition"/>
        /// cujo <c>Type</c> bate com <see cref="RuleType"/>.
        /// </summary>
        /// <param name="context">Contexto do compilation-start, para registrar as ações.</param>
        /// <param name="rule">A regra do YAML já resolvida (from/to/through/max/... em <c>Extra</c>).</param>
        /// <param name="layerResolver">Resolvedor de camadas já preparado para esta compilação.</param>
        /// <param name="slotDescriptor">
        /// O <see cref="DiagnosticDescriptor"/> do slot técnico pré-declarado (ex.: ARCH0007)
        /// vinculado a <paramref name="rule"/> pelo pool de slots (ADR-001 §3 Opção C). É este
        /// descriptor, nunca um novo construído em runtime, que deve ser usado em
        /// <c>Diagnostic.Create</c> — reportar um ID não declarado em
        /// <c>SupportedDiagnostics</c> produz AD0001 (ADR-001 §1).
        /// </param>
        void RegisterActions(
            CompilationStartAnalysisContext context,
            RuleDefinition rule,
            ILayerResolver layerResolver,
            DiagnosticDescriptor slotDescriptor);
    }
}
