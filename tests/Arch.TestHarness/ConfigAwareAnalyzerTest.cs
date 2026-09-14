using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Arch.TestHarness
{
    /// <summary>
    /// Variante reutilizável de <see cref="CSharpAnalyzerTest{TAnalyzer, TVerifier}"/> que
    /// neutraliza uma armadilha real de
    /// <c>Microsoft.CodeAnalysis.CSharp.Analyzer.Testing</c> 1.1.4 (Roslyn 4.8.0), descoberta
    /// no spike da Fase 0 (PLANO-implementacao.md) e originalmente isolada em
    /// <c>tests/Arch.Analyzer.Tests/AnalyzerConfigFilesTests.cs</c> (<c>ConfigAwareAnalyzerTest</c>
    /// não genérica). Esta classe extrai o mesmo padrão para ser consumida por qualquer
    /// projeto de teste de analyzer do repositório (Arch.Config.Tests, Arch.Analyzer.Tests e
    /// os futuros harnesses de tipo de regra na Fase 2).
    ///
    /// <para><b>A armadilha:</b> <c>AnalyzerTest&lt;TVerifier&gt;.ApplyCompilationOptions</c> é
    /// chamado por <c>RunAsync()</c> depois de <c>SolutionTransforms</c> e força
    /// <c>CompilationOptions.SpecificDiagnosticOptions[id] = ReportDiagnostic.Default</c> para
    /// TODO ID presente em <c>DiagnosticAnalyzer.SupportedDiagnostics</c> que esteja habilitado
    /// por padrão. Nesta versão do Roslyn (4.8.0), uma entrada em
    /// <c>SpecificDiagnosticOptions</c> — mesmo com o valor sentinela <c>Default</c> — tem
    /// precedência sobre o <c>SyntaxTreeOptionsProvider</c> (a fonte que
    /// <c>TestState.AnalyzerConfigFiles</c>/<c>.editorconfig</c>/<c>.globalconfig</c>
    /// alimentam), e portanto ANULA silenciosamente qualquer escalonamento de severidade vindo
    /// de config dentro do harness — independente de a config chegar via
    /// <c>TestState.AnalyzerConfigFiles</c> (plano A) ou via
    /// <c>SolutionTransforms</c> + <c>Solution.AddAnalyzerConfigDocument</c> (plano B), já que
    /// ambos alimentam o mesmo <see cref="Project"/> e é o
    /// <c>ApplyCompilationOptions</c> da classe base, chamado depois de qualquer um dos dois,
    /// quem neutraliza o efeito.</para>
    ///
    /// <para><b>O contorno:</b> esta classe sobrescreve
    /// <c>ApplyCompilationOptions</c> e remove, para cada ID em
    /// <see cref="DiagnosticAnalyzer.SupportedDiagnostics"/> do analyzer sob teste, a entrada
    /// forçada em <c>SpecificDiagnosticOptions</c> — restaurando o
    /// <c>SyntaxTreeOptionsProvider</c> (e portanto o .editorconfig/.globalconfig de teste)
    /// como fonte de severidade efetiva.</para>
    ///
    /// <para><b>Importante:</b> isto é uma peculiaridade do HARNESS de teste desta versão do
    /// pacote de testing, não do analyzer real — a prova de ponta a ponta com
    /// <c>dotnet build</c> de verdade (<c>tests/Arch.Spike.Consumer</c>) já mostrou o
    /// .editorconfig funcionando sem nenhum workaround; é assim que um consumidor real da lib
    /// vai usá-la. Use esta classe apenas quando o teste precisar exercitar escalonamento de
    /// severidade via <c>AnalyzerConfigFiles</c>/.editorconfig/.globalconfig dentro do
    /// harness — para os demais casos, <see cref="CSharpAnalyzerTest{TAnalyzer, TVerifier}"/>
    /// direto é suficiente.</para>
    /// </summary>
    /// <typeparam name="TAnalyzer">O <see cref="DiagnosticAnalyzer"/> sob teste.</typeparam>
    /// <typeparam name="TVerifier">O verificador de resultado (normalmente
    /// <c>Microsoft.CodeAnalysis.Testing.Verifiers.DefaultVerifier</c>).</typeparam>
    public class ConfigAwareAnalyzerTest<TAnalyzer, TVerifier> : CSharpAnalyzerTest<TAnalyzer, TVerifier>
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TVerifier : IVerifier, new()
    {
        protected override Project ApplyCompilationOptions(Project project)
        {
            var configured = base.ApplyCompilationOptions(project);
            var options = configured.CompilationOptions!.SpecificDiagnosticOptions;

            foreach (var descriptor in new TAnalyzer().SupportedDiagnostics)
            {
                options = options.Remove(descriptor.Id);
            }

            return configured.WithCompilationOptions(
                configured.CompilationOptions.WithSpecificDiagnosticOptions(options));
        }
    }
}
