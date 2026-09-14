# AGENTS.md — RuleKit

Contexto de referência rápida para qualquer agente que trabalhe neste repositório. Leia isto antes
de pedir para reler o ADR ou o Plano inteiros; volte às fontes completas só quando precisar de
detalhe que não esteja aqui.

## 1. Visão geral

RuleKit é uma lib .NET distribuída como Roslyn Analyzer configurável que valida padrões
arquiteturais em tempo de compilação. A lib não embute nenhuma regra de negócio: cada organização
declara sua própria política num arquivo YAML externo (`arch-rules.yaml`) e recebe diagnósticos de
violação no `dotnet build`, no CI e ao vivo no Error List da IDE — sem fork da lib e sem regra
hardcoded por empresa.

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
funciona em live analysis na IDE sem build, e permite `#pragma warning disable ARCH0007`. O custo
aceito é que o slot é um "número mágico" no YAML — mitigado por um gerador CLI (`arch-rules gen`,
Fase 3) que aloca slots e emite lock file. Opções alternativas descartadas: descriptors construídos
em runtime (inviável — vira sempre `AD0001`) e um descriptor fixo por *tipo* de regra (mata
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

## 4. Estrutura de pastas (real, pós-Fase 1)

A árvore abaixo reflete o `RuleKit.sln` de fato após a Fase 0 (spike) e a Fase 1 (fundações).
`src/Arch.Rules/` e `src/Arch.Cli/` **ainda não existem** — são as Fases 2 e 3.

```
src/
  Arch.Analyzer/                 # REAL — netstandard2.0. DiagnosticAnalyzer + pool de 512
                                  # descriptors (ARCH0001..ARCH0512) + faixa ARCH9xxx,
                                  # ILayerResolver real (Contracts/ILayerResolver.cs,
                                  # LayerResolver.cs, NamespaceTrie.cs), cache de ArchConfig
                                  # por SourceText, RuleEvaluatorRegistry (vazio até a Fase 2)
  Arch.Config/                   # REAL — netstandard2.0, sem dependência de Roslyn. Modelo
                                  # (ArchConfig/LayerDefinition/LayerMatchCriterion/
                                  # RuleDefinition) + ArchConfigParser (YamlDotNet, em
                                  # Internal/) + validação de schema
  Arch.Rules/                    # AINDA NÃO EXISTE — Fase 2, um IRuleEvaluator por arquivo
  Arch.Cli/                      # AINDA NÃO EXISTE — Fase 3, gerador de .globalconfig/lock file
tests/
  Arch.Config.Tests/             # parser puro, sem Roslyn — 9 testes
  Arch.Analyzer.Tests/           # harness (código + config) -> diagnósticos — 52 testes
  Arch.TestHarness/              # harness reutilizável de compilação in-memory — 2 testes próprios,
                                  # consumido também por Arch.Analyzer.Tests
  Arch.Benchmarks/                # esqueleto de regressão de performance (ainda sem benchmarks reais)
  Arch.Spike.Consumer/            # artefato do spike da Fase 0 — projeto de prova de conceito que
                                  # CONSOME o analyzer via PackageReference; não faz parte da lib
                                  # final, não entra no pack
  Arch.Spike.ControlAnalyzer/    # artefato do spike da Fase 0 — analyzer de controle para comparação;
                                  # idem, não faz parte da lib final
build/
  ilrepack.targets               # fusão + /internalize do parser YAML
.github/
  workflows/ci.yml               # CI: build+test+pack+smoke (jobs build-and-test, pack-and-smoke).
                                  # Validação em host .NET Framework/Visual Studio fica FORA do
                                  # pipeline — ver issue #1 no GitHub
docs/
  adr/ADR-001-....md
  schema/arch-rules.v1.json      # JSON Schema publicado (ainda não existe — Fase 3)
```

`Arch.Config` e `Arch.Rules` são fundidos em `Arch.Analyzer.dll` no pack — a separação existe para
testabilidade e paralelização entre trilhas, não para distribuição.

## 5. Convenções técnicas

- **Target frameworks:** `netstandard2.0` para as libs do analyzer (`Arch.Analyzer`, `Arch.Config`,
  `Arch.Rules` — precisam rodar dentro do host do compilador). `net10.0` para `Arch.Cli` (ferramenta
  externa, sem essa restrição).
- **Parser YAML:** YamlDotNet, fundido no assembly do analyzer via ILRepack `/internalize`, virando
  tipos `internal`. **Nunca distribuído como DLL separada em `analyzers/dotnet/cs`** — isso é o que
  elimina a classe de falhas do roslyn#41421 (`FileLoadException` quando dois analyzers carregam
  versões diferentes do mesmo assembly).
- **Faixa `ARCH9xxx`** é reservada para erros do próprio motor (schema inválido, camada inexistente
  referenciada, tipo não resolvido, slot duplicado) — sempre habilitada por padrão, **nunca alocável
  por configuração** do consumidor. Não recebe `WellKnownDiagnosticTags.NotConfigurable` (essa tag
  bloquearia `#pragma`/`.editorconfig`, e o time precisa poder silenciar pontualmente um aviso do
  motor).
- YAML aceito é um subset restrito: sem anchors/aliases, sem multi-documento, sem tags customizadas;
  chave desconhecida é erro de schema, não silêncio.

## 6. Estado atual do projeto

- Fase de **design concluída**: ADR-001 e PLANO-implementacao.md aprovados (status "Proposto" no
  ADR, mas já serve de base de execução).
- **Fase 0 (spike de viabilidade Roslyn) concluída** — commit `fe902e6`. Provou o pool de slots na
  prática (sem `AD0001`) e congelou os contratos (`ILayerResolver`, `IRuleEvaluator`).
- **Fase 1 (fundações: config-parser, analyzer-core, infra-tests) concluída** — 3 trilhas paralelas,
  ainda não commitadas. Gate fechado: build da solução `RuleKit.sln` ok, exceto uma falha esperada/
  por design em `Arch.Spike.Consumer` (artefato de spike, não faz parte da lib), e **63 testes
  aprovados** no total (`Arch.Config.Tests` 9 + `Arch.Analyzer.Tests` 52 + `Arch.TestHarness` 2).
- **Pendência conhecida:** validação em host .NET Framework/Visual Studio, ainda fora do pipeline de
  CI — rastreada na issue https://github.com/SamuelGFDias/rulekit/issues/1.
- **Próxima fase:** Fase 2 (tipos de regra: `forbidden-call`, `must-route-through`,
  `naming-convention`, `max-dependencies`, em `src/Arch.Rules/`).

Fases seguintes do plano (cada uma só começa com o gate da anterior fechado):

- **Fase 2 — Tipos de regra:** 4 trilhas paralelas implementando `IRuleEvaluator` (
  `forbidden-call`, `must-route-through` como açúcar sobre a anterior, `naming-convention`,
  `max-dependencies`), isoladas por arquivo em `src/Arch.Rules/`.
- **Fase 3 — Ergonomia:** 2 trilhas paralelas — CLI gerador de `.globalconfig`/lock file
  (`Arch.Cli`) e documentação/JSON Schema publicado.
- **Fase 4 — Verificação adversarial:** 3 trilhas paralelas — dogfooding em repositório real,
  revisão adversarial obrigatória por agente sem investimento no código escrito, e auditoria de
  performance contra o orçamento de <5% de overhead de build.

## 7. Regra de processo

O projeto é conduzido pela skill `arquiteto`: o orquestrador **nunca escreve código diretamente**.
Ele decompõe o trabalho em subtarefas, delega via subagentes (sem sub-delegação — nenhum subagente
invoca outro), escolhe o modelo mais econômico que atenda cada tarefa (Haiku para trabalho mecânico,
Sonnet para o grosso, Opus só onde correção semântica do Roslyn ou revisão adversarial justificam) e
fixa contratos compartilhados (interfaces, trechos do ADR) no prompt de cada delegação. Cada fase do
`PLANO-implementacao.md` tem um gate de saída explícito que precisa fechar — todos os itens marcados
— antes da próxima fase começar.

## 8. Contratos e convenções estabelecidos na Fase 1

- **Separação de dependências entre modelo e contratos Roslyn.** O modelo de dados (`ArchConfig`,
  `LayerDefinition`, `LayerMatchCriterion`, `RuleDefinition`) mora em `Arch.Config` e não depende de
  Roslyn. `ILayerResolver`/`IRuleEvaluator` moram em `Arch.Analyzer.Contracts`, porque dependem de
  tipos do Roslyn (`Compilation`, `INamedTypeSymbol` etc.) — não podiam ficar em `Arch.Config`.
- **Ponto de entrada do parser.** `Arch.Config.ArchConfigParser.Parse(string) : ArchConfigParseResult`
  é o único ponto de entrada. Erros de schema são `SchemaValidationError` (`Code`/`Message`/`Line`/
  `Column`/`IsBlocking`). Um erro `IsBlocking = true` (ex.: `ARCH9001`) zera `Config` no resultado e
  interrompe o registro de regras; um erro não bloqueante (ex.: `ARCH9003`, tipo de regra
  desconhecido) deixa `Config` utilizável e não impede as demais regras de valerem.
- **Faixa ARCH9xxx hoje:** `ARCH9001` (config inválida/schema, `Error`, invalida o arquivo inteiro),
  `ARCH9002` (slot fora do pool de 512 ou duplicado entre regras, `Warning`, regra ignorada),
  `ARCH9003` (tipo de regra desconhecido, `Warning`, regra ignorada — **hoje TODAS as regras caem
  aqui**, porque `RuleEvaluatorRegistry` está intencionalmente vazio até a Fase 2 populá-lo com
  avaliadores reais), `ARCH9004` (nome de tipo em `implements`/`baseType`/`attribute` não resolvido
  via `GetTypeByMetadataName`, `Warning`, critério ignorado), `ARCH9005` (reservado para divergência
  YAML×`.globalconfig`, `Info`, usado só a partir da Fase 3).
- **Ponto de extensão para a Fase 2.** `RuleEvaluatorRegistry` (`src/Arch.Analyzer/
  RuleEvaluatorRegistry.cs`) é onde a Fase 2 registra um `IRuleEvaluator` por `type` de regra
  (`forbidden-call`, `must-route-through`, `naming-convention`, `max-dependencies`), um arquivo por
  tipo em `src/Arch.Rules/`. O roteamento, o cache de `ArchConfig` e o resolvedor de camadas já estão
  implementados e provados por `ArchRulesAnalyzerPipelineTests` (que registra um avaliador de teste e
  exercita o pipeline inteiro) — popular o registro é a única mudança necessária ali.
- **Riscos levantados nesta fase, para revisão futura (Fase 4 adversarial):**
  - Diagnósticos de erro de config são reportados via `RegisterCompilationEndAction`; o comportamento
    em live analysis de IDE (Error List atualizando durante digitação, não só no build) ainda não foi
    verificado.
  - Risco de duplicação de `ARCH9003` se a Fase 2 também passar a validar tipos de regra desconhecidos
    dentro do parser: a checagem de `type` deve continuar sendo responsabilidade exclusiva do
    analyzer (via `RuleEvaluatorRegistry`), nunca do parser em `Arch.Config`.
