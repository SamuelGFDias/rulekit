using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Arch.Cli;
using Arch.Config;
using Xunit;

namespace Arch.Cli.Tests
{
    /// <summary>
    /// Issue #25 — <c>arch-rules baseline &lt;sarif&gt;... [--update | --check] [--strict]</c>. Os SARIFs aqui
    /// têm a forma real que o Roslyn grava (<c>-p:ErrorLog=...,version=2.1</c>): <c>ruleId</c> de slot, uri
    /// <c>file:///</c> absoluta e <c>archRuleId</c>/<c>archSubject</c> em <c>properties.customProperties</c>.
    /// </summary>
    public sealed class BaselineCommandTests : IDisposable
    {
        private readonly string _root;

        public BaselineCommandTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "arch-cli-baseline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
        }

        private string Project(string relativeDir = "")
        {
            var dir = Path.Combine(_root, relativeDir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "arch-rules.yaml"), "schema: arch-rules/v1\n");
            return dir;
        }

        private static string BaselineIn(string dir) => Path.Combine(dir, BaselineFile.FileName);

        private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

        private static string Uri(string absolutePath) => new System.Uri(absolutePath).AbsoluteUri;

        /// <summary>Um resultado como o Roslyn o grava; <paramref name="uri"/> já no formato do SARIF.</summary>
        private static string Result(string ruleId, string uri, string archRuleId, string subject, bool suppressed = false, string uriBaseId = null)
        {
            var custom = archRuleId == null
                ? string.Empty
                : $"\"customProperties\": {{ \"archRuleId\": {Json(archRuleId)}, \"archRuleType\": \"forbidden-call\", \"archSubject\": {Json(subject)} }}";
            return $@"{{
  ""ruleId"": {Json(ruleId)}, ""ruleIndex"": 0, ""level"": ""warning"",
  ""message"": {{ ""text"": ""x"" }},
  ""locations"": [ {{ ""physicalLocation"": {{ ""artifactLocation"": {{ ""uri"": {Json(uri)}{(uriBaseId == null ? string.Empty : ", \"uriBaseId\": " + Json(uriBaseId))} }}, ""region"": {{ ""startLine"": 3 }} }} }} ],
  {(suppressed ? "\"suppressions\": [ { \"kind\": \"external\" } ]," : string.Empty)}
  ""properties"": {{ ""warningLevel"": 1{(custom.Length == 0 ? string.Empty : ", " + custom)} }}
}}";
        }

        private string Sarif(string name, string[] results, string extraRunProps = "")
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, $@"{{ ""version"": ""2.1.0"", ""runs"": [ {{ {extraRunProps} ""tool"": {{ ""driver"": {{ ""name"": ""csc"" }} }}, ""results"": [ {string.Join(",", results)} ] }} ] }}");
            return path;
        }

        private static (int Exit, string Output) Run(BaselineMode mode, bool strict, params string[] sarifs)
        {
            var output = new StringWriter();
            var exit = BaselineCommand.Run(sarifs, mode, output, strict);
            return (exit, output.ToString());
        }

        private static List<BaselineEntry> Entries(string dir)
        {
            var parsed = BaselineFile.Parse(File.ReadAllText(BaselineIn(dir)));
            Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors.Select(e => e.Message)));
            return parsed.Entries.ToList();
        }

        // ---- gerar (sem flag) ------------------------------------------------------------------

        [Fact]
        public void Gera_o_baseline_no_diretorio_do_arch_rules_com_caminhos_relativos()
        {
            var dir = Project("src/App");
            var sarif = Sarif("a.sarif", new[]
            {
                Result("ARCH0001", Uri(Path.Combine(dir, "Services", "Order.cs")), "ACME-001", "A -> B.Load"),
                Result("ARCH0003", Uri(Path.Combine(dir, "Api.cs")), "ACME-003", "Ns.Api"),
            });

            var (exit, output) = Run(BaselineMode.Regenerate, false, sarif);

            Assert.Equal(0, exit);
            Assert.Contains(BaselineIn(dir), output);
            Assert.Equal(
                new[] { "ACME-003|Api.cs|Ns.Api", "ACME-001|Services/Order.cs|A -> B.Load" },
                Entries(dir).Select(e => $"{e.Rule}|{e.File}|{e.Subject}").ToArray());
        }

        [Fact]
        public void Resultados_suprimidos_tambem_entram_e_duplicatas_viram_uma_entrada()
        {
            var dir = Project();
            var uri = Uri(Path.Combine(dir, "A.cs"));
            var sarif = Sarif("a.sarif", new[]
            {
                Result("ARCH0001", uri, "ACME-001", "S", suppressed: true),
                Result("ARCH0001", uri, "ACME-001", "S"),
            });

            Run(BaselineMode.Regenerate, false, sarif);

            Assert.Single(Entries(dir));
        }

        [Fact]
        public void Agrupa_pelo_ancestral_mais_proximo_com_arch_rules_e_une_varios_sarifs()
        {
            var outer = Project();
            var inner = Project("libs/Inner");
            var s1 = Sarif("1.sarif", new[] { Result("ARCH0001", Uri(Path.Combine(outer, "Top.cs")), "R1", "S1") });
            var s2 = Sarif("2.sarif", new[] { Result("ARCH0001", Uri(Path.Combine(inner, "Deep", "X.cs")), "R1", "S2") });

            var (exit, _) = Run(BaselineMode.Regenerate, false, s1, s2);

            Assert.Equal(0, exit);
            Assert.Equal("Top.cs", Assert.Single(Entries(outer)).File);
            Assert.Equal("Deep/X.cs", Assert.Single(Entries(inner)).File);
        }

        [Fact]
        public void Ignora_resultados_que_nao_sao_de_slot()
        {
            var dir = Project();
            var uri = Uri(Path.Combine(dir, "A.cs"));
            var sarif = Sarif("a.sarif", new[]
            {
                Result("CS0168", uri, "X", "S"),
                Result("ARCH9001", uri, "X", "S"),
                Result("ARCH0513", uri, "X", "S"),
                Result("ARCH0007", uri, "ACME-007", "S"),
            });

            Run(BaselineMode.Regenerate, false, sarif);

            Assert.Equal("ACME-007", Assert.Single(Entries(dir)).Rule);
        }

        [Fact]
        public void Sem_ancestral_com_arch_rules_e_aviso_e_ignorado_e_strict_falha_sem_escrever()
        {
            var project = Project("p");
            var orphan = Path.Combine(_root, "orfao", "O.cs");
            var sarif = Sarif("a.sarif", new[]
            {
                Result("ARCH0001", Uri(Path.Combine(project, "A.cs")), "R", "S"),
                Result("ARCH0001", Uri(orphan), "R", "S"),
            });

            var (exit, output) = Run(BaselineMode.Regenerate, false, sarif);
            Assert.Equal(0, exit);
            Assert.Contains("AVISO", output);
            Assert.Contains(orphan, output);
            Assert.Single(Entries(project));

            File.Delete(BaselineIn(project));
            var (strictExit, strictOutput) = Run(BaselineMode.Regenerate, true, sarif);
            Assert.Equal(1, strictExit);
            Assert.Contains("ERRO", strictOutput);
            Assert.False(File.Exists(BaselineIn(project)));
        }

        [Fact]
        public void Resultado_sem_archRuleId_ou_assunto_e_aviso_e_ignorado()
        {
            var dir = Project();
            var sarif = Sarif("a.sarif", new[] { Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), null, null) });

            var (exit, output) = Run(BaselineMode.Regenerate, false, sarif);

            Assert.Equal(0, exit);
            Assert.Contains("AVISO", output);
            Assert.Contains("archRuleId", output);
            Assert.False(File.Exists(BaselineIn(dir)));
        }

        [Fact]
        public void Regerar_substitui_o_conteudo_e_e_deterministico()
        {
            var dir = Project();
            File.WriteAllText(BaselineIn(dir), BaselineFile.Write(new[] { new BaselineEntry("R", "Velho.cs", "S") }));
            var sarif = Sarif("a.sarif", new[]
            {
                Result("ARCH0001", Uri(Path.Combine(dir, "B.cs")), "R", "S2"),
                Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "S1"),
            });

            Run(BaselineMode.Regenerate, false, sarif);
            var first = File.ReadAllText(BaselineIn(dir));
            Run(BaselineMode.Regenerate, false, sarif);

            Assert.Equal(new[] { "A.cs", "B.cs" }, Entries(dir).Select(e => e.File).ToArray());
            Assert.Equal(first, File.ReadAllText(BaselineIn(dir)));
        }

        [Fact]
        public void Uri_relativa_com_uriBaseId_e_caminho_com_espaco_codificado_sao_resolvidos()
        {
            var dir = Project("meu projeto");
            var baseUri = Uri(_root + Path.DirectorySeparatorChar);
            var sarif = Sarif(
                "a.sarif",
                new[] { Result("ARCH0001", "meu%20projeto/Pasta/A.cs", "R", "S", uriBaseId: "SRC") },
                extraRunProps: $"\"originalUriBaseIds\": {{ \"SRC\": {{ \"uri\": {Json(baseUri)} }} }},");

            var (exit, output) = Run(BaselineMode.Regenerate, false, sarif);

            Assert.Equal(0, exit);
            Assert.DoesNotContain("AVISO", output);
            Assert.Equal("Pasta/A.cs", Assert.Single(Entries(dir)).File);
        }

        // ---- --update --------------------------------------------------------------------------

        [Fact]
        public void Update_remove_o_que_nao_aparece_mais_e_nunca_acrescenta()
        {
            var dir = Project();
            File.WriteAllText(BaselineIn(dir), BaselineFile.Write(new[]
            {
                new BaselineEntry("R", "A.cs", "ficou"),
                new BaselineEntry("R", "A.cs", "foi-corrigida"),
            }));
            var sarif = Sarif("a.sarif", new[]
            {
                Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "ficou", suppressed: true),
                Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "violacao-nova"),
            });

            var (exit, output) = Run(BaselineMode.Update, false, sarif);

            Assert.Equal(0, exit);
            Assert.Equal("ficou", Assert.Single(Entries(dir)).Subject);
            Assert.Contains("foi-corrigida", output);
            Assert.DoesNotContain("violacao-nova", File.ReadAllText(BaselineIn(dir)));
        }

        [Fact]
        public void Update_ignora_diretorio_sem_baseline_e_nao_cria_arquivo()
        {
            var dir = Project();
            var sarif = Sarif("a.sarif", new[] { Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "S") });

            var (exit, _) = Run(BaselineMode.Update, false, sarif);

            Assert.Equal(0, exit);
            Assert.False(File.Exists(BaselineIn(dir)));
        }

        [Fact]
        public void Update_sem_mudanca_nao_reescreve_o_arquivo()
        {
            var dir = Project();
            var original = BaselineFile.Write(new[] { new BaselineEntry("R", "A.cs", "S") }).Replace("\n", "\r\n");
            File.WriteAllText(BaselineIn(dir), original);
            var sarif = Sarif("a.sarif", new[] { Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "S") });

            Run(BaselineMode.Update, false, sarif);

            Assert.Equal(original, File.ReadAllText(BaselineIn(dir)));
        }

        // ---- --check ---------------------------------------------------------------------------

        [Fact]
        public void Check_sai_1_listando_as_entradas_obsoletas_e_nao_escreve()
        {
            var dir = Project();
            var before = BaselineFile.Write(new[]
            {
                new BaselineEntry("R", "A.cs", "ficou"),
                new BaselineEntry("R", "A.cs", "obsoleta"),
            });
            File.WriteAllText(BaselineIn(dir), before);
            var sarif = Sarif("a.sarif", new[] { Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "ficou", suppressed: true) });

            var (exit, output) = Run(BaselineMode.Check, false, sarif);

            Assert.Equal(1, exit);
            Assert.Contains("obsoleta", output);
            Assert.DoesNotContain("ficou", output.Replace("obsoleta", string.Empty));
            Assert.Equal(before, File.ReadAllText(BaselineIn(dir)));
        }

        [Fact]
        public void Check_sai_0_quando_todas_as_entradas_ainda_aparecem_mesmo_com_violacao_nova()
        {
            var dir = Project();
            File.WriteAllText(BaselineIn(dir), BaselineFile.Write(new[] { new BaselineEntry("R", "A.cs", "S") }));
            var sarif = Sarif("a.sarif", new[]
            {
                Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "S", suppressed: true),
                Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "nova"),
            });

            var (exit, output) = Run(BaselineMode.Check, false, sarif);

            Assert.Equal(0, exit);
            Assert.Contains("OK", output);
        }

        [Fact]
        public void Check_ignora_diretorio_sem_baseline()
        {
            var dir = Project();
            var sarif = Sarif("a.sarif", new[] { Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "S") });

            Assert.Equal(0, Run(BaselineMode.Check, false, sarif).Exit);
        }

        [Theory]
        [InlineData(BaselineMode.Check)]
        [InlineData(BaselineMode.Update)]
        public void Baseline_existente_invalido_e_erro(BaselineMode mode)
        {
            var dir = Project();
            File.WriteAllText(BaselineIn(dir), "schema: errado\n");
            var sarif = Sarif("a.sarif", new[] { Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "S") });

            var (exit, output) = Run(mode, false, sarif);

            Assert.Equal(1, exit);
            Assert.Contains("ERRO", output);
            Assert.Contains("ARCH9009", output);
        }

        // ---- entrada ---------------------------------------------------------------------------

        [Fact]
        public void Sarif_inexistente_ou_invalido_e_erro()
        {
            var bad = Path.Combine(_root, "ruim.sarif");
            File.WriteAllText(bad, "{ não é json");
            var notSarif = Path.Combine(_root, "outro.json");
            File.WriteAllText(notSarif, "{ \"foo\": 1 }");

            foreach (var path in new[] { Path.Combine(_root, "nao-existe.sarif"), bad, notSarif })
            {
                var (exit, output) = Run(BaselineMode.Regenerate, false, path);
                Assert.Equal(1, exit);
                Assert.Contains("ERRO", output);
            }
        }

        [Fact]
        public void Program_roteia_os_argumentos_do_comando_baseline()
        {
            var dir = Project();
            var sarif = Sarif("a.sarif", new[] { Result("ARCH0001", Uri(Path.Combine(dir, "A.cs")), "R", "S") });
            var output = new StringWriter();
            var error = new StringWriter();

            Assert.Equal(0, Program.Run(new[] { "baseline", sarif }, output, error));
            Assert.True(File.Exists(BaselineIn(dir)));
            Assert.Equal(0, Program.Run(new[] { "baseline", sarif, "--check", "--strict" }, output, error));
            Assert.Equal(0, Program.Run(new[] { "baseline", "--update", sarif }, output, error));
        }

        [Theory]
        [InlineData("baseline")]
        [InlineData("baseline x.sarif --update --check")]
        [InlineData("baseline x.sarif --nope")]
        public void Program_rejeita_uso_invalido_do_baseline(string line)
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exit = Program.Run(line.Split(' '), output, error);

            Assert.Equal(1, exit);
            Assert.Contains("baseline", error.ToString());
        }

        [Fact]
        public void Ajuda_documenta_o_baseline()
        {
            var output = new StringWriter();

            Program.Run(new[] { "--help" }, output, new StringWriter());

            Assert.Contains("arch-rules baseline <sarif>", output.ToString());
            Assert.Contains("--update", output.ToString());
            Assert.Contains("--check", output.ToString());
        }
    }
}
