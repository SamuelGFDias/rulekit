using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using YamlDotNet.Serialization;

namespace Arch.Spike.ControlAnalyzer
{
    /// <summary>
    /// Analyzer de CONTROLE do spike da Fase 0 — não faz parte da lib. Único propósito:
    /// referenciar uma versão de YamlDotNet DIFERENTE da que Arch.Analyzer funde/internaliza,
    /// SEM internalizar (o YamlDotNet.dll desta versão é distribuído como DLL própria, normal,
    /// dentro de analyzers/dotnet/cs deste pacote de controle). Quando o projeto consumidor
    /// referencia os dois pacotes (Arch.Analyzer.Spike + Arch.Spike.ControlAnalyzer) ao mesmo
    /// tempo, prova a tese central do ADR-001 sobre roslyn#41421: como Arch.Analyzer não expõe
    /// mais nenhuma referência pública a YamlDotNet (foi internalizada), não existem DUAS
    /// cópias públicas conflitantes do mesmo assembly no host do compilador — só a cópia do
    /// controle é pública, e o build não quebra com FileLoadException.
    ///
    /// A regra em si (CTRL0001) é decorativa: só existe para provar que ESTE analyzer também
    /// carrega e funciona (ou seja, o build não falhou silenciosamente nem o CTRL0001 sumiu
    /// como AD0001) mesmo usando uma API do YamlDotNet 6.x, incompatível na superfície com a
    /// 13.x usada por Arch.Analyzer.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ControlAnalyzer : DiagnosticAnalyzer
    {
        internal static readonly DiagnosticDescriptor Ctrl0001 = new DiagnosticDescriptor(
            id: "CTRL0001",
            title: "Analyzer de controle carregado (YamlDotNet em versão distinta, não internalizada)",
            messageFormat: "Analyzer de controle ativo — YamlDotNet {0} resolvido sem conflito ao lado de Arch.Analyzer",
            category: "Spike",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Ver ADR-001 §1/roslyn#41421 e PLANO-implementacao.md, Fase 0, gate 'projeto de controle com YamlDotNet de outra versão continua buildando'.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Ctrl0001);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            // Toca a API pública do YamlDotNet desta versão de propósito, para que o
            // assembly YamlDotNet.dll (versão de controle) seja de fato carregado e
            // executado no processo do compilador, não só referenciado em disco.
            var deserializer = new DeserializerBuilder().Build();
            var parsed = deserializer.Deserialize<object>("value: ok");
            var yamlVersionMarker = parsed != null ? "ok" : "vazio";

            context.RegisterCompilationEndAction(endContext =>
            {
                var location = endContext.Compilation.SyntaxTrees.FirstOrDefaultLocation();
                endContext.ReportDiagnostic(Diagnostic.Create(Ctrl0001, location, yamlVersionMarker));
            });
        }
    }

    internal static class CompilationExtensions
    {
        internal static Location FirstOrDefaultLocation(this System.Collections.Generic.IEnumerable<Microsoft.CodeAnalysis.SyntaxTree> trees)
        {
            foreach (var tree in trees)
            {
                return Location.Create(tree, Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(0, 0));
            }
            return Location.None;
        }
    }
}
