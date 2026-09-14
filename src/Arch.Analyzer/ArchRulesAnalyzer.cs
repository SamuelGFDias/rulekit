using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Arch.Analyzer
{
    /// <summary>
    /// Spike de viabilidade da Fase 0 (ADR-001, PLANO-implementacao.md). Prova, de ponta a
    /// ponta, o mecanismo de pool de slots (ADR-001 §3 Opção C):
    ///
    ///  - ARCH0001..ARCH0003 são pré-declarados em <see cref="SupportedDiagnostics"/>, lidos
    ///    pelo Roslyn sem qualquer contexto de compilação (ADR-001 §1);
    ///  - a configuração real (aqui, um YAML trivial com `forbiddenSuffix`) só é lida dentro
    ///    de <see cref="OnCompilationStart"/>, via <c>context.Options.AdditionalFiles</c>;
    ///  - o slot ARCH0001 é o único artefato que o compilador/.editorconfig/#pragma enxergam;
    ///    o valor de negócio (o sufixo proibido) só aparece na mensagem.
    ///
    /// Não implementa IRuleEvaluator/ILayerResolver (contratos congelados em
    /// <c>Contracts/</c> para a Fase 2) — a regra aqui é fixa (sufixo → ARCH0001) porque o
    /// objetivo deste spike é validar o mecanismo de slots, não o motor de regras completo.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ArchRulesAnalyzer : DiagnosticAnalyzer
    {
        private const string ConfigFileName = "arch-rules.yaml";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ArchDiagnostics.All;

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var additionalFile = context.Options.AdditionalFiles.FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file.Path), ConfigFileName, StringComparison.OrdinalIgnoreCase));

            if (additionalFile == null)
            {
                // Saída rápida (ADR-001 §5): sem arch-rules.yaml, nenhuma ação é registrada —
                // custo próximo de zero para quem instalou o pacote e ainda não configurou.
                return;
            }

            var yamlText = additionalFile.GetText(context.CancellationToken)?.ToString();
            var forbiddenSuffix = SpikeYamlConfig.ReadForbiddenSuffix(yamlText);

            if (string.IsNullOrWhiteSpace(forbiddenSuffix))
            {
                return;
            }

            context.RegisterOperationAction(
                operationContext => AnalyzeInvocation(operationContext, forbiddenSuffix),
                OperationKind.Invocation);
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context, string forbiddenSuffix)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var containingType = invocation.TargetMethod?.ContainingType;

            if (containingType == null)
            {
                return;
            }

            if (!containingType.Name.EndsWith(forbiddenSuffix, StringComparison.Ordinal))
            {
                return;
            }

            var diagnostic = Diagnostic.Create(
                ArchDiagnostics.Arch0001,
                invocation.Syntax.GetLocation(),
                containingType.Name,
                forbiddenSuffix);

            context.ReportDiagnostic(diagnostic);
        }
    }
}
