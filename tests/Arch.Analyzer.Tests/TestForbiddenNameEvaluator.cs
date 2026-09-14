using System.Collections.Generic;
using Arch.Analyzer.Contracts;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Avaliador DE TESTE, deliberadamente fora de <c>src/Arch.Analyzer</c>: a Fase 1B entrega o
    /// motor, não os tipos de regra (Fase 2). Ele existe só para provar que, QUANDO existe um
    /// avaliador registrado, o pipeline inteiro do motor funciona ponta a ponta — parse → cache →
    /// resolução de camadas → roteamento por `type` → <see cref="IRuleEvaluator.RegisterActions"/> →
    /// diagnóstico reportado NO SLOT CERTO.
    ///
    /// Semântica (um `forbidden-call` reduzido ao essencial): reporta toda invocação de um método
    /// cujo tipo declarante pertence à camada <c>to</c>, quando feita de dentro de um tipo da camada
    /// <c>from</c> (`from` ausente = qualquer origem). Usa <see cref="ILayerResolver"/> dos dois
    /// lados — é isso que faz o teste exercitar o resolvedor real, e não uma checagem de nome.
    /// </summary>
    internal sealed class TestForbiddenNameEvaluator : IRuleEvaluator
    {
        internal const string TypeName = "test-forbidden-call";

        public string RuleType => TypeName;

        public void RegisterActions(
            CompilationStartAnalysisContext context,
            RuleDefinition rule,
            ILayerResolver layerResolver,
            DiagnosticDescriptor slotDescriptor)
        {
            string fromLayer;
            rule.Extra.TryGetValue("from", out fromLayer);

            string toLayer;
            if (!rule.Extra.TryGetValue("to", out toLayer) || string.IsNullOrEmpty(toLayer))
            {
                return;
            }

            context.RegisterOperationAction(
                operationContext =>
                {
                    var invocation = (IInvocationOperation)operationContext.Operation;
                    var target = invocation.TargetMethod?.ContainingType;
                    if (target == null || !layerResolver.IsInLayer(target, toLayer))
                    {
                        return;
                    }

                    if (!string.IsNullOrEmpty(fromLayer))
                    {
                        var caller = operationContext.ContainingSymbol?.ContainingType;
                        if (caller == null || !layerResolver.IsInLayer(caller, fromLayer))
                        {
                            return;
                        }
                    }

                    // O descriptor vem pronto do pool (nunca construído aqui — ADR-001 §1); a
                    // mensagem é o único argumento, porque o descriptor não pode conhecer o texto
                    // da política antes de o YAML existir.
                    operationContext.ReportDiagnostic(Diagnostic.Create(
                        slotDescriptor,
                        invocation.Syntax.GetLocation(),
                        BuildMessage(rule, toLayer, target.Name)));
                },
                OperationKind.Invocation);
        }

        internal static string BuildMessage(RuleDefinition rule, string toLayer, string targetTypeName)
            => rule.Id + ": acesso proibido a '" + targetTypeName + "' (camada " + toLayer + ")";

        internal static IReadOnlyDictionary<string, string> Extra(string from, string to)
        {
            var extra = new Dictionary<string, string>();
            if (from != null)
            {
                extra["from"] = from;
            }

            extra["to"] = to;
            return extra;
        }
    }
}
