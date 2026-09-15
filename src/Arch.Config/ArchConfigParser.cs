using System;
using System.Collections.Generic;
using Arch.Config.Internal;

namespace Arch.Config
{
    /// <summary>
    /// Um erro de validação de schema (ADR-001 D4 — faixa ARCH9xxx é reservada para erros do
    /// próprio motor). Este tipo carrega a informação pura (código, mensagem, posição); a
    /// conversão para um Diagnostic real do Roslyn (com o DiagnosticDescriptor pré-declarado
    /// do slot ARCH9xxx correspondente) é responsabilidade de Arch.Analyzer, não deste projeto
    /// — Arch.Config não depende de Microsoft.CodeAnalysis (ver Arch.Config.Tests: "parser
    /// puro, sem Roslyn", PLANO-implementacao.md).
    /// </summary>
    public sealed class SchemaValidationError
    {
        /// <summary>Código do erro de motor, ex.: "ARCH9001" (schema inválido/major não
        /// suportada), "ARCH9003" (tipo de regra desconhecido), "ARCH9004" (nome não resolvido
        /// via GetTypeByMetadataName — reportado por Arch.Analyzer, não por este parser).</summary>
        public string Code { get; }

        public string Message { get; }

        /// <summary>Linha 1-based no texto YAML original onde o problema foi detectado.</summary>
        public int Line { get; }

        /// <summary>Coluna 1-based no texto YAML original onde o problema foi detectado.</summary>
        public int Column { get; }

        /// <summary>true quando o erro invalida o arquivo inteiro (ex.: ARCH9001 — major não
        /// suportada, YAML sintaticamente inválido); false quando é um aviso que não impede as
        /// demais regras de valerem (ex.: ARCH9003 — tipo de regra desconhecido).</summary>
        public bool IsBlocking { get; }

        public SchemaValidationError(string code, string message, int line, int column, bool isBlocking)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Line = line;
            Column = column;
            IsBlocking = isBlocking;
        }
    }

    /// <summary>
    /// Resultado de <see cref="ArchConfigParser.Parse"/>. <see cref="Config"/> é não-nulo
    /// quando o parsing produziu um resultado utilizável (mesmo que existam erros não
    /// bloqueantes em <see cref="Errors"/>, ex.: ARCH9003 em uma regra específica — as demais
    /// regras continuam valendo, ADR-001 D4). <see cref="Config"/> é nulo quando algum erro em
    /// <see cref="Errors"/> tem <see cref="SchemaValidationError.IsBlocking"/> true.
    /// </summary>
    public sealed class ArchConfigParseResult
    {
        public ArchConfig Config { get; }
        public IReadOnlyList<SchemaValidationError> Errors { get; }

        public ArchConfigParseResult(ArchConfig config, IReadOnlyList<SchemaValidationError> errors)
        {
            Config = config;
            Errors = errors ?? Array.Empty<SchemaValidationError>();
        }
    }

    /// <summary>
    /// Ponto de entrada único do parser YAML de Arch.Config (ADR-001 D1/D2/D4).
    /// </summary>
    public static class ArchConfigParser
    {
        /// <param name="yamlText">Conteúdo bruto do arquivo `arch-rules.yaml` (já lido do
        /// AdditionalFile pelo chamador — este método não faz I/O).</param>
        /// <remarks>
        /// Este método não resolve a chave `extends:` do YAML — resolver o caminho de um
        /// `extends` relativo ao diretório do arquivo é uma decisão de integração (I/O) que cabe
        /// a quem chama o parser (Fase de wiring em Arch.Analyzer), não a este parser puro. Um
        /// `extends:` presente no texto é validado apenas quanto à forma (lista de strings); o
        /// merge por precedência descrito em ADR-001 D2 é feito por
        /// <see cref="Parse(IReadOnlyList{string})"/>, que recebe os textos já resolvidos pelo
        /// chamador, em ordem crescente de precedência (o último vence um conflito).
        /// </remarks>
        public static ArchConfigParseResult Parse(string yamlText)
        {
            return Parse(new[] { yamlText });
        }

        /// <summary>
        /// Overload que já recebe os textos YAML de uma cadeia de `extends` resolvida
        /// pelo chamador (ex.: [conteúdo de arch-rules.base.yaml, conteúdo de arch-rules.yaml]),
        /// em ordem crescente de precedência — o último documento é o que declara `extends` dos
        /// anteriores e vence qualquer conflito (merge por nome/`id` em `layers`/`rules`,
        /// ADR-001 D2). Passar uma lista com um único elemento equivale a
        /// <see cref="Parse(string)"/>.
        /// </summary>
        /// <remarks>
        /// Público desde a Fase 3: além de <c>Arch.Config.Tests</c> (via
        /// <c>InternalsVisibleTo</c>, que continua valendo), <c>Arch.Cli</c>
        /// (<c>arch-rules gen</c>/<c>arch-rules validate</c>) é um consumidor de produção
        /// legítimo deste overload — ele resolve `extends` de verdade a partir do disco (I/O que,
        /// como documentado acima, é responsabilidade de quem chama o parser) e precisa passar a
        /// lista de textos já na ordem de precedência correta.
        /// </remarks>
        public static ArchConfigParseResult Parse(IReadOnlyList<string> yamlTextsInPrecedenceOrder)
        {
            if (yamlTextsInPrecedenceOrder == null || yamlTextsInPrecedenceOrder.Count == 0)
            {
                throw new ArgumentException("Ao menos um texto YAML é necessário.", nameof(yamlTextsInPrecedenceOrder));
            }

            var parsedDocuments = new List<ParsedDocument>(yamlTextsInPrecedenceOrder.Count);
            foreach (var yamlText in yamlTextsInPrecedenceOrder)
            {
                parsedDocuments.Add(ArchConfigDocumentParser.Parse(yamlText));
            }

            var merged = ArchConfigMerger.Merge(parsedDocuments);

            if (merged.HasBlockingError)
            {
                return new ArchConfigParseResult(null, merged.Errors);
            }

            var config = new ArchConfig(
                merged.Schema,
                merged.Layers,
                merged.Rules,
                merged.Severities,
                merged.DefaultSeverityName);

            return new ArchConfigParseResult(config, merged.Errors);
        }

        /// <summary>
        /// Extrai só a lista de <c>extends:</c> de um texto YAML, sem validar o restante do schema
        /// (a validação completa acontece em <see cref="Parse(IReadOnlyList{string})"/> depois que a
        /// cadeia inteira for resolvida). Usado por Arch.Analyzer para descobrir, ANTES de montar a
        /// lista de textos em ordem de precedência, quais outros AdditionalFiles precisa localizar
        /// (ele não pode ler do disco — RS1035). Retorna lista vazia se não houver <c>extends:</c>
        /// ou se o YAML for inválido demais para nem isso ser extraído (nesse caso o erro real
        /// aparece de qualquer forma quando o texto entrar em <c>Parse</c>).
        /// </summary>
        /// <remarks>
        /// Os caminhos são devolvidos EXATAMENTE como escritos no YAML (relativos ao diretório do
        /// arquivo que os declara, ADR-001 D2). Resolver esses caminhos é responsabilidade de quem
        /// chama — no analyzer, manipulação puramente textual de caminho contra
        /// <c>context.Options.AdditionalFiles</c>; no CLI, I/O de disco real
        /// (<c>Arch.Cli.ExtendsResolver</c>).
        /// </remarks>
        public static IReadOnlyList<string> PeekExtends(string yamlText)
        {
            return ArchConfigDocumentParser.PeekExtends(yamlText);
        }
    }
}
