using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Arch.Config;
using Xunit;

namespace Arch.Config.Tests
{
    /// <summary>
    /// Resolvedor de `extends` compartilhado por analyzer e CLI (issue #18). O resolvedor não faz
    /// I/O: recebe um loader (caminho → texto, ou null quando ausente) e um combinador de caminho, e
    /// devolve os textos na ordem crescente de precedência de
    /// <see cref="ArchConfigParser.Parse(IReadOnlyList{string})"/> mais os problemas tipados. A
    /// POLÍTICA de erro (ARCH9006/ARCH9007 no analyzer, erro bloqueante no CLI) é do chamador.
    /// </summary>
    public class ExtendsChainResolverTests
    {
        /// <summary>Combinador textual simples: diretório do arquivo que declara + relativo.</summary>
        private static string Combine(string declaringPath, string relative)
        {
            var dir = declaringPath.Substring(0, declaringPath.LastIndexOf('/'));
            var parts = new List<string>(dir.Split('/'));
            foreach (var segment in relative.Split('/'))
            {
                if (segment == "." || segment.Length == 0)
                {
                    continue;
                }

                if (segment == "..")
                {
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }

                parts.Add(segment);
            }

            return string.Join("/", parts);
        }

        private static ExtendsChainResult Resolve(
            string rootPath,
            IDictionary<string, string> files,
            int maxDepth = ExtendsChainResolver.DefaultMaxDepth,
            int maxTexts = ExtendsChainResolver.DefaultMaxTexts)
        {
            return ExtendsChainResolver.Resolve(
                rootPath,
                files[rootPath],
                path => files.TryGetValue(path, out var text) ? text : null,
                Combine,
                pathComparer: null,
                maxDepth: maxDepth,
                maxTexts: maxTexts,
                cancellationToken: CancellationToken.None);
        }

        [Fact]
        public void SemExtends_DevolveSoORaiz_SemProblemas()
        {
            var files = new Dictionary<string, string> { ["/r/a.yaml"] = "schema: arch-rules/v1\n" };

            var result = Resolve("/r/a.yaml", files);

            Assert.Equal(new[] { "schema: arch-rules/v1\n" }, result.Texts);
            Assert.Equal("/r/a.yaml", Assert.Single(result.Entries).Path);
            Assert.Empty(result.Problems);
        }

        [Fact]
        public void CadeiaDeTresNiveis_OrdemCrescenteDePrecedencia_RelativoReancoraACadaNivel()
        {
            var files = new Dictionary<string, string>
            {
                ["/r/app/main.yaml"] = "extends: [./mid/mid.yaml]\n# main",
                ["/r/app/mid/mid.yaml"] = "extends: [../../shared/base.yaml]\n# mid",
                ["/r/shared/base.yaml"] = "# base",
            };

            var result = Resolve("/r/app/main.yaml", files);

            Assert.Empty(result.Problems);
            Assert.Equal(
                new[] { "/r/shared/base.yaml", "/r/app/mid/mid.yaml", "/r/app/main.yaml" },
                result.Entries.Select(e => e.Path).ToArray());
            Assert.Equal(result.Entries.Select(e => e.Text).ToArray(), result.Texts.ToArray());
        }

        [Fact]
        public void MultiplosExtends_NaOrdemDeclarada_AntesDoArquivoQueDeclara()
        {
            var files = new Dictionary<string, string>
            {
                ["/r/main.yaml"] = "extends: [./a.yaml, ./b.yaml]\n",
                ["/r/a.yaml"] = "# a",
                ["/r/b.yaml"] = "# b",
            };

            var result = Resolve("/r/main.yaml", files);

            Assert.Equal(new[] { "/r/a.yaml", "/r/b.yaml", "/r/main.yaml" }, result.Entries.Select(e => e.Path).ToArray());
        }

        [Fact]
        public void Ausente_ViraProblemaMissing_ERamoEDescartado()
        {
            var files = new Dictionary<string, string>
            {
                ["/r/main.yaml"] = "extends: [./sumiu.yaml, ./a.yaml]\n",
                ["/r/a.yaml"] = "# a",
            };

            var result = Resolve("/r/main.yaml", files);

            var problem = Assert.Single(result.Problems);
            Assert.Equal(ExtendsChainProblemKind.Missing, problem.Kind);
            Assert.Equal("./sumiu.yaml", problem.DeclaredPath);
            Assert.Equal("/r/sumiu.yaml", problem.ResolvedPath);
            Assert.Equal("/r/main.yaml", problem.DeclaringPath);
            Assert.Equal(new[] { "/r/a.yaml", "/r/main.yaml" }, result.Entries.Select(e => e.Path).ToArray());
        }

        [Fact]
        public void Ciclo_ViraProblemaCycle_ComAPilhaDaRaizAteQuemDeclara_ESemTravar()
        {
            var files = new Dictionary<string, string>
            {
                ["/r/a.yaml"] = "extends: [./b.yaml]\n# a",
                ["/r/b.yaml"] = "extends: [./A.yaml]\n# b",
            };

            var result = Resolve("/r/a.yaml", files);

            var problem = Assert.Single(result.Problems);
            Assert.Equal(ExtendsChainProblemKind.Cycle, problem.Kind);
            Assert.Equal(new[] { "/r/a.yaml", "/r/b.yaml" }, problem.Stack.ToArray());
            Assert.Equal("/r/A.yaml", problem.ResolvedPath);

            // O ciclo corta o RAMO, não a cadeia: os dois documentos continuam na lista.
            Assert.Equal(new[] { "/r/b.yaml", "/r/a.yaml" }, result.Entries.Select(e => e.Path).ToArray());
        }

        [Fact]
        public void ComparadorDeCaminho_PodeSerSensivelACaixa()
        {
            var files = new Dictionary<string, string>
            {
                ["/r/a.yaml"] = "extends: [./b.yaml]\n",
                ["/r/b.yaml"] = "extends: [./A.yaml]\n",
                ["/r/A.yaml"] = "# A",
            };

            var result = ExtendsChainResolver.Resolve(
                "/r/a.yaml",
                files["/r/a.yaml"],
                path => files.TryGetValue(path, out var text) ? text : null,
                Combine,
                StringComparer.Ordinal);

            Assert.Empty(result.Problems);
            Assert.Equal(3, result.Entries.Count);
        }

        [Fact]
        public void Profundidade_AcimaDoLimite_ViraProblemaDepthExceeded()
        {
            var files = new Dictionary<string, string>();
            for (var i = 0; i < 10; i++)
            {
                files[$"/r/f{i}.yaml"] = $"extends: [./f{i + 1}.yaml]\n";
            }

            files["/r/f10.yaml"] = "# fim";

            var result = Resolve("/r/f0.yaml", files, maxDepth: 3);

            var problem = Assert.Single(result.Problems);
            Assert.Equal(ExtendsChainProblemKind.DepthExceeded, problem.Kind);
            Assert.Equal("./f3.yaml", problem.DeclaredPath);
            Assert.Equal("/r/f2.yaml", problem.DeclaringPath);
            Assert.Equal(new[] { "/r/f2.yaml", "/r/f1.yaml", "/r/f0.yaml" }, result.Entries.Select(e => e.Path).ToArray());
        }

        [Fact]
        public void LimiteDeTextos_CortaDiamantesExponenciais()
        {
            // Diamante: cada nível estende dois arquivos que estendem o mesmo próximo nível — sem
            // limite de textos a cadeia cresceria 2^n mesmo sem ciclo.
            var files = new Dictionary<string, string>();
            for (var i = 0; i < 12; i++)
            {
                files[$"/r/l{i}a.yaml"] = $"extends: [./l{i + 1}a.yaml, ./l{i + 1}b.yaml]\n";
                files[$"/r/l{i}b.yaml"] = $"extends: [./l{i + 1}a.yaml, ./l{i + 1}b.yaml]\n";
            }

            files["/r/l12a.yaml"] = "# a";
            files["/r/l12b.yaml"] = "# b";

            var result = Resolve("/r/l0a.yaml", files, maxTexts: 16);

            // O limite é checado antes de descer em cada `extends`; os nós já na pilha ainda entram
            // na lista ao voltar da recursão, então o teto é limite + profundidade (13), não 2^12.
            Assert.True(result.Texts.Count <= 16 + 13, "textos: " + result.Texts.Count);
            Assert.Contains(result.Problems, p => p.Kind == ExtendsChainProblemKind.DepthExceeded);
        }

        [Fact]
        public void YamlInvalido_NaoLanca_ECadeiaSegueSoComORaiz()
        {
            var files = new Dictionary<string, string> { ["/r/a.yaml"] = "extends: [unterminated\n  : : :" };

            var result = Resolve("/r/a.yaml", files);

            Assert.Empty(result.Problems);
            Assert.Single(result.Texts);
        }

        [Fact]
        public void Cancelamento_ePropagado()
        {
            var files = new Dictionary<string, string> { ["/r/a.yaml"] = "# a" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => ExtendsChainResolver.Resolve(
                "/r/a.yaml", files["/r/a.yaml"], _ => null, Combine, cancellationToken: cts.Token));
        }

        [Fact]
        public void ArgumentosObrigatorios_SaoValidados()
        {
            Assert.Throws<ArgumentNullException>(() => ExtendsChainResolver.Resolve(null, "x", _ => null, Combine));
            Assert.Throws<ArgumentNullException>(() => ExtendsChainResolver.Resolve("/r/a", null, _ => null, Combine));
            Assert.Throws<ArgumentNullException>(() => ExtendsChainResolver.Resolve("/r/a", "x", null, Combine));
            Assert.Throws<ArgumentNullException>(() => ExtendsChainResolver.Resolve("/r/a", "x", _ => null, null));
        }
    }
}
