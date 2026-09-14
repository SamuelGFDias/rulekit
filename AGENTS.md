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

## 4. Estrutura de pastas planejada

Copiada da seção "Estrutura do repositório (contrato de layout)" do `PLANO-implementacao.md`. Esta
árvore ainda não existe integralmente no repo — está sendo criada pela Fase 0 (spike) em paralelo a
este documento. **Não editar `src/`, `tests/` nem `build/` a partir deste AGENTS.md** — apenas
descrever o contrato.

```
src/
  Arch.Analyzer/                 # netstandard2.0 — o DiagnosticAnalyzer + pool de slots
  Arch.Config/                   # netstandard2.0 — modelo, parser YAML, validação de schema
  Arch.Rules/                    # netstandard2.0 — avaliadores (um por tipo de regra)
  Arch.Cli/                      # net10.0 — gerador de .globalconfig e lock file
tests/
  Arch.Config.Tests/             # parser puro, sem Roslyn
  Arch.Analyzer.Tests/           # harness (código + config) -> diagnósticos
  Arch.Benchmarks/               # regressão de performance
build/
  ilrepack.targets               # fusão + /internalize do parser YAML
docs/
  adr/ADR-001-....md
  schema/arch-rules.v1.json      # JSON Schema publicado
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
- **Fase 0 (spike de viabilidade Roslyn) em andamento nesta mesma sessão**, em paralelo à criação
  deste AGENTS.md — outro agente trabalha em `src/`, `tests/` e `build/`. É um gate bloqueante:
  todas as fases seguintes assumem que o pool de slots funciona na prática (sem `AD0001`).

Fases seguintes do plano (cada uma só começa com o gate da anterior fechado):

- **Fase 1 — Fundações:** 3 trilhas em paralelo (parser/schema YAML em `Arch.Config`; núcleo do
  analyzer com pool de 512 descriptors e resolução de camadas em `Arch.Analyzer`; harness de testes
  e packaging/CI) partindo das interfaces congeladas na Fase 0.
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
