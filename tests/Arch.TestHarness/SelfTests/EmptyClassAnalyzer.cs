using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Arch.TestHarness.SelfTests
{
    /// <summary>
    /// Analyzer de brinquedo usado exclusivamente para autoverificar
    /// <see cref="ConfigAwareAnalyzerTest{TAnalyzer, TVerifier}"/> (ver
    /// <c>ConfigAwareAnalyzerTestSelfTests</c>). Não faz parte da lib real — vive só neste
    /// projeto de harness para provar que a classe base funciona sem depender de nenhum
    /// analyzer de <c>src/**</c>. Reporta <c>HARNESS001</c> em toda declaração de classe.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class EmptyClassAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "HARNESS001";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            title: "Declaração de classe encontrada (self-test)",
            messageFormat: "Classe '{0}' encontrada pelo analyzer de autoverificação do harness",
            category: "Arch.TestHarness.SelfTest",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeClassDeclaration, SyntaxKind.ClassDeclaration);
        }

        private static void AnalyzeClassDeclaration(SyntaxNodeAnalysisContext context)
        {
            var classDeclaration = (ClassDeclarationSyntax)context.Node;
            context.ReportDiagnostic(Diagnostic.Create(
                Rule,
                classDeclaration.Identifier.GetLocation(),
                classDeclaration.Identifier.Text));
        }
    }
}
