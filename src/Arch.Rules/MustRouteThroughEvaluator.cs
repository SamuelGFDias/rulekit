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
    /// <para><b>Formato de <c>through</c> em <see cref="RuleDefinition.Extra"/>.</b> Confirmado
    /// contra <c>Arch.Config/Internal/ArchConfigDocumentParser.cs</c> (<c>StringifyNode</c>):
    /// `through` é uma <c>YamlSequenceNode</c> no YAML (ex.: <c>through: [Service]</c>), e o parser
    /// serializa a sequência com <c>string.Join(",", ...)</c> — SEM espaço após a vírgula. Uma lista
    /// de um item vira <c>Extra["through"] = "Service"</c>; uma lista de dois itens viraria
    /// <c>"Service,Outro"</c>. Este avaliador não precisa desmembrar essa string: ela é usada
    /// literalmente como valor do placeholder <c>{through}</c> na mensagem, herdado de graça de
    /// <see cref="ForbiddenCallEvaluator.ExpandTemplate"/> (que resolve qualquer chave de
    /// <see cref="RuleDefinition.Extra"/> que não seja `from`/`to`/`symbol`/`id`).</para>
    /// </summary>
    public class MustRouteThroughEvaluator : ForbiddenCallEvaluator
    {
        /// <summary>Valor de `rules[].type` no YAML tratado por esta classe.</summary>
        public new const string TypeName = "must-route-through";

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
