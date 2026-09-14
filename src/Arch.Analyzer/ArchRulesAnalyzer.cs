using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Arch.Analyzer
{
    /// <summary>
    /// O motor. Não contém nenhuma regra de arquitetura: lê a política do consumidor de
    /// <c>arch-rules.yaml</c> (AdditionalFiles), resolve camadas e roteia cada regra ao
    /// <see cref="IRuleEvaluator"/> do seu `type`, sempre reportando através de um slot
    /// pré-declarado do pool (ADR-001 §3, Opção C).
    ///
    /// <para><b>Ordem das coisas (ADR-001 §1 e §5):</b> <see cref="SupportedDiagnostics"/> é lida sem
    /// contexto de compilação e cacheada pela sessão da IDE inteira — por isso ela devolve o pool
    /// fixo de <see cref="ArchDiagnostics"/>, e nunca algo derivado do YAML. O YAML só existe dentro
    /// de <c>RegisterCompilationStartAction</c>, onde <c>context.Options.AdditionalFiles</c> está
    /// disponível.</para>
    ///
    /// <para><b>Custo por compilação:</b> sem <c>arch-rules.yaml</c> nos AdditionalFiles nenhuma ação
    /// é registrada (saída rápida); com ele, o parse acontece uma vez por INSTÂNCIA de
    /// <see cref="SourceText"/> — a IDE reaproveita essa instância entre compilações enquanto o
    /// arquivo não muda, então digitar código não reparseia a política.</para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ArchRulesAnalyzer : DiagnosticAnalyzer
    {
        private const string ConfigFileName = "arch-rules.yaml";

        private readonly IReadOnlyDictionary<string, IRuleEvaluator> _evaluators;

        /// <summary>
        /// Fronteira com o parser de <c>Arch.Config</c>. É um delegate (e não a chamada direta) só
        /// para que os testes do pipeline possam injetar uma configuração determinística sem
        /// depender da trilha 1A; em produção aponta sempre para
        /// <see cref="ArchConfigParser.Parse(string)"/>.
        /// </summary>
        private readonly Func<string, ArchConfigParseResult> _parse;

        /// <summary>
        /// Cache do resultado do parse, chaveado pela INSTÂNCIA do <see cref="SourceText"/> do
        /// AdditionalFile (ADR-001 §5). Deliberadamente NÃO por caminho (não reflete o conteúdo) e
        /// NÃO por <c>SourceText.GetChecksum()</c> — que devolve <c>ImmutableArray&lt;byte&gt;</c>,
        /// um struct, inválido como chave de <see cref="ConditionalWeakTable{TKey,TValue}"/> (exige
        /// <c>class</c> e compara por referência) e desaconselhado para igualdade pela própria
        /// documentação (BOM/encoding mudam o checksum de conteúdos idênticos).
        ///
        /// <para>A tabela é campo de instância, não estática: o host reaproveita a instância do
        /// analyzer durante toda a sessão (é o que dá o efeito de cache entre compilações), e assim
        /// dois analyzers configurados de formas diferentes nunca compartilham resultado. Sendo
        /// "weak" nas chaves, a entrada morre junto com o <see cref="SourceText"/> — nada segura
        /// memória depois que a IDE descarta o texto.</para>
        /// </summary>
        private readonly ConditionalWeakTable<SourceText, CachedConfig> _configCache =
            new ConditionalWeakTable<SourceText, CachedConfig>();

        private readonly ConditionalWeakTable<SourceText, CachedConfig>.CreateValueCallback _createCachedConfig;

        /// <summary>Construtor usado pelo host do compilador (Roslyn exige um sem parâmetros).</summary>
        public ArchRulesAnalyzer()
            : this(RuleEvaluatorRegistry.Default, null)
        {
        }

        internal ArchRulesAnalyzer(
            IReadOnlyDictionary<string, IRuleEvaluator> evaluators,
            Func<string, ArchConfigParseResult> parse)
        {
            _evaluators = evaluators ?? RuleEvaluatorRegistry.Empty;
            _parse = parse ?? ArchConfigParser.Parse;
            _createCachedConfig = sourceText => Parse(sourceText);
        }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        {
            get { return ArchDiagnostics.All; }
        }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var additionalFile = FindConfigFile(context.Options.AdditionalFiles);
            if (additionalFile == null)
            {
                // Saída rápida (ADR-001 §5): quem instalou o pacote e ainda não configurou nada não
                // paga por ação nenhuma registrada.
                return;
            }

            var sourceText = additionalFile.GetText(context.CancellationToken);
            if (sourceText == null)
            {
                return;
            }

            var cached = _configCache.GetValue(sourceText, _createCachedConfig);

            var engineDiagnostics = new List<Diagnostic>();

            foreach (var error in cached.Errors)
            {
                if (error == null)
                {
                    continue;
                }

                engineDiagnostics.Add(CreateEngineDiagnostic(additionalFile, sourceText, error));
            }

            if (cached.HasBlockingError || cached.Config == null)
            {
                // Config inválida invalida tudo: nenhuma ação de regra é registrada, para não
                // transformar um YAML malformado numa avalanche de falsos positivos (ADR-001 D4).
                ReportAtCompilationEnd(context, engineDiagnostics);
                return;
            }

            ImmutableArray<string> unresolvedTypeNames;
            var layerResolver = LayerResolver.Create(cached.Config, context.Compilation, out unresolvedTypeNames);

            foreach (var name in unresolvedTypeNames)
            {
                // Uma vez por NOME, no compilation-start — nunca por símbolo (ADR-001 §5).
                engineDiagnostics.Add(CreateEngineDiagnostic(
                    additionalFile,
                    sourceText,
                    ArchDiagnostics.Arch9004,
                    "Tipo '" + name + "' referenciado em arch-rules.yaml não foi encontrado nas referências desta " +
                    "compilação; o critério que o usa foi ignorado."));
            }

            var slotsInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in cached.Config.Rules)
            {
                if (rule == null || !rule.Enabled)
                {
                    // `enabled: false` desliga a regra preservando rastreabilidade (ADR-001 D2).
                    continue;
                }

                DiagnosticDescriptor slotDescriptor;
                if (!ArchDiagnostics.TryGetSlot(rule.Slot, out slotDescriptor))
                {
                    engineDiagnostics.Add(CreateEngineDiagnostic(
                        additionalFile,
                        sourceText,
                        ArchDiagnostics.Arch9002,
                        "Regra '" + rule.Id + "' ignorada: slot '" + rule.Slot + "' está fora do pool pré-declarado " +
                        "(ARCH0001..ARCH" + ArchDiagnostics.SlotCount.ToString("D4", CultureInfo.InvariantCulture) + ")."));
                    continue;
                }

                if (!slotsInUse.Add(slotDescriptor.Id))
                {
                    engineDiagnostics.Add(CreateEngineDiagnostic(
                        additionalFile,
                        sourceText,
                        ArchDiagnostics.Arch9002,
                        "Regra '" + rule.Id + "' ignorada: o slot '" + slotDescriptor.Id + "' já está vinculado a " +
                        "outra regra. Dois IDs de negócio no mesmo slot tornariam impossível dar override de " +
                        "severidade a um sem afetar o outro."));
                    continue;
                }

                IRuleEvaluator evaluator;
                if (!_evaluators.TryGetValue(rule.Type, out evaluator) || evaluator == null)
                {
                    // ADR-001 D4 (MINOR aditivo): tipo desconhecido é ARCH9003 e não derruba o resto.
                    engineDiagnostics.Add(CreateEngineDiagnostic(
                        additionalFile,
                        sourceText,
                        ArchDiagnostics.Arch9003,
                        "Regra '" + rule.Id + "' ignorada: tipo de regra '" + rule.Type + "' não é suportado por esta " +
                        "versão da lib."));
                    continue;
                }

                evaluator.RegisterActions(context, rule, layerResolver, slotDescriptor);
            }

            ReportAtCompilationEnd(context, engineDiagnostics);
        }

        private static AdditionalText FindConfigFile(ImmutableArray<AdditionalText> additionalFiles)
        {
            // Sem LINQ: este trecho roda em toda compilação, inclusive nas que não têm config.
            for (var i = 0; i < additionalFiles.Length; i++)
            {
                var file = additionalFiles[i];
                if (file != null
                    && string.Equals(Path.GetFileName(file.Path), ConfigFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }

            return null;
        }

        private CachedConfig Parse(SourceText sourceText)
        {
            try
            {
                var result = _parse(sourceText.ToString());

                if (result == null)
                {
                    return CachedConfig.Blocking(
                        "O parser de arch-rules.yaml não devolveu resultado algum (retorno nulo).");
                }

                return new CachedConfig(result.Config, result.Errors);
            }
            catch (OperationCanceledException)
            {
                // Cancelamento é fluxo normal na IDE (o usuário digitou de novo): nunca vira
                // diagnóstico, e nunca pode ser cacheado como se fosse resultado.
                throw;
            }
            catch (Exception ex)
            {
                // Uma exceção atravessando a fronteira do analyzer vira AD0001 — mensagem inútil
                // para quem só quer saber que o YAML está quebrado. Converte-se no erro de motor
                // correspondente, que é acionável e silenciável.
                return CachedConfig.Blocking(
                    "Falha inesperada ao interpretar arch-rules.yaml: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // RS1013 ("este compilation-start só registra um compilation-end; use
        // RegisterCompilationAction") é um falso positivo aqui, e a análise dele é lexical — ela
        // enxerga só este método auxiliar. O compilation-start REAL (OnCompilationStart) também
        // registra as ações de cada regra, via IRuleEvaluator.RegisterActions. Trocar por
        // RegisterCompilationAction também não seria possível: o contexto de compilation-start não
        // expõe esse registro, e os erros de motor precisam ser calculados exatamente ali, onde a
        // config já foi resolvida uma única vez. A supressão cobre a assinatura inteira porque é no
        // parâmetro `context` que o diagnóstico é reportado.
#pragma warning disable RS1013
        private static void ReportAtCompilationEnd(CompilationStartAnalysisContext context, List<Diagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                return;
            }

            // Os diagnósticos já estão prontos (criados uma única vez no compilation-start); o
            // compilation-end só existe porque o contexto de start não expõe ReportDiagnostic.
            context.RegisterCompilationEndAction(endContext =>
            {
                foreach (var diagnostic in diagnostics)
                {
                    endContext.ReportDiagnostic(diagnostic);
                }
            });
        }
#pragma warning restore RS1013

        private static Diagnostic CreateEngineDiagnostic(
            AdditionalText file,
            SourceText sourceText,
            SchemaValidationError error)
        {
            var descriptor = ArchDiagnostics.GetEngineDescriptorOrFallback(error.Code, error.IsBlocking);

            // Código desconhecido (parser mais novo que o analyzer) não pode ser reportado como ele
            // mesmo — não está em SupportedDiagnostics — mas não pode sumir da mensagem também.
            var message = ArchDiagnostics.IsKnownEngineCode(error.Code)
                ? error.Message
                : error.Code + ": " + error.Message;

            return Diagnostic.Create(
                descriptor,
                CreateLocation(file, sourceText, error.Line, error.Column),
                message);
        }

        private static Diagnostic CreateEngineDiagnostic(
            AdditionalText file,
            SourceText sourceText,
            DiagnosticDescriptor descriptor,
            string message)
        {
            return Diagnostic.Create(descriptor, CreateLocation(file, sourceText, 0, 0), message);
        }

        /// <summary>
        /// Localização dentro do próprio <c>arch-rules.yaml</c> — um erro de configuração precisa
        /// abrir o arquivo de configuração, não um .cs qualquer (ADR-001 D4: "apontando
        /// linha/coluna"). Linha/coluna inválidas ou ausentes caem para o início do arquivo.
        /// </summary>
        private static Location CreateLocation(AdditionalText file, SourceText sourceText, int line, int column)
        {
            var lines = sourceText.Lines;
            TextSpan span;

            if (line <= 0 || lines.Count == 0)
            {
                span = new TextSpan(0, 0);
            }
            else
            {
                var textLine = lines[Math.Min(line - 1, lines.Count - 1)];
                var start = textLine.Start + Math.Max(0, column - 1);
                start = Math.Min(start, textLine.End);
                span = TextSpan.FromBounds(start, textLine.End);
            }

            return Location.Create(file.Path, span, sourceText.Lines.GetLinePositionSpan(span));
        }

        /// <summary>
        /// O que fica no cache: a config resolvida MAIS os erros do parse. Guardar só a
        /// <see cref="ArchConfig"/> faria os erros não bloqueantes (ARCH9003 de uma regra, por
        /// exemplo) aparecerem na primeira compilação e sumirem em todas as seguintes, que acertam o
        /// cache — diagnóstico intermitente é pior que diagnóstico nenhum.
        /// </summary>
        private sealed class CachedConfig
        {
            internal readonly ArchConfig Config;
            internal readonly IReadOnlyList<SchemaValidationError> Errors;
            internal readonly bool HasBlockingError;

            internal CachedConfig(ArchConfig config, IReadOnlyList<SchemaValidationError> errors)
            {
                Config = config;
                Errors = errors ?? new SchemaValidationError[0];

                for (var i = 0; i < Errors.Count; i++)
                {
                    if (Errors[i] != null && Errors[i].IsBlocking)
                    {
                        HasBlockingError = true;
                        break;
                    }
                }
            }

            internal static CachedConfig Blocking(string message)
            {
                return new CachedConfig(
                    null,
                    new[] { new SchemaValidationError("ARCH9001", message, 0, 0, true) });
            }
        }
    }
}
