using System;
using System.Collections.Generic;
using System.IO;
using Arch.Config;

namespace Arch.Cli
{
    /// <summary>
    /// Pipeline compartilhado por `arch-rules gen` e `arch-rules validate`: resolve `extends` a
    /// partir do disco (<see cref="ExtendsResolver"/>, adaptador de
    /// <see cref="ExtendsChainResolver"/>), parseia com
    /// <see cref="ArchConfigParser.Parse(IReadOnlyList{string})"/>, e roda as duas validações que
    /// o parser puro de Arch.Config não faz — slot fora do pool/duplicado
    /// (<see cref="SlotValidator"/>) e severidade fora do vocabulário do AnalyzerConfig
    /// (<see cref="SeverityResolver"/>).
    ///
    /// Erro bloqueante em qualquer etapa (extends ausente/cíclico/profundo demais, schema `IsBlocking = true`,
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

            /// <summary>
            /// Um aviso que, por política, faz o comando falhar: ARCH9008 (regra malformada é sempre erro
            /// do usuário) apenas em <c>validate</c>, e qualquer aviso com <c>--strict</c>. ARCH9003
            /// (tipo desconhecido — pode ser de uma versão futura da lib) segue só como aviso.
            /// </summary>
            public static bool IsFatalWarning(CliDiagnostic warning, bool strict, bool failOnMalformedRule)
            {
                return strict || (failOnMalformedRule && warning.Code == "ARCH9008");
            }

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

            ExtendsChainResult chain;
            try
            {
                chain = ExtendsResolver.Resolve(yamlPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Arquivo existente mas ilegível (permissão, diretório no lugar de arquivo): erro de
                // configuração limpo em vez de exceção (issue #4).
                blocking.Add(new CliDiagnostic(
                    "ARCH9001",
                    $"Não foi possível ler a configuração ou um arquivo de 'extends' ({ex.GetType().Name}): {ex.Message}",
                    0,
                    0));
                return new Result(null, null, blocking, warnings);
            }

            if (chain.Problems.Count > 0)
            {
                // Política do CLI: qualquer problema de cadeia é bloqueante (gerar um .globalconfig
                // de uma política incompleta seria pior que não gerar nada).
                blocking.AddRange(ExtendsResolver.ToDiagnostics(chain.Problems));
                return new Result(null, null, blocking, warnings);
            }

            var parseResult = ArchConfigParser.Parse(chain.Texts);

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
