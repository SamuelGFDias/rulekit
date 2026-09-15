using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Arch.Benchmarks.Support
{
    /// <summary>
    /// Constrói compilações C# EM MEMÓRIA (sem <c>.csproj</c> em disco) para a trilha 4C
    /// (perf-audit, PLANO-implementacao.md Fase 4). Cada projeto sintético usa a MESMA política de
    /// camadas (Controller/Service/Repository, ADR-001 D2) para que
    /// <c>ConfigCacheBenchmarks</c> e <c>LayerResolverMemoizationBenchmarks</c> exercitem
    /// exatamente as mesmas regras — só o tamanho/formato do código gerado muda.
    /// </summary>
    internal static class SyntheticProjectFactory
    {
        /// <summary>
        /// <c>arch-rules.yaml</c> de referência: 3 camadas + as 4 regras do ADR-001 (uma por tipo de
        /// avaliador real em <c>Arch.Rules</c>), cada uma num slot distinto do pool. Sintaxe copiada
        /// do exemplo de ADR-001 D2 (Controller/Service/Repository), adaptada ao namespace sintético
        /// abaixo.
        /// </summary>
        internal const string ArchRulesYaml = @"schema: arch-rules/v1

layers:
  Controller:
    match:
      - namespace: ""Acme.Sample.Api.Controllers.**""
      - nameSuffix: ""Controller""
  Service:
    match:
      - nameSuffix: ""Service""
  Repository:
    match:
      - nameSuffix: ""Repository""

severities:
  restrita: error
  media: warning
  livre: suggestion

defaults:
  severity: media

rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
    severity: restrita
    message: ""{from} não pode chamar {to} diretamente ({symbol}). Passe por um Service.""

  - id: ACME-002
    slot: ARCH0002
    type: must-route-through
    from: Controller
    to: Repository
    through: [Service]
    severity: restrita

  - id: ACME-003
    slot: ARCH0003
    type: naming-convention
    layer: Repository
    require: { suffix: Repository }
    severity: media

  - id: ACME-004
    slot: ARCH0004
    type: max-dependencies
    layer: Service
    max: 5
    countBy: constructor-parameters
    severity: media
";

        internal const string ConfigFileName = "arch-rules.yaml";

        private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

        /// <summary>
        /// Projeto de tamanho médio (ADR-001 §5 / enunciado da trilha 4C: "50-100 tipos"): uma
        /// mistura de Controllers/Services/Repositories nos moldes do <c>arch-rules.yaml</c> de
        /// exemplo, mais alguns tipos de dependência/DTO que não pertencem a nenhuma camada (ruído
        /// realista). Usado pelo benchmark de cache de config (frio vs. quente).
        /// </summary>
        internal static CSharpCompilation CreateMediumProject(int typesPerLayer = 25)
        {
            var source = BuildLayeredSource(typesPerLayer);
            return Compile(source, "Arch.Benchmarks.MediumSample");
        }

        /// <summary>
        /// Projeto "achatado" com <paramref name="count"/> tipos DISTINTOS, sufixos alternando entre
        /// Controller/Service/Repository/Handler (o último não casa nenhuma camada — ruído). Usado
        /// pelo benchmark de memoização do <c>LayerResolver</c>: precisa de muitos símbolos
        /// diferentes para comparar contra 1000 classificações repetidas do MESMO símbolo.
        /// </summary>
        internal static CSharpCompilation CreateManyDistinctTypesProject(int count)
        {
            var source = BuildFlatSource(count);
            return Compile(source, "Arch.Benchmarks.BulkSample");
        }

        private static string BuildLayeredSource(int perLayerCount)
        {
            var sb = new StringBuilder();

            // Controller: chama o Repository de mesmo índice diretamente — dispara forbidden-call
            // (ACME-001/002) em metade dos casos; a outra metade passa pelo Service (rota "correta").
            sb.AppendLine("namespace Acme.Sample.Api.Controllers");
            sb.AppendLine("{");
            for (var i = 0; i < perLayerCount; i++)
            {
                sb.AppendLine($"    public class Controller{i}");
                sb.AppendLine("    {");
                if (i % 2 == 0)
                {
                    sb.AppendLine($"        public void Get() {{ new Acme.Sample.Infra.Repository{i}().Load(); }}");
                }
                else
                {
                    var ctorArgs = string.Join(
                        ", ",
                        Enumerable.Range(0, i % 10).Select(d => $"new Acme.Sample.Application.Dep{d}()"));
                    sb.AppendLine($"        public void Get() {{ new Acme.Sample.Application.Service{i}({ctorArgs}).Do(); }}");
                }

                sb.AppendLine("    }");
            }

            sb.AppendLine("}");

            // Service: número de parâmetros de construtor variando 0..9 para exercitar
            // max-dependencies (ACME-004, max: 5) tanto dentro quanto fora do limite.
            sb.AppendLine("namespace Acme.Sample.Application");
            sb.AppendLine("{");
            for (var d = 0; d < 10; d++)
            {
                sb.AppendLine($"    public class Dep{d} {{ }}");
            }

            for (var i = 0; i < perLayerCount; i++)
            {
                var depCount = i % 10;
                var ctorParams = string.Join(
                    ", ",
                    Enumerable.Range(0, depCount).Select(d => $"Dep{d} dep{d}"));

                sb.AppendLine($"    public class Service{i}");
                sb.AppendLine("    {");
                if (depCount > 0)
                {
                    sb.AppendLine($"        public Service{i}({ctorParams}) {{ }}");
                }

                sb.AppendLine($"        public void Do() {{ new Acme.Sample.Infra.Repository{i}().Load(); }}");
                sb.AppendLine("    }");
            }

            sb.AppendLine("}");

            sb.AppendLine("namespace Acme.Sample.Infra");
            sb.AppendLine("{");
            for (var i = 0; i < perLayerCount; i++)
            {
                sb.AppendLine($"    public class Repository{i}");
                sb.AppendLine("    {");
                sb.AppendLine("        public void Load() { }");
                sb.AppendLine("    }");
            }

            sb.AppendLine("}");

            // Ruído: tipos fora de qualquer camada (nem Controller/Service/Repository).
            sb.AppendLine("namespace Acme.Sample.Contracts");
            sb.AppendLine("{");
            for (var i = 0; i < Math.Max(1, perLayerCount / 3); i++)
            {
                sb.AppendLine($"    public class Dto{i} {{ public int Value {{ get; set; }} }}");
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string BuildFlatSource(int count)
        {
            var suffixes = new[] { "Controller", "Service", "Repository", "Handler" };
            var sb = new StringBuilder();

            sb.AppendLine("namespace Acme.Sample.Bulk");
            sb.AppendLine("{");
            for (var i = 0; i < count; i++)
            {
                var suffix = suffixes[i % suffixes.Length];
                sb.AppendLine($"    public class Type{i}{suffix} {{ }}");
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        private static CSharpCompilation Compile(string source, string assemblyName)
        {
            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source) },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            if (errors.Length > 0)
            {
                throw new InvalidOperationException(
                    "O projeto sintético gerado para o benchmark não compila: "
                        + string.Join(" | ", errors.Select(e => e.ToString())));
            }

            return compilation;
        }

        /// <summary>
        /// Mesma técnica de <c>tests/Arch.Analyzer.Tests/TestSupport.cs</c> (TestCompilations):
        /// referências do próprio runtime que executa o benchmark (net10.0), via
        /// TRUSTED_PLATFORM_ASSEMBLIES, para não depender de um pacote de reference assemblies só
        /// para ter System.Object/System.Attribute disponíveis.
        /// </summary>
        private static ImmutableArray<MetadataReference> LoadReferences()
        {
            var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;

            return trusted
                .Split(Path.PathSeparator)
                .Where(path => path.Length > 0)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "netstandard.dll", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "mscorlib.dll", StringComparison.OrdinalIgnoreCase);
                })
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();
        }
    }
}
