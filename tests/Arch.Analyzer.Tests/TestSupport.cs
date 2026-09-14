using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// <see cref="AdditionalText"/> em memória. O ponto crítico para os testes de cache é que
    /// <see cref="GetText"/> devolve SEMPRE a mesma instância de <see cref="SourceText"/> recebida no
    /// construtor — é exatamente o que a IDE faz com um documento que não mudou, e é essa identidade
    /// de instância (não o caminho, não o checksum) que chaveia o cache do analyzer (ADR-001 §5).
    /// </summary>
    internal sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        internal InMemoryAdditionalText(string path, SourceText text)
        {
            Path = path;
            _text = text;
        }

        internal InMemoryAdditionalText(string path, string content)
            : this(path, SourceText.From(content))
        {
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    internal static class TestCompilations
    {
        private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

        /// <summary>
        /// Compila <paramref name="source"/> em memória e FALHA o teste se o código de exemplo não
        /// compilar — um símbolo que não existe classificaria "corretamente" em camada nenhuma e
        /// daria um teste verde sem valor.
        /// </summary>
        internal static CSharpCompilation Create(string source, string assemblyName = "ArchLayerResolverSample")
        {
            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source) },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            Assert.True(
                errors.Length == 0,
                "O código de exemplo do teste não compila: " + string.Join(" | ", errors.Select(e => e.ToString())));

            return compilation;
        }

        private static ImmutableArray<MetadataReference> LoadReferences()
        {
            // Referências do próprio runtime que executa os testes (net10.0). Evita depender de um
            // pacote de reference assemblies só para ter System.Object/System.Attribute disponíveis.
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

    /// <summary>
    /// Fábricas de <see cref="ArchConfig"/> EM MEMÓRIA. Os testes desta trilha (1B) não podem
    /// depender do parser YAML (trilha 1A, em andamento em paralelo): o objeto de configuração é
    /// construído à mão, o que também deixa cada teste explícito sobre a política que está provando.
    /// </summary>
    internal static class TestConfigs
    {
        internal static LayerMatchCriterion Namespace(string glob) => new LayerMatchCriterion(@namespace: glob);

        internal static LayerMatchCriterion NameSuffix(string suffix) => new LayerMatchCriterion(nameSuffix: suffix);

        internal static LayerMatchCriterion BaseType(string metadataName) => new LayerMatchCriterion(baseType: metadataName);

        internal static LayerMatchCriterion Implements(string metadataName) => new LayerMatchCriterion(implements: metadataName);

        internal static LayerMatchCriterion Attribute(string metadataName) => new LayerMatchCriterion(attribute: metadataName);

        internal static LayerDefinition Layer(string name, LayerMatchCriterion[] match, LayerMatchCriterion[] exclude = null)
            => new LayerDefinition(name, match, exclude);

        internal static RuleDefinition Rule(
            string id,
            string slot,
            string type,
            IReadOnlyDictionary<string, string> extra = null,
            bool enabled = true)
            => new RuleDefinition(id, slot, type, enabled, "media", null, null, extra);

        internal static ArchConfig Policy(LayerDefinition[] layers, RuleDefinition[] rules = null)
        {
            var layerMap = new Dictionary<string, LayerDefinition>(StringComparer.Ordinal);
            foreach (var layer in layers)
            {
                layerMap[layer.Name] = layer;
            }

            return new ArchConfig(
                "arch-rules/v1",
                layerMap,
                rules ?? Array.Empty<RuleDefinition>(),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["media"] = "warning" },
                "media");
        }

        internal static Func<string, ArchConfigParseResult> Parser(ArchConfig config, params SchemaValidationError[] errors)
            => _ => new ArchConfigParseResult(config, errors);
    }

    internal static class AnalyzerRunner
    {
        /// <summary>
        /// Roda o analyzer contra uma compilação real, do jeito que o compilador roda, e devolve os
        /// diagnósticos. Falha se aparecer AD0001 — que é como o Roslyn reporta tanto "ID não
        /// declarado em SupportedDiagnostics" quanto "o analyzer lançou exceção", os dois modos de
        /// falha silenciosa que o ADR-001 §1 manda vigiar.
        /// </summary>
        internal static async Task<ImmutableArray<Diagnostic>> RunAsync(
            DiagnosticAnalyzer analyzer,
            Compilation compilation,
            params AdditionalText[] additionalFiles)
        {
            var options = new AnalyzerOptions(ImmutableArray.CreateRange(additionalFiles ?? Array.Empty<AdditionalText>()));

            var diagnostics = await compilation
                .WithAnalyzers(ImmutableArray.Create(analyzer), options)
                .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
                .ConfigureAwait(false);

            var ad0001 = diagnostics.Where(d => d.Id == "AD0001").ToArray();
            Assert.True(
                ad0001.Length == 0,
                "AD0001 reportado (ID não suportado ou exceção no analyzer): "
                    + string.Join(" | ", ad0001.Select(d => d.GetMessage())));

            return diagnostics;
        }

        internal static string Describe(ImmutableArray<Diagnostic> diagnostics)
            => diagnostics.Length == 0
                ? "(nenhum diagnóstico)"
                : string.Join(" | ", diagnostics.Select(d => d.Id + ": " + d.GetMessage()));
    }
}
