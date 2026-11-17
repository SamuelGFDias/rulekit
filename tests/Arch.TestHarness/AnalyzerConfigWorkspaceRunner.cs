using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Arch.TestHarness
{
    /// <summary>
    /// Roda um analyzer sobre um projeto montado num <see cref="AdhocWorkspace"/> com fontes,
    /// AdditionalFiles e ANALYZER CONFIG FILES (<c>.editorconfig</c>/<c>.globalconfig</c>) e devolve
    /// TODOS os diagnósticos do analyzer, para asserções livres (<c>Assert.Contains</c>, mensagem,
    /// localização). Complementa <see cref="ConfigAwareAnalyzerTest{TAnalyzer, TVerifier}"/>, que
    /// exige declarar a lista exata de diagnósticos esperados.
    ///
    /// <para>Por que o workspace e não <c>CSharpCompilation.Create</c>: só o workspace transforma
    /// AnalyzerConfig documents no <see cref="CompilationOptions.SyntaxTreeOptionsProvider"/> da
    /// compilação — o mesmo lugar onde um <c>.globalconfig</c> aterrissa num <c>dotnet build</c> real
    /// (<c>/analyzerconfig:</c>). Aqui não há o <c>ApplyCompilationOptions</c> do pacote de testing
    /// (ver <see cref="ConfigAwareAnalyzerTest{TAnalyzer, TVerifier}"/>), então nada é injetado em
    /// <c>SpecificDiagnosticOptions</c> além do que o chamador passar.</para>
    /// </summary>
    public static class AnalyzerConfigWorkspaceRunner
    {
        /// <summary>Diretório virtual do projeto; caminhos relativos são ancorados nele.</summary>
        public const string ProjectDirectory = "/0/";

        private static readonly Lazy<ImmutableArray<MetadataReference>> DefaultReferences =
            new Lazy<ImmutableArray<MetadataReference>>(LoadRuntimeReferences);

        /// <param name="analyzer">Analyzer sob teste.</param>
        /// <param name="sources">Fontes C# (caminho, conteúdo).</param>
        /// <param name="additionalFiles">AdditionalFiles (caminho, conteúdo).</param>
        /// <param name="analyzerConfigFiles">.editorconfig/.globalconfig (caminho absoluto, conteúdo).</param>
        /// <param name="specificDiagnosticOptions">Equivalente a /warnaserror:ID, /nowarn:ID, ruleset.</param>
        /// <param name="references">Referências; padrão: assemblies System.* do runtime de teste.</param>
        public static async Task<ImmutableArray<Diagnostic>> RunAsync(
            DiagnosticAnalyzer analyzer,
            IEnumerable<(string Path, string Content)> sources,
            IEnumerable<(string Path, string Content)> additionalFiles = null,
            IEnumerable<(string Path, string Content)> analyzerConfigFiles = null,
            IReadOnlyDictionary<string, ReportDiagnostic> specificDiagnosticOptions = null,
            IEnumerable<MetadataReference> references = null,
            CancellationToken cancellationToken = default)
        {
            using var workspace = new AdhocWorkspace();

            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            if (specificDiagnosticOptions != null)
            {
                options = options.WithSpecificDiagnosticOptions(specificDiagnosticOptions);
            }

            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Default,
                name: "HarnessProject",
                assemblyName: "HarnessProject",
                language: LanguageNames.CSharp,
                filePath: ProjectDirectory + "HarnessProject.csproj",
                compilationOptions: options,
                metadataReferences: references ?? DefaultReferences.Value));

            foreach (var (path, content) in sources ?? Enumerable.Empty<(string, string)>())
            {
                var fullPath = Anchor(path);
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(projectId), System.IO.Path.GetFileName(fullPath), SourceText.From(content), filePath: fullPath);
            }

            foreach (var (path, content) in additionalFiles ?? Enumerable.Empty<(string, string)>())
            {
                var fullPath = Anchor(path);
                solution = solution.AddAdditionalDocument(
                    DocumentId.CreateNewId(projectId), System.IO.Path.GetFileName(fullPath), SourceText.From(content), filePath: fullPath);
            }

            foreach (var (path, content) in analyzerConfigFiles ?? Enumerable.Empty<(string, string)>())
            {
                var fullPath = Anchor(path);
                solution = solution.AddAnalyzerConfigDocument(
                    DocumentId.CreateNewId(projectId), System.IO.Path.GetFileName(fullPath), SourceText.From(content), filePath: fullPath);
            }

            var project = solution.GetProject(projectId)!;
            var compilation = (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false))!;

            var compileErrors = compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            if (compileErrors.Length > 0)
            {
                throw new InvalidOperationException(
                    "O código de exemplo do teste não compila: " + string.Join(" | ", compileErrors.Select(e => e.ToString())));
            }

            return await compilation
                .WithAnalyzers(ImmutableArray.Create(analyzer), project.AnalyzerOptions)
                .GetAnalyzerDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        private static string Anchor(string path)
            => path.StartsWith("/", StringComparison.Ordinal) ? path : ProjectDirectory + path;

        private static ImmutableArray<MetadataReference> LoadRuntimeReferences()
        {
            var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;

            return trusted
                .Split(System.IO.Path.PathSeparator)
                .Where(path => path.Length > 0)
                .Where(path =>
                {
                    var name = System.IO.Path.GetFileName(path);
                    return name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "netstandard.dll", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "mscorlib.dll", StringComparison.OrdinalIgnoreCase);
                })
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();
        }
    }
}
