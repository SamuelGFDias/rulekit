using Arch.Config;

namespace Arch.Rules
{
    /// <summary>
    /// `must-route-through` (ADR-001 D2, "Nota sobre must-route-through"): semanticamente, "A só
    /// alcança C passando por B" é, em análise estática, o mesmo que "A não chama C diretamente" — a
    /// rota real A→B→C não é verificável sem o grafo de chamadas completo (custo de build alto,
    /// frágil com DI/reflexão/delegates). Por isso esta regra é açúcar sintático sobre
    /// <see cref="ForbiddenCallEvaluator"/>: MESMA detecção (`from`/`to`), a única diferença é que
    /// `through` alimenta a mensagem (e, no futuro, um Code Fix — fora de escopo na Fase 2).
    ///
    /// <para><b>Formato de <c>through</c>.</b> O catálogo (<see cref="RuleTypeCatalog"/>) declara
    /// `through` como lista opcional (ex.: <c>through: [Service]</c>); em
    /// <see cref="RuleDefinition.Properties"/> ela chega tipada, e na forma texto
    /// (<see cref="RuleDefinition.GetString"/>/<see cref="RuleDefinition.Extra"/>) é serializada com
    /// <c>string.Join(",", ...)</c> — SEM espaço após a vírgula (<c>"Service,Outro"</c>). Este
    /// avaliador usa essa forma texto literalmente como valor do placeholder <c>{through}</c>,
    /// herdado de <see cref="ForbiddenCallEvaluator.ExpandTemplate"/> (que resolve qualquer campo da
    /// regra que não seja `from`/`to`/`symbol`/`id`).</para>
    /// </summary>
    public class MustRouteThroughEvaluator : ForbiddenCallEvaluator
    {
        /// <summary>Valor de `rules[].type` no YAML tratado por esta classe.</summary>
        public new const string TypeName = RuleTypeNames.MustRouteThrough;

        public override string RuleType
        {
            get { return TypeName; }
        }

        /// <summary>
        /// Único ponto de diferença em relação a <see cref="ForbiddenCallEvaluator"/>: o texto padrão
        /// nomeia o intermediário esperado via `{through}`. Detecção, controle de falso positivo e
        /// composição de mensagem (prefixo do ID de negócio, expansão de placeholders) são herdados
        /// sem modificação.
        /// </summary>
        protected override string DefaultMessageTemplate
        {
            get { return "{from} não pode alcançar {to} diretamente ({symbol}). Passe por {through}."; }
        }
    }
}
