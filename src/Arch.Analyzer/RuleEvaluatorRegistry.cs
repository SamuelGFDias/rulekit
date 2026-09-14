using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Arch.Analyzer.Contracts;

namespace Arch.Analyzer
{
    /// <summary>
    /// Ponto de extensão do motor: mapeia o valor de `rules[].type` do YAML ao
    /// <see cref="IRuleEvaluator"/> que sabe registrar as ações Roslyn daquele tipo de regra.
    ///
    /// <para><b>Estado atual (Fase 1B): vazio, e isso é o comportamento correto — não um furo.</b>
    /// Os avaliadores concretos (`forbidden-call`, `must-route-through`, `naming-convention`,
    /// `max-dependencies`) são entregues pela Fase 2, cada um em seu arquivo sob
    /// <c>src/Arch.Rules/</c> (PLANO-implementacao.md). Enquanto o registro estiver vazio, TODA regra
    /// declarada no YAML é, literalmente, de um tipo desconhecido para este analyzer — e a política
    /// MINOR-aditiva do ADR-001 D4 manda exatamente isto: reportar ARCH9003 ("regra ignorada, requer
    /// a lib &gt;= x.y") e seguir em frente, sem invalidar o arquivo.</para>
    ///
    /// <para>Para a Fase 2, popular <see cref="Default"/> é a única mudança necessária aqui: o
    /// roteamento, o cache de config, o resolvedor de camadas e o repasse do descriptor do slot já
    /// estão implementados e provados em teste (ver <c>ArchRulesAnalyzerPipelineTests</c>, que
    /// registra um avaliador de teste e exercita o pipeline inteiro).</para>
    /// </summary>
    internal static class RuleEvaluatorRegistry
    {
        /// <summary>
        /// Comparação do `type` da regra. OrdinalIgnoreCase para que `Forbidden-Call` e
        /// `forbidden-call` não virem um ARCH9003 gratuito — o schema (Arch.Config) é quem decide se
        /// quer ser estrito com a grafia.
        /// </summary>
        internal static readonly StringComparer TypeComparer = StringComparer.OrdinalIgnoreCase;

        internal static readonly ImmutableDictionary<string, IRuleEvaluator> Empty =
            ImmutableDictionary.Create<string, IRuleEvaluator>(TypeComparer);

        /// <summary>
        /// Registro usado pelo analyzer em produção. Fase 2 substitui por
        /// <c>Create(new ForbiddenCallEvaluator(), new NamingConventionEvaluator(), …)</c>.
        /// </summary>
        internal static ImmutableDictionary<string, IRuleEvaluator> Default
        {
            get { return Empty; }
        }

        /// <summary>
        /// Monta um registro a partir dos avaliadores dados, indexado por
        /// <see cref="IRuleEvaluator.RuleType"/>. Usado pela Fase 2 e pelos testes do pipeline.
        /// </summary>
        internal static ImmutableDictionary<string, IRuleEvaluator> Create(params IRuleEvaluator[] evaluators)
        {
            if (evaluators == null || evaluators.Length == 0)
            {
                return Empty;
            }

            var builder = ImmutableDictionary.CreateBuilder<string, IRuleEvaluator>(TypeComparer);

            foreach (var evaluator in evaluators)
            {
                if (evaluator == null || string.IsNullOrWhiteSpace(evaluator.RuleType))
                {
                    throw new ArgumentException(
                        "Todo IRuleEvaluator precisa declarar um RuleType não vazio.", nameof(evaluators));
                }

                builder[evaluator.RuleType.Trim()] = evaluator;
            }

            return builder.ToImmutable();
        }
    }
}
