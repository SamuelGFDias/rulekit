using System.Collections.Generic;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Arch.Analyzer
{
    /// <summary>
    /// Leitura de um arch-rules.yaml TRIVIAL, só para o spike da Fase 0 provar que o parser
    /// YAML (YamlDotNet, fundido via ILRepack /internalize — ADR-001 D1) funciona dentro do
    /// analyzer lido a partir de AdditionalFiles. Não é o parser real: schema completo,
    /// validação de linha/coluna, `extends`/merge e a faixa ARCH9xxx de erros do motor são
    /// escopo de Arch.Config na Fase 1A (ver ADR-001 D4 e PLANO-implementacao.md, Fase 1).
    ///
    /// Formato aceito neste spike:
    /// <code>
    /// schema: arch-rules/v1
    /// forbiddenSuffix: Repository
    /// </code>
    /// </summary>
    internal static class SpikeYamlConfig
    {
        /// <summary>
        /// Extrai o valor de <c>forbiddenSuffix</c>. Retorna null se a chave não existir ou o
        /// YAML não parsear — o chamador decide o que fazer (no spike: nenhuma ação é
        /// registrada, mesma política de "saída rápida" do ADR-001 §5 para config ausente).
        /// </summary>
        internal static string ReadForbiddenSuffix(string yamlText)
        {
            if (string.IsNullOrWhiteSpace(yamlText))
            {
                return null;
            }

            var deserializer = new DeserializerBuilder().Build();

            Dictionary<object, object> root;
            try
            {
                root = deserializer.Deserialize<Dictionary<object, object>>(yamlText);
            }
            catch (YamlException)
            {
                return null;
            }

            if (root == null || !root.TryGetValue("forbiddenSuffix", out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }
    }
}
