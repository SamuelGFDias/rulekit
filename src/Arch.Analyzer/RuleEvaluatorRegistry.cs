using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Arch.Analyzer.Contracts;
using Arch.Rules;

namespace Arch.Analyzer
{
    /// <summary>
    /// Ponto de extensão do motor: mapeia o valor de `rules[].type` do YAML ao
    /// <see cref="IRuleEvaluator"/> que sabe registrar as ações Roslyn daquele tipo de regra.
    ///
    /// <para><b>Estado atual (Fase 2): populado com os 4 avaliadores concretos.</b>
    /// `forbidden-call`, `must-route-through`, `naming-convention` e `max-dependencies` (cada um
    /// em seu arquivo sob <c>src/Arch.Rules/</c>, PLANO-implementacao.md) são instanciados em
    /// <see cref="Default"/>. Uma regra do YAML de um tipo ainda não coberto por nenhum avaliador
    /// registrado continua sendo, para este analyzer, de um tipo desconhecido — e a política
    /// MINOR-aditiva do ADR-001 D4 manda exatamente isto: reportar ARCH9003 ("regra ignorada, requer
    /// a lib &gt;= x.y") e seguir em frente, sem invalidar o arquivo.</para>
    ///
    /// <para>O roteamento, o cache de config, o resolvedor de camadas e o repasse do descriptor do
    /// slot já estavam implementados e provados em teste antes desta integração (ver
    /// <c>ArchRulesAnalyzerPipelineTests</c>, que registra um avaliador de teste e exercita o
    /// pipeline inteiro).</para>
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
        /// Registro usado pelo analyzer em produção — os 4 avaliadores concretos da Fase 2.
        /// </summary>
        internal static ImmutableDictionary<string, IRuleEvaluator> Default
        {
            get
            {
                return Create(
                    new ForbiddenCallEvaluator(),
                    new MustRouteThroughEvaluator(),
                    new NamingConventionEvaluator(),
                    new MaxDependenciesEvaluator());
            }
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
