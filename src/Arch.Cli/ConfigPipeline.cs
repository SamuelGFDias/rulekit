using System.Collections.Generic;
using Arch.Config;

namespace Arch.Cli
{
    /// <summary>
    /// Pipeline compartilhado por `arch-rules gen` e `arch-rules validate`: resolve `extends` a
    /// partir do disco (<see cref="ExtendsResolver"/>), parseia com
    /// <see cref="ArchConfigParser.Parse(IReadOnlyList{string})"/>, e roda as duas validações que
    /// o parser puro de Arch.Config não faz — slot fora do pool/duplicado
    /// (<see cref="SlotValidator"/>) e severidade fora do vocabulário do AnalyzerConfig
    /// (<see cref="SeverityResolver"/>).
    ///
    /// Erro bloqueante em qualquer etapa (extends malformado/cíclico, schema `IsBlocking = true`,
    /// slot inválido, severidade inválida) impede a geração de qualquer arquivo — a decisão de
    /// "sem gerar nada" é do chamador (<see cref="GenCommand"/>/<see cref="ValidateCommand"/>),
    /// que consulta <see cref="Result.Success"/>.
    /// </summary>
    public static class ConfigPipeline
    {
        public sealed class Result
        {
            public ArchConfig Config { get; }
            public IReadOnlyDictionary<string, string> ResolvedSeverityByRuleId { get; }
            public IReadOnlyList<CliDiagnostic> BlockingErrors { get; }
            public IReadOnlyList<CliDiagnostic> Warnings { get; }

            public bool Success => BlockingErrors.Count == 0;

            public Result(
                ArchConfig config,
                IReadOnlyDictionary<string, string> resolvedSeverityByRuleId,
                IReadOnlyList<CliDiagnostic> blockingErrors,
                IReadOnlyList<CliDiagnostic> warnings)
            {
                Config = config;
                ResolvedSeverityByRuleId = resolvedSeverityByRuleId ?? new Dictionary<string, string>();
                BlockingErrors = blockingErrors;
                Warnings = warnings;
            }
        }

        public static Result Run(string yamlPath)
        {
            var blocking = new List<CliDiagnostic>();
            var warnings = new List<CliDiagnostic>();

            IReadOnlyList<string> orderedTexts;
            try
            {
                orderedTexts = ExtendsResolver.ResolveOrderedYamlTexts(yamlPath);
            }
            catch (ExtendsResolutionException ex)
            {
                blocking.Add(new CliDiagnostic("CLI-EXTENDS", ex.Message, 0, 0));
                return new Result(null, null, blocking, warnings);
            }

            var parseResult = ArchConfigParser.Parse(orderedTexts);

            foreach (var error in parseResult.Errors)
            {
                var diagnostic = CliDiagnostic.FromSchemaError(error);
                if (error.IsBlocking)
                {
                    blocking.Add(diagnostic);
                }
                else
                {
                    warnings.Add(diagnostic);
                }
            }

            if (parseResult.Config == null)
            {
                return new Result(null, null, blocking, warnings);
            }

            var config = parseResult.Config;

            blocking.AddRange(SlotValidator.Validate(config.Rules));

            var resolvedSeverities = SeverityResolver.Resolve(config, out var severityErrors);
            blocking.AddRange(severityErrors);

            return new Result(config, resolvedSeverities, blocking, warnings);
        }
    }
}
