using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Arch.Analyzer.Tests
{
    /// <summary>
    /// Resolução de `extends:` DENTRO DO ANALYZER (ADR-001 D2). Até a Fase 4 o motor chamava
    /// <c>ArchConfigParser.Parse(string)</c> sobre o texto do arquivo principal e ignorava `extends`
    /// por completo: uma política herdada compilava limpa e simplesmente não valia — sem nenhum
    /// aviso. O CLI (`arch-rules gen`/`validate`) sempre resolveu a cadeia, o que tornava o silêncio
    /// ainda mais traiçoeiro (CI verde no `validate`, analyzer mudo no build).
    ///
    /// <para>Estes testes usam o PARSER REAL e os AVALIADORES REAIS (construtor público de
    /// <see cref="ArchRulesAnalyzer"/>), porque é exatamente a integração — analyzer → PeekExtends →
    /// casamento de caminho contra AdditionalFiles → <c>Parse(IReadOnlyList&lt;string&gt;)</c> — que
    /// está sob teste. Injetar uma config pronta (como fazem os testes de pipeline) não provaria
    /// nada aqui.</para>
    ///
    /// <para>O analyzer não pode fazer I/O (RS1035): tudo o que ele enxerga são os AdditionalFiles.
    /// Por isso cada caso monta os arquivos em memória com caminhos plausíveis de um projeto real
    /// (<c>/repo/arch-rules.yaml</c>, <c>/repo/arch-rules.base.yaml</c>) e o casamento é feito por
    /// string normalizada, não pelo disco.</para>
    /// </summary>
    public class ExtendsResolutionTests
    {
        private const string MainPath = "/repo/arch-rules.yaml";
        private const string BasePath = "/repo/arch-rules.base.yaml";
        private const string MidPath = "/repo/arch-rules.mid.yaml";

        /// <summary>
        /// Controller chamando Repository diretamente: a violação clássica de `forbidden-call`.
        /// O método é ESTÁTICO de propósito — `new Repo().Load()` produziria duas operações
        /// (ObjectCreation + Invocation) e, portanto, dois diagnósticos, o que só tornaria a
        /// contagem destes testes ambígua sem provar nada sobre `extends`.
        /// </summary>
        private const string Source = @"
namespace Contoso.Infra
{
    public class OrderRepository { public static void Load() { } }
}

namespace Contoso.Api
{
    public class OrderController
    {
        public void Get() { Contoso.Infra.OrderRepository.Load(); }
    }
}
";

        private const string LayersAndSeverities = @"schema: arch-rules/v1
layers:
  Controller:
    match:
      - nameSuffix: Controller
  Repository:
    match:
      - nameSuffix: Repository
severities:
  aviso: warning
defaults:
  severity: aviso
";

        private static async Task<ImmutableArray<Diagnostic>> RunAsync(params (string Path, string Yaml)[] files)
        {
            var additionalFiles = files
                .Select(file => (AdditionalText)new InMemoryAdditionalText(file.Path, file.Yaml))
                .ToArray();

            // Construtor público = parser de produção (ArchConfigParser.Parse da CADEIA) +
            // RuleEvaluatorRegistry.Default (os 4 avaliadores reais da Fase 2).
            return await AnalyzerRunner.RunAsync(new ArchRulesAnalyzer(), TestCompilations.Create(Source), additionalFiles);
        }

        private static string[] Ids(ImmutableArray<Diagnostic> diagnostics)
            => diagnostics.Select(d => d.Id).ToArray();

        /// <summary>
        /// O caso que motivou a correção: a regra existe SÓ no arquivo base, e o `arch-rules.yaml`
        /// do projeto só faz `extends`. Antes, zero diagnóstico.
        /// </summary>
        [Fact]
        public async Task Regra_declarada_so_no_arquivo_base_dispara_via_extends()
        {
            var baseYaml = LayersAndSeverities + @"rules:
  - id: BASE-001
    slot: ARCH0011
    type: forbidden-call
    from: Controller
    to: Repository
";

            var mainYaml = @"schema: arch-rules/v1
extends: [./arch-rules.base.yaml]
";

            var diagnostics = await RunAsync((BasePath, baseYaml), (MainPath, mainYaml));

            var reported = Assert.Single(diagnostics);
            Assert.Equal("ARCH0011", reported.Id);
            Assert.Contains("BASE-001", reported.GetMessage());
        }

        /// <summary>
        /// `extends` apontando para um arquivo que não está nos AdditionalFiles (o caso de quem usa
        /// um nome fora da convenção `arch-rules.*.yaml` e não acrescentou a linha no .csproj).
        ///
        /// Decisão de design sob teste (documentada em <c>ArchRulesAnalyzer.ResolveChain</c>): o
        /// ramo ausente é DESCARTADO, não bloqueia. O ARCH9006 diz o que faltou, e tudo o que o
        /// arquivo principal consegue expressar sozinho continua valendo — o oposto (invalidar a
        /// config) devolveria o projeto ao silêncio total que esta correção elimina.
        /// </summary>
        [Fact]
        public async Task Extends_sem_AdditionalFile_reporta_ARCH9006_e_mantem_o_arquivo_principal_valendo()
        {
            var mainYaml = LayersAndSeverities.Replace(
                    "schema: arch-rules/v1\n",
                    "schema: arch-rules/v1\nextends: [./politica-corporativa.yaml]\n")
                + @"rules:
  - id: MAIN-001
    slot: ARCH0011
    type: forbidden-call
    from: Controller
    to: Repository
";

            var diagnostics = await RunAsync((MainPath, mainYaml));

            Assert.Contains("ARCH9006", Ids(diagnostics));

            var missing = diagnostics.Single(d => d.Id == "ARCH9006");
            Assert.Contains("politica-corporativa.yaml", missing.GetMessage());
            Assert.Equal(DiagnosticSeverity.Warning, missing.Severity);

            // A regra do arquivo principal continua sendo avaliada — nada de "config parcialmente
            // inválida significa nenhuma regra".
            var violation = diagnostics.Single(d => d.Id == "ARCH0011");
            Assert.Contains("MAIN-001", violation.GetMessage());
        }

        /// <summary>
        /// Ciclo A → B → A. O ramo que fecharia o ciclo é cortado (ARCH9007) e a resolução termina:
        /// sem isso, a recursão só pararia com StackOverflowException, que derruba o processo do
        /// compilador inteiro — nem AD0001 sobraria. O Timeout existe para que uma regressão de
        /// travamento falhe o teste em vez de pendurar a suíte.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task Ciclo_de_extends_e_cortado_com_ARCH9007_sem_travar()
        {
            var mainYaml = LayersAndSeverities.Replace(
                    "schema: arch-rules/v1\n",
                    "schema: arch-rules/v1\nextends: [./arch-rules.base.yaml]\n")
                + @"rules:
  - id: MAIN-001
    slot: ARCH0011
    type: forbidden-call
    from: Controller
    to: Repository
";

            var baseYaml = @"schema: arch-rules/v1
extends: [./arch-rules.yaml]
";

            var diagnostics = await RunAsync((BasePath, baseYaml), (MainPath, mainYaml));

            var cycle = diagnostics.Single(d => d.Id == "ARCH9007");
            Assert.Contains("Ciclo", cycle.GetMessage());

            // Os dois documentos continuam compondo a política: o ciclo corta o RAMO, não a cadeia.
            Assert.Contains("ARCH0011", Ids(diagnostics));
        }

        /// <summary>
        /// Cadeia de 3 níveis (avô → pai → filho), a mesma semântica que <c>Arch.Cli.Tests</c> já
        /// cobre para o CLI — agora provada no ANALYZER: ordem CRESCENTE de precedência, o arquivo
        /// que faz `extends` vence, merge de `rules` por `id` (ADR-001 D2).
        ///
        /// A prova é dupla: (a) a regra que só o avô declara continua valendo no fim da cadeia, e
        /// (b) a regra redeclarada nos três níveis fica com o `message` do FILHO — se a ordem
        /// estivesse invertida, a mensagem do avô venceria e o teste falharia mostrando exatamente
        /// isso.
        /// </summary>
        [Fact]
        public async Task Cadeia_de_tres_niveis_respeita_a_ordem_crescente_de_precedencia()
        {
            var grandparentYaml = LayersAndSeverities + @"rules:
  - id: SHARED-001
    slot: ARCH0011
    type: forbidden-call
    from: Controller
    to: Repository
    message: ""mensagem do avo""
  - id: ONLY-GRANDPARENT
    slot: ARCH0012
    type: naming-convention
    layer: Repository
    require: { suffix: Repositorio }
";

            var midYaml = @"schema: arch-rules/v1
extends: [./arch-rules.base.yaml]
rules:
  - id: SHARED-001
    slot: ARCH0011
    type: forbidden-call
    from: Controller
    to: Repository
    message: ""mensagem do pai""
";

            var childYaml = @"schema: arch-rules/v1
extends: [./arch-rules.mid.yaml]
rules:
  - id: SHARED-001
    slot: ARCH0011
    type: forbidden-call
    from: Controller
    to: Repository
    message: ""mensagem do filho""
";

            var diagnostics = await RunAsync(
                (BasePath, grandparentYaml),
                (MidPath, midYaml),
                (MainPath, childYaml));

            var shared = diagnostics.Single(d => d.Id == "ARCH0011");
            Assert.Contains("mensagem do filho", shared.GetMessage());
            Assert.DoesNotContain("mensagem do avo", shared.GetMessage());

            // OrderRepository não termina em "Repositorio": a regra que só o avô declara sobreviveu
            // aos dois níveis de merge e continua reportando.
            var onlyGrandparent = diagnostics.Single(d => d.Id == "ARCH0012");
            Assert.Contains("ONLY-GRANDPARENT", onlyGrandparent.GetMessage());
        }

        /// <summary>
        /// Controle negativo do casamento de caminho: o `extends` é relativo ao diretório do arquivo
        /// QUE O DECLARA. Um arquivo homônimo em OUTRO diretório não pode ser confundido com o
        /// esperado — se a resolução ignorasse o diretório e casasse só por nome de arquivo, este
        /// teste passaria a herdar a política errada em silêncio.
        /// </summary>
        [Fact]
        public async Task Extends_relativo_nao_casa_arquivo_homonimo_de_outro_diretorio()
        {
            var outroDiretorio = LayersAndSeverities + @"rules:
  - id: OUTRO-001
    slot: ARCH0011
    type: forbidden-call
    from: Controller
    to: Repository
";

            var mainYaml = @"schema: arch-rules/v1
extends: [./arch-rules.base.yaml]
";

            var diagnostics = await RunAsync(
                ("/outro-projeto/arch-rules.base.yaml", outroDiretorio),
                (MainPath, mainYaml));

            Assert.Contains("ARCH9006", Ids(diagnostics));
            Assert.DoesNotContain("ARCH0011", Ids(diagnostics));
        }

        /// <summary>
        /// Rootedness mista, o caso que um build real produz: o MSBuild entrega relativizados os
        /// AdditionalFiles que ficam DENTRO da pasta do projeto (verificado com `dotnet build` de
        /// verdade — o `arch-rules.yaml` incluído como caminho absoluto pelo .targets do pacote
        /// chega ao analyzer como "arch-rules.yaml"), enquanto um arquivo compartilhado fora dessa
        /// pasta pode chegar absoluto. Sem o diretório do projeto, alinhar os dois exige o fallback
        /// por cauda de caminho — e ele só vale quando um único candidato bate.
        /// </summary>
        [Fact]
        public async Task Extends_casa_quando_o_AdditionalFile_chega_absoluto_e_o_principal_relativo()
        {
            var baseYaml = LayersAndSeverities + @"rules:
  - id: CORP-001
    slot: ARCH0011
    type: forbidden-call
    from: Controller
    to: Repository
";

            var mainYaml = @"schema: arch-rules/v1
extends: [../corporativo/arch-rules.base.yaml]
";

            var diagnostics = await RunAsync(
                ("/srv/corporativo/arch-rules.base.yaml", baseYaml),
                ("arch-rules.yaml", mainYaml));

            var reported = Assert.Single(diagnostics);
            Assert.Equal("ARCH0011", reported.Id);
            Assert.Contains("CORP-001", reported.GetMessage());
        }

        /// <summary>
        /// Caminho com separador do Windows e segmentos redundantes (<c>..</c>, <c>.</c>) precisa
        /// casar o mesmo AdditionalFile — a normalização é lexical e roda igual nos dois sistemas
        /// operacionais, já que o analyzer não pode perguntar ao disco.
        /// </summary>
        [Fact]
        public async Task Caminho_de_extends_e_normalizado_antes_de_casar_o_AdditionalFile()
        {
            var baseYaml = LayersAndSeverities + @"rules:
  - id: BASE-001
    slot: ARCH0011
    type: forbidden-call
    from: Controller
    to: Repository
";

            var mainYaml = @"schema: arch-rules/v1
extends: ['.\politica\..\arch-rules.base.yaml']
";

            var diagnostics = await RunAsync((BasePath, baseYaml), (MainPath, mainYaml));

            var reported = Assert.Single(diagnostics);
            Assert.Equal("ARCH0011", reported.Id);
        }
    }
}
