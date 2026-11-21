using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arch.Config;
using Xunit;

namespace Arch.Config.Tests
{
    /// <summary>
    /// O JSON Schema (<c>docs/schema/arch-rules.v1.json</c>) é para o editor: não pode rejeitar o que o parser
    /// aceita. Sem biblioteca de JSON Schema no repo, estes testes leem o <c>pattern</c> e a forma de <c>when</c>
    /// direto do arquivo e os confrontam com o parser real.
    /// </summary>
    public sealed class SchemaParserParityTests
    {
        private static JsonElement LoadSchema()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "docs", "schema", "arch-rules.v1.json");
                if (File.Exists(candidate))
                {
                    return JsonDocument.Parse(File.ReadAllText(candidate)).RootElement.Clone();
                }
            }

            throw new FileNotFoundException("docs/schema/arch-rules.v1.json não encontrado a partir de " + AppContext.BaseDirectory);
        }

        private static JsonElement MatchProperty(string name)
            => LoadSchema().GetProperty("$defs").GetProperty("matchCriterion").GetProperty("properties").GetProperty(name);

        [Theory]
        [InlineData("namespace")]
        [InlineData("assembly")]
        [InlineData("[Nome]")]
        [InlineData("Acme.[Module].Services.**")]
        [InlineData("Acme..Api")]
        [InlineData("Acme.Api.")]
        [InlineData(".Acme.Api")]
        [InlineData(" Acme . Api ")]
        [InlineData("Acme. [Module] .Api")]
        [InlineData("Acme.*.**")]
        [InlineData("Acme.Api-Core.v2")]
        [InlineData("")]
        public void Pattern_de_namespace_e_assembly_aceita_tudo_o_que_o_SegmentPattern_aceita(string value)
        {
            Assert.True(SegmentPattern.TryParse(value, out _, out var error), error);

            foreach (var field in new[] { "namespace", "assembly" })
            {
                var pattern = MatchProperty(field).GetProperty("pattern").GetString();
                Assert.Matches(new Regex(pattern), value);
            }
        }

        [Theory]
        [InlineData("Acme.[Module")]
        [InlineData("Acme.[1x].Api")]
        [InlineData("Acme.Foo[Bar].Api")]
        [InlineData("Acme.[A B].Api")]
        public void Pattern_de_namespace_e_assembly_rejeita_apenas_placeholder_malformado(string value)
        {
            Assert.False(SegmentPattern.TryParse(value, out _, out _));

            foreach (var field in new[] { "namespace", "assembly" })
            {
                var pattern = MatchProperty(field).GetProperty("pattern").GetString();
                Assert.DoesNotMatch(new Regex(pattern), value);
            }
        }

        [Fact]
        public void When_aceita_a_forma_de_mapa_e_a_escalar_legada_que_o_parser_aceita()
        {
            var when = LoadSchema().GetProperty("$defs").GetProperty("rule").GetProperty("properties").GetProperty("when");

            Assert.True(when.TryGetProperty("oneOf", out var alternatives), "when deve ser oneOf (objeto | string)");
            var types = alternatives.EnumerateArray().Select(a => a.GetProperty("type").GetString()).OrderBy(t => t).ToArray();
            Assert.Equal(new[] { "object", "string" }, types);
        }

        [Fact]
        public void Parser_aceita_when_escalar_legado()
        {
            const string yaml = @"schema: arch-rules/v1
layers:
  A: { match: [ { namespace: 'X.[M].A' } ] }
  B: { match: [ { namespace: 'X.[M].B' } ] }
rules:
  - { id: R1, slot: ARCH0001, type: forbidden-call, from: A, to: B, when: 'differ=M' }
";
            var result = ArchConfigParser.Parse(yaml);

            Assert.Empty(result.Errors);
            var rule = Assert.Single(result.Config.Rules);
            Assert.True(rule.TryGetKeyList("when", out var key, out var items));
            Assert.Equal("differ", key);
            Assert.Equal("M", Assert.Single(items));
        }
    }
}
