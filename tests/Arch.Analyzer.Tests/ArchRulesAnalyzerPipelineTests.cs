using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Arch.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;
using static Arch.Analyzer.Tests.TestConfigs;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Pipeline completo do motor, ponta a ponta, contra uma compilação real:
    /// AdditionalFile → parse (cacheado por instância de SourceText) → <see cref="LayerResolver"/> →
    /// roteamento por `type` → <c>IRuleEvaluator.RegisterActions</c> → diagnóstico no slot
    /// pré-declarado.
    ///
    /// A configuração é injetada já parseada (a trilha 1A ainda está implementando o YAML), e o
    /// avaliador é o <see cref="TestForbiddenNameEvaluator"/>, que vive só neste projeto de teste.
    /// O que está sob teste é o MOTOR — que o registro de avaliadores esteja vazio em produção não
    /// impede provar que ele funciona quando um avaliador existe.
    /// </summary>
    public class ArchRulesAnalyzerPipelineTests
    {
        private const string ConfigPath = "/repo/arch-rules.yaml";

        private const string YamlPlaceholder =
            "schema: arch-rules/v1\nlayers:\n  Controller:\n    match:\n      - namespace: Acme.Api.Controllers.**\n";

        private const string Source = @"
namespace Acme.Infra
{
    public class OrderRepository { public void Load() { } }
}

namespace Acme.Api.Controllers
{
    public class OrderController
    {
        public void Get() { new Acme.Infra.OrderRepository().Load(); }
    }
}

namespace Acme.Domain
{
    public class OrderService
    {
        // Controle de falso positivo: a regra é `from: Controller`, então esta chamada
        // idêntica NÃO pode virar diagnóstico.
        public void Do() { new Acme.Infra.OrderRepository().Load(); }
    }
}
";

        private static readonly LayerDefinition[] Layers =
        {
            Layer("Controller", new[] { Namespace("Acme.Api.Controllers.**") }),
            Layer("Repository", new[] { NameSuffix("Repository") }),
        };

        private static RuleDefinition ForbiddenCallRule(string slot = "ARCH0007", string id = "ACME-001", bool enabled = true)
            => Rule(id, slot, TestForbiddenNameEvaluator.TypeName, TestForbiddenNameEvaluator.Extra("Controller", "Repository"), enabled);

        private static ArchRulesAnalyzer Analyzer(Func<string, ArchConfigParseResult> parser)
            => new ArchRulesAnalyzer(
                RuleEvaluatorRegistry.Create(new TestForbiddenNameEvaluator()),
                parser);

        private static InMemoryAdditionalText ConfigFile()
            => new InMemoryAdditionalText(ConfigPath, YamlPlaceholder);

        [Fact]
        public async Task Pipeline_completo_reporta_no_slot_da_regra()
        {
            var config = Policy(Layers, new[] { ForbiddenCallRule() });
            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(Parser(config)),
                TestCompilations.Create(Source),
                ConfigFile());

            var diagnostic = Assert.Single(diagnostics);

            // O slot técnico é o que o compilador/.editorconfig/#pragma enxergam...
            Assert.Equal("ARCH0007", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);

            // ...e o ID de negócio da organização vive na mensagem (ADR-001 §3, Opção C).
            Assert.Equal(
                TestForbiddenNameEvaluator.BuildMessage(ForbiddenCallRule(), "Repository", "OrderRepository"),
                diagnostic.GetMessage());

            // Reportado na chamada feita DE DENTRO do Controller, não na chamada idêntica do Service.
            var reportedLine = diagnostic.Location.SourceTree
                .GetText()
                .Lines[diagnostic.Location.GetLineSpan().StartLinePosition.Line]
                .ToString();
            Assert.Contains("public void Get()", reportedLine);
        }

        [Fact]
        public async Task Sem_arquivo_de_config_nenhuma_acao_e_registrada()
        {
            var config = Policy(Layers, new[] { ForbiddenCallRule() });

            // Mesma config que acima produziria um diagnóstico — sem o AdditionalFile, o analyzer
            // sequer chega a olhar para ela (saída rápida do ADR-001 §5).
            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(Parser(config)),
                TestCompilations.Create(Source));

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task Regra_de_tipo_desconhecido_gera_ARCH9003()
        {
            // Nenhum avaliador registrado para `max-dependencies` — que é a situação de TODA regra
            // enquanto a Fase 2 não entrega os avaliadores (ADR-001 D4: MINOR aditivo).
            var config = Policy(Layers, new[]
            {
                Rule("ACME-004", "ARCH0004", "max-dependencies"),
            });

            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(Parser(config)),
                TestCompilations.Create(Source),
                ConfigFile());

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("ARCH9003", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Contains("ACME-004", diagnostic.GetMessage());
            Assert.Contains("max-dependencies", diagnostic.GetMessage());

            // Erro de configuração aponta para o arquivo de configuração, não para um .cs qualquer.
            Assert.Equal(ConfigPath, diagnostic.Location.GetLineSpan().Path);
        }

        [Fact]
        public async Task Registro_vazio_de_avaliadores_gera_ARCH9003_por_regra()
        {
            // Exatamente o comportamento em produção hoje (RuleEvaluatorRegistry.Default vazio):
            // uma regra, um ARCH9003 — e nenhuma regra silenciosamente ignorada.
            var config = Policy(Layers, new[]
            {
                ForbiddenCallRule("ARCH0007", "ACME-001"),
                ForbiddenCallRule("ARCH0008", "ACME-002"),
            });

            var analyzer = new ArchRulesAnalyzer(RuleEvaluatorRegistry.Default, Parser(config));
            var diagnostics = await AnalyzerRunner.RunAsync(analyzer, TestCompilations.Create(Source), ConfigFile());

            Assert.Equal(2, diagnostics.Length);
            Assert.All(diagnostics, d => Assert.Equal("ARCH9003", d.Id));
            Assert.Contains(diagnostics, d => d.GetMessage().Contains("ACME-001"));
            Assert.Contains(diagnostics, d => d.GetMessage().Contains("ACME-002"));
        }

        [Fact]
        public async Task Erro_bloqueante_reporta_ARCH9001_e_nao_registra_nenhuma_regra()
        {
            // Contrato do parser: Config é nulo quando algum erro bloqueia.
            var parser = Parser(null, new SchemaValidationError("ARCH9001", "schema: arch-rules/v2 não suportado", 3, 5, true));

            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(parser),
                TestCompilations.Create(Source),
                ConfigFile());

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("ARCH9001", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);

            // Linha/coluna do parser preservadas (ADR-001 D4: apontar linha/coluna, não avalanche).
            var lineSpan = diagnostic.Location.GetLineSpan();
            Assert.Equal(ConfigPath, lineSpan.Path);
            Assert.Equal(2, lineSpan.StartLinePosition.Line);
            Assert.Equal(4, lineSpan.StartLinePosition.Character);

            // Nenhum diagnóstico de slot: config inválida invalida tudo.
            Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("ARCH0", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Erro_nao_bloqueante_nao_impede_as_demais_regras()
        {
            var config = Policy(Layers, new[] { ForbiddenCallRule() });
            var parser = Parser(config, new SchemaValidationError("ARCH9003", "regra 'ACME-009' ignorada", 12, 1, false));

            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(parser),
                TestCompilations.Create(Source),
                ConfigFile());

            Assert.Equal(2, diagnostics.Length);
            Assert.Contains(diagnostics, d => d.Id == "ARCH9003" && d.Severity == DiagnosticSeverity.Warning);
            Assert.Contains(diagnostics, d => d.Id == "ARCH0007");
        }

        [Fact]
        public async Task Codigo_de_erro_desconhecido_do_parser_nao_vira_AD0001()
        {
            // Um Arch.Config mais novo pode reportar um código que este analyzer ainda não declara.
            // Reportá-lo como ele mesmo seria AD0001 (ID fora de SupportedDiagnostics); o motor cai
            // para um ID da faixa declarada e preserva o código original na mensagem.
            var parser = Parser(null, new SchemaValidationError("ARCH9099", "erro de uma versão futura", 1, 1, true));

            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(parser),
                TestCompilations.Create(Source),
                ConfigFile());

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("ARCH9001", diagnostic.Id);
            Assert.Contains("ARCH9099", diagnostic.GetMessage());
        }

        [Fact]
        public async Task Excecao_do_parser_vira_ARCH9001_em_vez_de_derrubar_o_analyzer()
        {
            Func<string, ArchConfigParseResult> parser = _ => throw new InvalidOperationException("boom");

            // AnalyzerRunner falha o teste se aparecer AD0001 — que é como uma exceção vazada
            // apareceria para o consumidor.
            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(parser),
                TestCompilations.Create(Source),
                ConfigFile());

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("ARCH9001", diagnostic.Id);
            Assert.Contains("boom", diagnostic.GetMessage());
        }

        [Fact]
        public async Task Slot_fora_do_pool_gera_ARCH9002()
        {
            var config = Policy(Layers, new[] { ForbiddenCallRule("ARCH0900") });

            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(Parser(config)),
                TestCompilations.Create(Source),
                ConfigFile());

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("ARCH9002", diagnostic.Id);
            Assert.Contains("ARCH0900", diagnostic.GetMessage());
        }

        [Fact]
        public async Task Slot_duplicado_desliga_apenas_a_segunda_regra()
        {
            var config = Policy(Layers, new[]
            {
                ForbiddenCallRule("ARCH0007", "ACME-001"),
                ForbiddenCallRule("ARCH0007", "ACME-002"),
            });

            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(Parser(config)),
                TestCompilations.Create(Source),
                ConfigFile());

            Assert.Equal(2, diagnostics.Length);
            Assert.Contains(diagnostics, d => d.Id == "ARCH0007" && d.GetMessage().Contains("ACME-001"));
            Assert.Contains(diagnostics, d => d.Id == "ARCH9002" && d.GetMessage().Contains("ACME-002"));
        }

        [Fact]
        public async Task Regra_desabilitada_nao_registra_acao_nem_reporta_erro()
        {
            var config = Policy(Layers, new[] { ForbiddenCallRule(enabled: false) });

            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(Parser(config)),
                TestCompilations.Create(Source),
                ConfigFile());

            Assert.Empty(diagnostics);
        }

        [Fact]
        public async Task Tipo_nao_resolvido_gera_um_unico_ARCH9004()
        {
            var layers = new[]
            {
                Layer("Controller", new[] { Namespace("Acme.Api.Controllers.**") }),
                Layer("Repository", new[] { NameSuffix("Repository"), Implements("Acme.Nope.IGhost") }),
            };

            var config = Policy(layers, new[] { ForbiddenCallRule() });

            var diagnostics = await AnalyzerRunner.RunAsync(
                Analyzer(Parser(config)),
                TestCompilations.Create(Source),
                ConfigFile());

            // Uma vez por NOME, não por símbolo (a compilação tem vários tipos candidatos).
            var unresolved = Assert.Single(diagnostics.Where(d => d.Id == "ARCH9004"));
            Assert.Contains("Acme.Nope.IGhost", unresolved.GetMessage());

            // E o critério restante (`nameSuffix`) continua valendo: a regra ainda dispara.
            Assert.Contains(diagnostics, d => d.Id == "ARCH0007");
        }

        [Fact]
        public async Task Config_e_parseada_uma_vez_por_instancia_de_SourceText()
        {
            var parseCount = 0;
            var config = Policy(Layers, new[] { ForbiddenCallRule() });
            Func<string, ArchConfigParseResult> countingParser = _ =>
            {
                parseCount++;
                return new ArchConfigParseResult(config, Array.Empty<SchemaValidationError>());
            };

            var analyzer = Analyzer(countingParser);

            var sourceText = SourceText.From(YamlPlaceholder);
            var additionalFile = new InMemoryAdditionalText(ConfigPath, sourceText);

            // Duas compilações diferentes (o que a IDE faz a cada tecla), MESMA instância de
            // SourceText no AdditionalFile — que é o que a IDE reaproveita enquanto o arquivo de
            // configuração não muda.
            await AnalyzerRunner.RunAsync(analyzer, TestCompilations.Create(Source, "A"), additionalFile);
            await AnalyzerRunner.RunAsync(analyzer, TestCompilations.Create(Source + "\n// edição\n", "B"), additionalFile);

            Assert.Equal(1, parseCount);

            // Conteúdo idêntico, INSTÂNCIA nova de SourceText (arquivo reaberto/editado): reparseia.
            // É o comportamento correto — a chave é a identidade do texto, nunca o caminho.
            var reopened = new InMemoryAdditionalText(ConfigPath, SourceText.From(YamlPlaceholder));
            await AnalyzerRunner.RunAsync(analyzer, TestCompilations.Create(Source, "C"), reopened);

            Assert.Equal(2, parseCount);
        }

        [Fact]
        public async Task Resultado_cacheado_mantem_os_erros_nao_bloqueantes_em_toda_compilacao()
        {
            // Regressão: se o cache guardasse só a ArchConfig, o ARCH9003 apareceria na primeira
            // compilação e sumiria nas seguintes — diagnóstico intermitente.
            var config = Policy(Layers, new[] { ForbiddenCallRule() });
            var parser = Parser(config, new SchemaValidationError("ARCH9003", "regra 'ACME-009' ignorada", 1, 1, false));
            var analyzer = Analyzer(parser);
            var additionalFile = ConfigFile();

            var first = await AnalyzerRunner.RunAsync(analyzer, TestCompilations.Create(Source, "A"), additionalFile);
            var second = await AnalyzerRunner.RunAsync(analyzer, TestCompilations.Create(Source, "B"), additionalFile);

            Assert.Contains(first, d => d.Id == "ARCH9003");
            Assert.Contains(second, d => d.Id == "ARCH9003");
        }
    }
}
