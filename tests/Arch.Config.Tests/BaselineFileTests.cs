using System.Linq;
using Xunit;

namespace Arch.Config.Tests
{
    /// <summary>Issue #25 — modelo, parser e writer do <c>arch-rules.baseline.yaml</c>.</summary>
    public class BaselineFileTests
    {
        private const string Valid = @"schema: arch-rules-baseline/v1
entries:
  - rule: ACME-001
    file: Services/OrderService.cs
    subject: ""Acme.Services.OrderService -> Acme.Data.OrderRepository.Load""
  - { rule: ACME-002, file: Api/Ctl.cs, subject: ""Acme.Api.Ctl"" }
";

        [Fact]
        public void Parse_valido_devolve_as_entradas_na_ordem_do_arquivo()
        {
            var result = BaselineFile.Parse(Valid);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
            Assert.Equal(2, result.Entries.Count);
            Assert.Equal("ACME-001", result.Entries[0].Rule);
            Assert.Equal("Services/OrderService.cs", result.Entries[0].File);
            Assert.Equal("Acme.Services.OrderService -> Acme.Data.OrderRepository.Load", result.Entries[0].Subject);
            Assert.Equal("Acme.Api.Ctl", result.Entries[1].Subject);
        }

        [Fact]
        public void Parse_lista_vazia_e_valido()
        {
            var result = BaselineFile.Parse("schema: arch-rules-baseline/v1\nentries: []\n");

            Assert.True(result.IsValid);
            Assert.Empty(result.Entries);
        }

        [Fact]
        public void Parse_normaliza_contrabarra_e_ponto_barra_no_caminho()
        {
            var yaml = "schema: arch-rules-baseline/v1\nentries:\n  - { rule: R, file: 'Sub\\A.cs', subject: S }\n  - { rule: R, file: './B.cs', subject: S }\n";

            var result = BaselineFile.Parse(yaml);

            Assert.Equal(new[] { "Sub/A.cs", "B.cs" }, result.Entries.Select(e => e.File).ToArray());
        }

        [Fact]
        public void Parse_ignora_entradas_duplicadas()
        {
            var yaml = Valid + "  - { rule: ACME-002, file: Api/Ctl.cs, subject: \"Acme.Api.Ctl\" }\n";

            Assert.Equal(2, BaselineFile.Parse(yaml).Entries.Count);
        }

        [Theory]
        [InlineData("entries: []\n", "schema", 0, 0)]
        [InlineData("schema: arch-rules-baseline/v2\nentries: []\n", "arch-rules-baseline/v1", 1, 9)]
        [InlineData("schema: arch-rules/v1\nentries: []\n", "arch-rules-baseline/v1", 1, 9)]
        [InlineData("schema: arch-rules-baseline/v1\n", "entries", 0, 0)]
        [InlineData("schema: arch-rules-baseline/v1\nentries: x\n", "lista", 2, 10)]
        [InlineData("schema: arch-rules-baseline/v1\nentries: []\nextra: 1\n", "extra", 3, 1)]
        [InlineData("schema: arch-rules-baseline/v1\nentries:\n  - x\n", "mapeamento", 3, 5)]
        [InlineData("schema: arch-rules-baseline/v1\nentries:\n  - { rule: R, file: F }\n", "subject", 3, 5)]
        [InlineData("schema: arch-rules-baseline/v1\nentries:\n  - { rule: R, file: F, subject: S, line: 3 }\n", "line", 3, 5)]
        [InlineData("schema: arch-rules-baseline/v1\nentries:\n  - { rule: '', file: F, subject: S }\n", "rule", 3, 5)]
        [InlineData("schema: arch-rules-baseline/v1\nentries:\n  - { rule: [a], file: F, subject: S }\n", "rule", 3, 5)]
        [InlineData("schema: arch-rules-baseline/v1\nentries: [\n", "YAML", 3, 1)]
        [InlineData("schema: arch-rules-baseline/v1\nentries: &a []\n", "Anchor", 2, 10)]
        [InlineData("", "vazio", 0, 0)]
        [InlineData("- a\n", "mapeamento", 0, 0)]
        public void Parse_invalido_devolve_ARCH9009_com_posicao_e_nenhuma_entrada(string yaml, string messagePart, int line, int column)
        {
            var result = BaselineFile.Parse(yaml);

            Assert.False(result.IsValid);
            Assert.Empty(result.Entries);
            var error = result.Errors.First();
            Assert.Equal("ARCH9009", error.Code);
            Assert.Contains(messagePart, error.Message);
            if (line > 0)
            {
                Assert.Equal(line, error.Line);
                Assert.Equal(column, error.Column);
            }
        }

        [Fact]
        public void Parse_texto_nulo_nao_lanca()
        {
            Assert.False(BaselineFile.Parse(null).IsValid);
        }

        [Fact]
        public void Parse_texto_acima_do_limite_e_ARCH9009()
        {
            var result = BaselineFile.Parse(new string('#', 1_048_577));

            Assert.False(result.IsValid);
            Assert.Equal("ARCH9009", result.Errors[0].Code);
        }

        [Fact]
        public void Write_ordena_por_arquivo_regra_e_assunto_e_remove_duplicatas()
        {
            var entries = new[]
            {
                new BaselineEntry("B", "z.cs", "s2"),
                new BaselineEntry("B", "a.cs", "s2"),
                new BaselineEntry("A", "a.cs", "s9"),
                new BaselineEntry("B", "a.cs", "s1"),
                new BaselineEntry("B", "a.cs", "s1"),
            };

            var text = BaselineFile.Write(entries);

            var parsed = BaselineFile.Parse(text);
            Assert.Equal(
                new[] { "A|a.cs|s9", "B|a.cs|s1", "B|a.cs|s2", "B|z.cs|s2" },
                parsed.Entries.Select(e => e.Rule + "|" + e.File + "|" + e.Subject).ToArray());
            Assert.StartsWith("schema: arch-rules-baseline/v1\nentries:\n", text);
            Assert.EndsWith("\n", text);
        }

        [Fact]
        public void Write_e_deterministico_independente_da_ordem_de_entrada()
        {
            var a = new[] { new BaselineEntry("R", "a.cs", "x"), new BaselineEntry("R", "b.cs", "y") };

            Assert.Equal(BaselineFile.Write(a), BaselineFile.Write(a.Reverse()));
        }

        [Fact]
        public void Write_sem_entradas_gera_lista_vazia_valida()
        {
            var text = BaselineFile.Write(new BaselineEntry[0]);

            Assert.Equal("schema: arch-rules-baseline/v1\nentries: []\n", text);
            Assert.True(BaselineFile.Parse(text).IsValid);
        }

        [Theory]
        [InlineData("Ns.A -> Ns.B.Load")]
        [InlineData("Ns.Cache<T> -> Ns.Repo<T>.Get")]
        [InlineData("com \"aspas\" e \\ contrabarra")]
        [InlineData("começa: com dois-pontos # e comentário")]
        [InlineData("  espaços nas pontas  ")]
        [InlineData("- travessão, [colchetes] {chaves} & e * e !")]
        [InlineData("true")]
        [InlineData("123")]
        [InlineData("~")]
        [InlineData("ação 日本語")]
        public void Round_trip_preserva_o_assunto_exato(string subject)
        {
            var original = new BaselineEntry("ACME-001", "Dir/Arq.cs", subject);

            var parsed = BaselineFile.Parse(BaselineFile.Write(new[] { original }));

            Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors.Select(e => e.Message)));
            Assert.Equal(original, Assert.Single(parsed.Entries));
        }

        [Fact]
        public void Entry_tem_igualdade_por_valor_e_normaliza_o_caminho()
        {
            Assert.Equal(new BaselineEntry("R", "a/b.cs", "s"), new BaselineEntry("R", "a\\b.cs", "s"));
        }
    }
}
