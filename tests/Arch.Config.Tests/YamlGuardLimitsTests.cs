using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Arch.Config.Tests
{
    /// <summary>
    /// Revisão B2/B3: o <c>YamlSubsetGuard</c> (roda por eventos, antes de <c>YamlStream.Load</c>)
    /// rejeita com ARCH9001 bloqueante, com linha/coluna, (a) texto acima de 1 MiB e (b) aninhamento
    /// (mapping/sequence) acima de 64 níveis — antes, 100 mil níveis de `[` estouravam a pilha no
    /// <c>YamlStream.Load</c> e derrubavam o processo do compilador/CLI. Também converte em ARCH9001 as
    /// exceções do scanner que não são <c>YamlException</c> (ex.: `...` seguido de conteúdo).
    /// </summary>
    public class YamlGuardLimitsTests
    {
        private const int MaxChars = 1_048_576;
        private const int MaxDepth = 64;

        private static SchemaValidationError SingleBlocking(ArchConfigParseResult result)
        {
            Assert.Null(result.Config);
            var error = Assert.Single(result.Errors);
            Assert.Equal("ARCH9001", error.Code);
            Assert.True(error.IsBlocking);
            return error;
        }

        [Fact]
        public void Parse_100kNestedFlowSequences_IsRejectedWithArch9001_WithoutStackOverflow()
        {
            var yaml = new string('[', 100_000);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var error = SingleBlocking(ArchConfigParser.Parse(yaml));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "deve rejeitar rápido: " + watch.Elapsed);

            Assert.Contains("aninhamento", error.Message);
            Assert.True(error.Line >= 1, "linha deve vir do evento que estourou o limite");
            Assert.True(error.Column >= 1);
        }

        [Fact]
        public void Parse_100kNestedFlowMappings_IsRejectedWithArch9001()
        {
            var yaml = string.Concat(Enumerable.Repeat("{a: ", 100_000));

            var error = SingleBlocking(ArchConfigParser.Parse(yaml));

            Assert.Contains("aninhamento", error.Message);
        }

        [Fact]
        public void Parse_DeepBlockNesting_IsRejectedWithArch9001()
        {
            var yaml = string.Concat(Enumerable.Range(0, 200).Select(i => new string(' ', i) + "a:\n"));

            var error = SingleBlocking(ArchConfigParser.Parse(yaml));

            Assert.Contains("aninhamento", error.Message);
            Assert.True(error.Line >= 60);
        }

        [Fact]
        public void Parse_NestingAtTheLimit_IsNotRejectedByTheGuard()
        {
            var yaml = "a: " + new string('[', MaxDepth - 1) + new string(']', MaxDepth - 1);

            var result = ArchConfigParser.Parse(yaml);

            Assert.DoesNotContain(result.Errors, e => e.Message.Contains("aninhamento"));
        }

        [Fact]
        public void Parse_NestingOneAboveTheLimit_IsRejected()
        {
            var yaml = "a: " + new string('[', MaxDepth) + new string(']', MaxDepth);

            var error = SingleBlocking(ArchConfigParser.Parse(yaml));

            Assert.Contains("aninhamento", error.Message);
        }

        [Fact]
        public void PeekExtends_100kNestedFlowSequences_ReturnsEmpty_WithoutStackOverflow()
        {
            var yaml = "extends: " + new string('[', 100_000);

            Assert.Empty(ArchConfigParser.PeekExtends(yaml));
        }

        [Fact]
        public void Parse_TextAboveOneMiB_IsRejectedWithArch9001_AndPosition()
        {
            var yaml = "schema: arch-rules/v1\n# " + new string('x', MaxChars);

            var error = SingleBlocking(ArchConfigParser.Parse(yaml));

            Assert.Contains("1 MiB", error.Message);
            Assert.True(error.Line >= 1);
            Assert.True(error.Column >= 1);
        }

        [Fact]
        public void Parse_TextExactlyAtOneMiB_IsNotRejectedBySize()
        {
            var prefix = "schema: arch-rules/v1\n# ";
            var yaml = prefix + new string('x', MaxChars - prefix.Length);
            Assert.Equal(MaxChars, yaml.Length);

            var result = ArchConfigParser.Parse(yaml);

            Assert.DoesNotContain(result.Errors, e => e.Message.Contains("MiB"));
        }

        [Fact]
        public void PeekExtends_TextAboveOneMiB_ReturnsEmpty()
        {
            var yaml = "extends: [a.yaml]\n# " + new string('x', MaxChars);

            Assert.Empty(ArchConfigParser.PeekExtends(yaml));
        }

        [Fact]
        public void Parse_ChainWithOversizedSecondDocument_PointsAtThatDocument()
        {
            var texts = new List<string> { "schema: arch-rules/v1\n", "schema: arch-rules/v1\n# " + new string('x', MaxChars) };

            var result = ArchConfigParser.Parse(texts);

            var error = SingleBlocking(result);
            Assert.Equal(1, error.SourceDocumentIndex);
        }

        // ---- B3: exceção do scanner que não é YamlException ----

        [Fact]
        public void Parse_DocumentEndMarkerFollowedByContent_IsArch9001WithPosition_NotAnUnexpectedFailure()
        {
            var error = SingleBlocking(ArchConfigParser.Parse("...\nfoo: bar"));

            Assert.DoesNotContain("Falha inesperada", error.Message);
            Assert.Contains("YAML", error.Message);
            Assert.True(error.Line >= 1, "deve trazer a melhor posição disponível, não 0");
        }

        [Fact]
        public void PeekExtends_DocumentEndMarkerFollowedByContent_ReturnsEmpty()
        {
            Assert.Empty(ArchConfigParser.PeekExtends("...\nfoo: bar"));
        }
    }
}
