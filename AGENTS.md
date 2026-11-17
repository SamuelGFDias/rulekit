# AGENTS.md — RuleKit

Contexto de referência rápida para qualquer agente que trabalhe neste repositório. Leia isto antes
de pedir para reler o ADR ou o Plano inteiros; volte às fontes completas só quando precisar de
detalhe que não esteja aqui. Para uso da lib, veja o `README.md`; para compilar/testar/publicar, o
`CONTRIBUTING.md`; o que cada versão entrega, o `CHANGELOG.md`.

## 1. Visão geral

RuleKit é uma lib .NET distribuída como Roslyn Analyzer configurável que valida padrões
arquiteturais em tempo de compilação. A lib não embute nenhuma regra de negócio: cada organização
declara sua própria política num arquivo YAML externo (`arch-rules.yaml`) e recebe diagnósticos de
violação no `dotnet build`, no CI e (a verificar — ver §6) ao vivo no Error List da IDE — sem fork
da lib e sem regra hardcoded por empresa. Dois pacotes NuGet: `RuleKit.Analyzer` e `RuleKit.Cli`
(comando `arch-rules`).

## 2. Decisão arquitetural central: pool de slots

O ponto de projeto que define toda a arquitetura: o contrato do Roslyn é rígido onde o requisito
pede flexibilidade.

- `DiagnosticAnalyzer.SupportedDiagnostics` é lida **sem contexto de compilação** (antes de
  `AdditionalFiles`/YAML estarem disponíveis) e é **cacheada por assembly do analyzer durante toda a
  sessão da IDE**.
- Reportar um diagnóstico com ID não declarado ali não aparece: gera `AD0001` silenciosamente.
- Um ID como `ACME-001` (com hífen) nunca poderia ser um ID de diagnóstico Roslyn real de qualquer
  forma — `IsValidIdentifier` rejeita.

**Solução (D2 do ADR, Opção C escolhida):** a lib pré-declara N descriptors estáveis
`ARCH0001..ARCH0512` (512 cobre qualquer política realista). O YAML de configuração **mapeia** o ID
de negócio da organização a um desses slots técnicos:

```yaml
- id: ACME-001      # identidade da organização — aparece na mensagem e em Diagnostic.Properties
  slot: ARCH0007    # identidade técnica — é o que o compilador, .editorconfig e #pragma enxergam
```

Isso dá override de severidade por regra individual (`dotnet_diagnostic.ARCH0007.severity = ...`),
e permite `#pragma warning disable ARCH0007`. Como os descriptors são conhecidos sem build, a
intenção é que isso funcione também em live analysis na IDE — **ainda não verificado** (ver §6). O custo aceito é que o
slot é um "número mágico" no YAML — mitigado pelo CLI (`arch-rules validate`/`gen`), que **valida**
slots únicos dentro do pool e emite o `.globalconfig` e o lock file. **O CLI não aloca slots**: quem
escolhe o slot é o autor do YAML. Opções alternativas descartadas: descriptors construídos em
runtime (inviável — vira sempre `AD0001`) e um descriptor fixo por *tipo* de regra (mata
granularidade de override por regra). Uma MSBuild task que aloca slots automaticamente (Opção D) foi
**adiada para v2**, não rejeitada — v1 prioriza feedback ao vivo na IDE sobre ergonomia de alocação.

## 3. As quatro decisões estruturais do ADR

| # | Ponto em aberto | Decisão |
|---|---|---|
| D1 | Formato do arquivo de config | YAML, com parser (YamlDotNet) fundido no assembly via ILRepack `/internalize` — nunca DLL separada |
| D2 | Sintaxe de camadas e regras | `layers` por critérios combinados via OR + `rules` tipadas, usando o pool de slots (seção 2) |
| D3 | Nomenclatura dos níveis de severidade | Nomes livres definidos pelo consumidor (`severities:` no YAML); a severidade **real** que o compilador aplica vive no `.editorconfig`/`.globalconfig`, gerado a partir do YAML |
| D4 | Versionamento de schema | `schema: arch-rules/v1` obrigatório; MAJOR quebra o arquivo inteiro (`ARCH9001`), MINOR é aditivo (tipo desconhecido gera `ARCH9003` e não invalida o resto) |

Detalhes completos, exemplos de YAML e trade-offs: `ADR-001-motor-regras-arquiteturais.md`.

## 4. Estrutura de pastas (real, v0.1.0)

```
src/
  Arch.Analyzer/                 # netstandard2.0 — PackageId RuleKit.Analyzer (assembly/namespaces
                                  # continuam Arch.Analyzer). ArchRulesAnalyzer, pool de 512 descriptors
                                  # (ARCH0001..ARCH0512) + faixa ARCH9xxx (ArchDiagnostics),
                                  # LayerResolver/NamespaceTrie, RuleEvaluatorRegistry (despacho),
                                  # SeverityDivergence (ARCH9005). EnforceExtendedAnalyzerRules ligado
                                  # (RS1035: sem I/O).
  Arch.Analyzer.Contracts/       # netstandard2.0 — IRuleEvaluator/ILayerResolver. Projeto próprio para
                                  # evitar ciclo Arch.Analyzer <-> Arch.Rules (ver seção 8).
  Arch.Config/                   # netstandard2.0, sem Roslyn — modelo (ArchConfig/LayerDefinition/
                                  # LayerMatchCriterion/RuleDefinition/RulePropertyValue),
                                  # ArchConfigParser (YamlDotNet, em Internal/), RuleTypeCatalog,
                                  # SlotPool, SeverityVocabulary, ExtendsChainResolver.
  Arch.Rules/                    # netstandard2.0 — um IRuleEvaluator por arquivo: ForbiddenCall,
                                  # MustRouteThrough (herda do anterior), NamingConvention,
                                  # MaxDependencies. Referencia só Contracts + Config.
  Arch.Cli/                      # net8.0;net10.0, PackAsTool — PackageId RuleKit.Cli, comando arch-rules,
                                  # RollForward=Major (só rola para runtimes MAIS NOVOS: exige .NET 8+;
                                  # o pack traz tools/net8.0/any e tools/net10.0/any).
                                  # `gen <yaml> [--out <dir>] [--strict]` e `validate <yaml> [--strict]`: resolvem extends do
                                  # disco (ExtendsResolver = loader de disco sobre ExtendsChainResolver),
                                  # validam schema/slots/severidades e `gen` escreve .globalconfig e
                                  # arch-rules.lock.yaml. Sem dependência direta de YamlDotNet.
tests/
  Arch.Config.Tests/             # parser + catálogo, sem Roslyn — 90 testes
  Arch.Analyzer.Tests/           # harness (código + config) -> diagnósticos — 84 testes
  Arch.Rules.Tests/              # testes dos 4 avaliadores — 32 testes
  Arch.Cli.Tests/                # testes do CLI (gen/validate) — 50 testes
  Arch.TestHarness/              # compilação in-memory reutilizável — 4 testes próprios
  Arch.Benchmarks/               # BenchmarkDotNet (memoização do LayerResolver, cache de config); só compila no CI
  Arch.Dogfood/                  # Arch.Dogfood.Sample consome o analyzer por .nupkg real (não
                                  # ProjectReference) — fora da RuleKit.sln; versão por
                                  # -p:RuleKitAnalyzerVersion
build/
  ilrepack.targets               # funde as 5 DLLs (Arch.Analyzer + YamlDotNet + Arch.Config +
                                  # Arch.Analyzer.Contracts + Arch.Rules) com /internalize, SÓ durante
                                  # `dotnet pack` (BeforeTargets="_GetPackageFiles"), nunca num build
                                  # comum — projetos que usam ProjectReference nunca veem o assembly
                                  # internalizado (evita CS0433).
  RuleKit.Analyzer.targets       # empacotado em build/ no .nupkg (nome PRECISA ser build/<PackageId>.targets).
                                  # Adiciona arch-rules.yaml e arch-rules.*.yaml (exceto o .lock.yaml)
                                  # a @(AdditionalFiles) em todo projeto que instale o pacote.
eng/perf/                        # solução sintética de referência (generate.sh) e medição do orçamento
                                  # de < 5% de overhead (measure.sh); metodologia em eng/perf/README.md
skills/rulekit-arch-rules/       # skill de agente de IA para QUEM CONSOME os pacotes (SKILL.md + references/:
                                  # YAML, diagnósticos, CLI/CI). Ao mudar comportamento visível ao consumidor
                                  # (campos, códigos ARCH9xxx, opções do CLI), atualizar a skill junto.
docs/schema/arch-rules.v1.json   # JSON Schema (draft 2020-12), $id resolvível em raw.githubusercontent.com,
                                  # alinhado ao RuleTypeCatalog
.github/workflows/
  ci.yml                         # jobs: build-test (build, `dotnet test`, pack), smoke (dogfood contra o
                                  # .nupkg + dotnet tool), windows-vs (MSBuild/csc .NET Framework do
                                  # Visual Studio, RoslynCompilerType=Framework; falha se não provar o
                                  # host ou se aparecer AD0001), perf (gate de 5%, mediana de pares)
  publish.yml                    # tag v* -> build/test/pack -> nuget.org (Trusted Publishing) -> Release
Directory.Build.props            # metadados de pacote, MinVer (tag prefix `v`), SourceLink — comuns a todos
Directory.Packages.props         # Central Package Management: versões SÓ aqui (Roslyn 4.8.0,
                                  # YamlDotNet 13.1.0, MinVer 8.0.0, ILRepack.Lib.MSBuild.Task 2.0.46)
global.json                      # SDK 10.0.100, rollForward latestFeature
README.md                        # orientado a consumidor; é o PackageReadmeFile dos dois pacotes
CONTRIBUTING.md / CHANGELOG.md   # desenvolvimento / histórico de versões
ADR-001-...md / PLANO-implementacao.md   # decisão de arquitetura / plano original (v1 concluído)
```

`Arch.Config`, `Arch.Rules` e `Arch.Analyzer.Contracts` são fundidos em `Arch.Analyzer.dll` no pack —
a separação existe para testabilidade e para evitar ciclos, não para distribuição. O `README.md` é
empacotado nos dois `.nupkg`: **só URLs absolutas** (links relativos quebram no nuget.org).

## 5. Convenções técnicas

- **Target frameworks:** `netstandard2.0` para as libs do analyzer (`Arch.Analyzer`, `Arch.Config`,
  `Arch.Rules`, `Arch.Analyzer.Contracts` — precisam rodar dentro do host do compilador). `net8.0;net10.0`
  para `Arch.Cli` (ferramenta externa, sem essa restrição) e `net10.0` para os testes. Roslyn fixado em 4.8.0
  (menor versão suportada: SDK .NET 8+ / VS 2022 17.8+).
- **Versão dos pacotes:** MinVer a partir de tags `vX.Y.Z`; nunca fixar `<Version>` num csproj.
- **Parser YAML:** YamlDotNet, fundido no assembly do analyzer via ILRepack `/internalize`, virando
  tipos `internal`. **Nunca distribuído como DLL separada em `analyzers/dotnet/cs`** — isso é o que
  elimina a classe de falhas do roslyn#41421 (`FileLoadException` quando dois analyzers carregam
  versões diferentes do mesmo assembly).
- **Faixa `ARCH9xxx`** é reservada para erros do próprio motor — sempre habilitada por padrão,
  **nunca alocável por configuração** do consumidor. Não recebe `WellKnownDiagnosticTags.NotConfigurable`
  (essa tag bloquearia `#pragma`/`.editorconfig`, e o time precisa poder silenciar pontualmente um
  aviso do motor).
- YAML aceito é um subset restrito: sem anchors/aliases, sem multi-documento, sem tags customizadas;
  chave desconhecida é erro de schema, não silêncio.
- O analyzer não faz I/O (RS1035): tudo que lê vem de `AdditionalFiles`.

## 6. Estado atual do projeto

**v0.1.0 pronto para publicação.** Fases 0–4 do `PLANO-implementacao.md` concluídas e o ciclo de
preparação para o nuget.org fechado (pacotes renomeados, catálogo de tipos, `ARCH9005`/`ARCH9008`
reais, resolvedor único de `extends`, CI com Windows/VS e perf, publicação por Trusted Publishing,
README/CHANGELOG/CONTRIBUTING). O que falta é só o ato de release (seção 10): tag `v0.1.0` num commit
da `main`.

Gate verificado (`dotnet test RuleKit.sln -c Release`): **260 testes aprovados, 0 falhas** —
`Arch.Config.Tests` 90 + `Arch.Analyzer.Tests` 84 + `Arch.Rules.Tests` 32 + `Arch.Cli.Tests` 50 +
`Arch.TestHarness` 4.

Pendências conhecidas (não bloqueiam a v0.1.0):
- **#1** — live analysis do Visual Studio (Error List durante a digitação) não verificada. O build no
  host .NET Framework do VS já é provado a cada push (job `windows-vs`).
- **#20** (v2) — erros do motor saem em `RegisterCompilationEndAction`, que pode não rodar na live
  analysis padrão; proposta: `RegisterAdditionalFileAction`.
- **#21** (v2) — sem extensão de terceiros: `RuleEvaluatorRegistry` é `internal` e Contracts/Rules são
  internalizados no pack.
- `ARCH9005` ignora `.editorconfig` por árvore e `TreatWarningsAsErrors` (documentado no README).

Histórico (commits): Fase 0 `fe902e6`, Fase 1 `73e9bf4`, Fase 2 `937fec8`, Fase 3 `7d08264`, Fase 4
`55adb7c`; ciclo de publicação `38eca4f`…`287c86e`.

## 7. Regra de processo

O projeto é conduzido pela skill `arquiteto`: o orquestrador **nunca escreve código diretamente**.
Ele decompõe o trabalho em subtarefas, delega via subagentes (sem sub-delegação — nenhum subagente
invoca outro), escolhe o modelo mais econômico que atenda cada tarefa (Haiku para trabalho mecânico,
Sonnet para o grosso, Opus só onde correção semântica do Roslyn ou revisão adversarial justificam) e
fixa contratos compartilhados (interfaces, trechos do ADR) no prompt de cada delegação. Cada fase do
`PLANO-implementacao.md` tem um gate de saída explícito que precisa fechar — todos os itens marcados
— antes da próxima fase começar.

## 8. Contratos e convenções estabelecidos

- **Separação de dependências entre modelo e contratos Roslyn.** O modelo de dados (`ArchConfig`,
  `LayerDefinition`, `LayerMatchCriterion`, `RuleDefinition`) mora em `Arch.Config` e não depende de
  Roslyn. `ILayerResolver`/`IRuleEvaluator` moram em `Arch.Analyzer.Contracts`, porque dependem de
  tipos do Roslyn. A cadeia é linear: `Arch.Analyzer` → `Arch.Rules` → `Arch.Analyzer.Contracts` →
  `Arch.Config` (e `Arch.Analyzer` também referencia Contracts diretamente) — se os contratos
  vivessem em `Arch.Analyzer`, fechariam o ciclo `Arch.Analyzer` → `Arch.Rules` → `Arch.Analyzer`.
- **`RuleTypeCatalog` (`src/Arch.Config/RuleTypeCatalog.cs`) é a fonte de verdade de tipos de regra e
  de seus campos** (nome, forma — escalar/lista/inteiro/par —, obrigatoriedade, valores aceitos).
  `RuleTypeCatalog.Default` traz os 4 tipos de v1; `TryGetType` ignora caixa e espaços nas pontas e o
  tipo é gravado em `RuleDefinition.Type` na forma canônica. **O parser valida contra o catálogo**:
  tipo desconhecido → `ARCH9003` (não bloqueante, campos nem examinados); tipo conhecido com campo
  obrigatório ausente, chave que o tipo não aceita ou valor inválido → `ARCH9008` (não bloqueante, a
  regra é descartada). `RuleEvaluatorRegistry.Default` (analyzer) é só **despacho** — mantém um
  `ARCH9003` de fallback para uma regra cujo tipo o catálogo conhece mas que não tem avaliador — e o
  teste `RuleEvaluatorRegistryDefaultTests` garante que catálogo e registro declaram os mesmos tipos.
  Um novo tipo de regra (v2+) precisa de: um `RuleTypeSchema` no catálogo, um `IRuleEvaluator` em
  `Arch.Rules` e a entrada em `Default`.
- **`RuleDefinition.Properties`** (`IReadOnlyDictionary<string, RulePropertyValue>`, chaves sensíveis a
  caixa) carrega os campos específicos já **tipados** na forma do catálogo. `RuleDefinition.Extra`
  (texto) é **derivado** do mesmo dado, por compatibilidade: lista vira `"A,B"` (`string.Join(",", ...)`,
  sem espaço), par vira `"chave=valor"`, inteiro em cultura invariante. Os acessores `GetString`/
  `GetList`/`TryGetInt`/`TryGetKeyValue` leem das duas formas (uma regra montada só com `extra` recebe
  `Properties` escalares). `RuleDefinition.FromProperties` é a fábrica que o parser usa.
  `SourceLine`/`SourceColumn`/`SourceDocumentIndex` ancoram diagnósticos no arquivo da cadeia que
  declarou a regra.
- **`SlotPool`** (`src/Arch.Config/SlotPool.cs`) — tamanho do pool (512), formato `ARCH` + 4 dígitos ASCII
  e normalização (trim, sem diferenciar caixa) numa só fonte, usada pelo parser, por
  `ArchDiagnostics.TryGetSlot` (analyzer) e por `SlotValidator` (CLI) — os três dão o mesmo veredito.
  **`SeverityVocabulary`** (`src/Arch.Config/SeverityVocabulary.cs`) — `error`/`warning`/`suggestion`/
  `silent`/`none` (nunca `info`/`hidden`) e `Resolve(config, rule)` (nome livre → valor), usado pelo CLI
  (`SeverityResolver`) e por `SeverityDivergence` (analyzer).
- **`ExtendsChainResolver`** (`src/Arch.Config/ExtendsChainResolver.cs`) é o resolvedor **único** da cadeia
  de `extends`, usado pelo analyzer (loader sobre `@(AdditionalFiles)`, sem I/O — `ArchRulesAnalyzer.cs`)
  e pelo CLI (loader de disco — `ExtendsResolver.cs`). Não faz I/O: recebe `loader` e `combinePath`.
  Travessia em profundidade e pós-ordem (o arquivo estendido entra antes de quem o estende → ordem
  crescente de precedência). Limites `DefaultMaxDepth = 32` níveis / `DefaultMaxTexts = 64` arquivos;
  problemas tipados (`Missing`/`Cycle`/`DepthExceeded`) cortam só o ramo. Política de erro é do
  chamador: warning não bloqueante no analyzer (`ARCH9006`/`ARCH9007`), erro bloqueante no CLI.
- **Ponto de entrada do parser.** `Arch.Config.ArchConfigParser.Parse(string)` e `Parse(IReadOnlyList<string>)`
  (cadeia de textos já resolvida, em ordem crescente de precedência — **API pública estável**, consumida
  pelo CLI em produção) devolvem `ArchConfigParseResult`. Erros de schema são `SchemaValidationError`
  (`Code`/`Message`/`Line`/`Column`/`IsBlocking`/`SourceDocumentIndex`). Um erro `IsBlocking = true` (ex.:
  `ARCH9001`) zera `Config` e interrompe o registro de regras; um erro não bloqueante (`ARCH9003`,
  `ARCH9008`) deixa `Config` utilizável e as demais regras valem. Nenhuma exceção escapa do parser
  (rede de segurança → `ARCH9001`). **`ArchConfigParser.PeekExtends(string)`** (pública) extrai só a
  lista de `extends:` sem validar o resto — é o que o analyzer usa para descobrir quais
  `AdditionalFiles` fazem parte da cadeia.
- **Faixa ARCH9xxx hoje:** `ARCH9001` (config inválida/schema, `Error`, invalida o arquivo inteiro),
  `ARCH9002` (slot fora do pool ou duplicado, `Warning`, regra ignorada), `ARCH9003` (tipo de regra
  desconhecido, `Warning`, regra ignorada), `ARCH9004` (nome em `implements`/`baseType`/`attribute` não
  resolvido via `GetTypeByMetadataName`, `Warning`, critério ignorado), **`ARCH9005`** (`Info`;
  **emitido de verdade** pelo analyzer — `SeverityDivergence` — quando a severidade declarada no YAML
  diverge da efetiva do slot; precedência do compilador: `SpecificDiagnosticOptions` → `.globalconfig`
  → default do descriptor; **ignora `.editorconfig` por árvore e `TreatWarningsAsErrors`**; um por
  regra, no compilation-start; o CLI `validate` tem um aviso análogo quando o lock file diverge),
  `ARCH9006` (`extends` sem `AdditionalFile`, `Warning`), `ARCH9007` (ciclo/profundidade em `extends`,
  `Warning`), **`ARCH9008`** (regra de tipo conhecido com campo ausente/desconhecido/inválido,
  `Warning`, regra ignorada; emitido pelo parser). `ARCH9006`/`ARCH9007`/`ARCH9005` são reportados só
  pelo analyzer (descrevem a resolução contra `@(AdditionalFiles)` e a compilação, não schema).
  Um código de motor desconhecido para o analyzer cai em `ARCH9001`/`ARCH9002`
  (`ArchDiagnostics.GetEngineDescriptorOrFallback`).
- **Convenção de nome `arch-rules.*.yaml` para `extends`.** Só arquivos nesse padrão (ao lado do
  `.csproj`, exceto `arch-rules.yaml` e o `arch-rules.lock.yaml` gerado pelo CLI) entram
  automaticamente em `@(AdditionalFiles)` via `build/RuleKit.Analyzer.targets`. Fora da convenção, o
  consumidor adiciona `<AdditionalFiles Include="..." />` manualmente; senão o analyzer reporta
  `ARCH9006` com o caminho que faltou. O CLI não tem essa restrição (lê o disco), então um `validate`
  verde não prova que o analyzer enxergou o arquivo.
- **`.globalconfig`/lock gerados pelo CLI.** `gen` escreve por padrão ao lado do YAML (onde o
  compilador descobre `.globalconfig` sem configuração); só regras `enabled` entram. Sem `gen`, os
  slots reportam o default do descriptor (`warning`). `gen` trata `ARCH9003`/`ARCH9008`
  como `AVISO` (gera, exit 0); `validate` trata `ARCH9008` (regra malformada) como erro (exit 1) e
  `ARCH9003` (pode ser tipo de versão futura) como `AVISO` (exit 0); `--strict` (nos dois) faz QUALQUER
  aviso sair 1 (`gen` não gera nada). Erro de slot/`extends`/severidade é bloqueante (exit 1). O
  `SlotValidator` ignora regras `enabled: false` (paridade com o analyzer). O caminho de cada `extends`
  passa por `ExtendsChainResolver.NormalizeDeclaredPath` (trim + `\` → `/`) antes do combinador, nos dois lados.
- **Limites do `YamlSubsetGuard`** (roda por eventos, antes do `YamlStream.Load`, também para o
  `PeekExtends`): texto até 1 MiB e aninhamento de mapping/sequence até 64 níveis, senão `ARCH9001`
  bloqueante com linha/coluna; exceções do scanner que não são `YamlException` também viram `ARCH9001`.
- **Riscos em aberto para v2:** os erros do motor saem em `RegisterCompilationEndAction` (#20); a
  checagem de `type` mora agora no parser (catálogo) *e* no despacho do analyzer — manter os dois
  alinhados pelo teste acima.

## 9. Itens de limpeza

Os itens opcionais do fechamento da v1 foram resolvidos no ciclo de publicação: os projetos de spike
da Fase 0 foram removidos, o dogfood usa o `build/RuleKit.Analyzer.targets` (sem `AdditionalFiles`
manual) e o contorno por reflexão de `Arch.Benchmarks` foi eliminado. Nada pendente aqui.

## 10. Release

1. `main` verde (CI: build-test, smoke, windows-vs, perf) e `CHANGELOG.md` com a seção da versão.
2. Tag `vX.Y.Z` **num commit da `main`** (`git tag vX.Y.Z && git push origin vX.Y.Z`); o `publish.yml`
   recusa tag fora da `main`. O README empacotado é o do commit da tag — **comite a documentação antes
   de criar a tag**.
3. `publish.yml` (disparado por `push` de tag `v*`), em **dois jobs**: `build` (`contents: read`; checkout,
   build + testes + pack; confere que `RuleKit.Analyzer.<ver>.nupkg`, `RuleKit.Cli.<ver>.nupkg` e
   `.snupkg` têm a versão da tag (MinVer); sobe os pacotes como artifact) e `publish` (`needs: build`;
   `id-token: write` + `contents: write`; **sem checkout nem build** — só baixa o artifact):
   `NuGet/login@v1` (**Trusted Publishing**, usuário `samueldias21`, policy do nuget.org cadastrada para
   `SamuelGFDias/rulekit` + `publish.yml`, **sem `environment`** — declarar um no job faria o login
   falhar); `dotnet nuget push artifacts/*.nupkg --skip-duplicate`; Release idempotente (`gh release
   create` ou, se a tag já tiver Release, `gh release upload --clobber`, sempre com `--repo`). Sufixo na
   tag (`v1.0.0-rc.1`) = pré-lançamento. O `RuleKit.Cli` é multi-target (`net8.0;net10.0`): o pack traz
   `tools/net8.0/any` e `tools/net10.0/any`; o SDK 10 baixa o targeting pack do net8.0 no restore.
