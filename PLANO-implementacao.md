# Plano de implementação — motor de regras arquiteturais (.NET)

**Baseado em:** ADR-001
**Modo de execução:** orquestração com subagents (skill `arquiteto`), fases paralelizadas
**Regra de ouro:** o orquestrador não escreve código. Ele fixa contratos, delega, e valida o gate de
cada fase.

---

## Estrutura do repositório (contrato de layout)

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
testabilidade e paralelização, não para distribuição.

---

## Fase 0 — Spike de viabilidade (bloqueante, sequencial)

**Por que é sequencial:** todas as fases seguintes assumem que o pool de slots funciona na prática.
Se `AD0001` aparecer aqui, o ADR muda. Nada mais começa antes deste gate.

| Papel | Modelo | Entrega |
|---|---|---|
| `spike-roslyn` | Sonnet | Analyzer mínimo: 3 slots declarados, lê um YAML trivial de `AdditionalFiles`, reporta em `ARCH0001`, empacota com ILRepack, é consumido por um projeto de teste real |

**Prompt (esqueleto):**
> Construa um `DiagnosticAnalyzer` `netstandard2.0` que declare `ARCH0001..ARCH0003` em
> `SupportedDiagnostics`, leia `arch-rules.yaml` de `context.Options.AdditionalFiles` dentro de
> `RegisterCompilationStartAction`, e reporte `ARCH0001` em qualquer invocação de um tipo cujo nome
> termine com o sufixo lido do YAML. Empacote com YamlDotNet fundido via ILRepack `/internalize`.
> Crie um projeto consumidor que referencie o `.nupkg` local e prove que: (1) o diagnóstico aparece
> no `dotnet build`; (2) `dotnet_diagnostic.ARCH0001.severity = error` no `.editorconfig` quebra o
> build; (3) `#pragma warning disable ARCH0001` suprime. Não use sub-delegação.

**Gate de saída — todos obrigatórios:**
- [ ] diagnóstico aparece no `dotnet build` **e** no Error List da IDE
- [ ] `.editorconfig` sobrescreve a severidade do slot
- [ ] zero `AD0001` no log (`/warnaserror:AD0001` no projeto de teste)
- [ ] apenas 1 DLL em `analyzers/dotnet/cs` dentro do `.nupkg`
- [ ] projeto de controle com um segundo analyzer usando YamlDotNet de outra versão continua buildando
- [ ] validado no host .NET Core (CLI) **e** no host .NET Framework (VS) — o `FileLoadException` do
      roslyn#41421 não se manifesta igual nos dois
- [ ] confirmado se `TestState.AnalyzerConfigFiles` existe no `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing`
      atual; se não, validar o plano B (`SolutionTransforms` + `AddAnalyzerConfigDocument`)
- [ ] `.globalconfig` de teste usando `suggestion`/`silent` (não `info`/`hidden`) é aceito

**Saída adicional obrigatória:** as assinaturas de `IRuleEvaluator`, `ILayerResolver` e `ArchConfig`
congeladas. É o contrato que permite paralelizar a Fase 2.

---

## Fase 1 — Fundações (3 trilhas em paralelo)

Nenhuma trilha toca o arquivo da outra. Todas partem dos contratos congelados na Fase 0.

| Trilha | Papel | Modelo | Escopo | Não pode tocar |
|---|---|---|---|---|
| 1A | `config-parser` | Sonnet | `Arch.Config`: modelo, parser YAML (subset restrito), validação de schema com linha/coluna, `extends`/merge, faixa `ARCH9xxx` | `src/Arch.Analyzer`, `src/Arch.Rules` |
| 1B | `analyzer-core` | Opus | `Arch.Analyzer`: pool de 512 descriptors, `CompilationStartAction`, cache por checksum, `ILayerResolver` (trie de namespace, `implements`/`baseType`/`attribute`, `exclude`), memoização símbolo→camada | `src/Arch.Config`, `src/Arch.Rules` |
| 1C | `infra-tests` | Sonnet | Harness sobre `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` (não o `.XUnit`, deprecado), `.csproj` de packaging, `ilrepack.targets`, CI (build + test + pack + smoke de consumo nos dois hosts), `Arch.Benchmarks` esqueleto | `src/**` (só consome) |

**Por que 1B em Opus:** é onde performance e correção semântica do Roslyn se cruzam; é o código que
roda a cada tecla e o mais caro de consertar depois.

**Gate:** 1A com cobertura de casos malformados; 1B classificando símbolos corretamente num projeto
de exemplo; 1C rodando verde no CI e o harness aceitando `(código, config) → diagnósticos`.

---

## Fase 2 — Tipos de regra (4 trilhas em paralelo)

Todas implementam `IRuleEvaluator`, cada uma no seu arquivo sob `src/Arch.Rules/`. Isolamento por
arquivo é o que torna a paralelização segura.

| Trilha | Papel | Modelo | Regra |
|---|---|---|---|
| 2A | `rule-forbidden-call` | Opus | `forbidden-call` via `RegisterOperationAction` (`Invocation`, `ObjectCreation`, `PropertyReference`, `FieldReference`) |
| 2B | `rule-route-through` | Sonnet | `must-route-through` como açúcar sobre 2A, com `through` alimentando a mensagem |
| 2C | `rule-naming` | Haiku | `naming-convention` via `RegisterSymbolAction(NamedType)` |
| 2D | `rule-max-deps` | Sonnet | `max-dependencies` com `countBy: constructor-parameters \| fields \| all` |

**2B depende de 2A** (reusa o avaliador). Ordem: 2A/2C/2D disparam juntas; 2B entra assim que 2A
fechar. 2C em Haiku porque é a mais mecânica — puro `INamedTypeSymbol.Name` + sufixo/prefixo.

Cada prompt delegado carrega obrigatoriamente: a interface `IRuleEvaluator`, o trecho do ADR com a
semântica da regra, e a exigência de testes no harness cobrindo **falso positivo** (código que
deveria passar e passa), não só o caso que falha.

**Gate:** os 4 tipos passando no harness; `Arch.Benchmarks` dentro do orçamento de 5%.

---

## Fase 3 — Ergonomia (2 trilhas em paralelo)

| Trilha | Papel | Modelo | Escopo |
|---|---|---|---|
| 3A | `cli-generator` | Sonnet | `Arch.Cli`: `arch-rules gen` → aloca slots, emite `.globalconfig` e `arch-rules.lock.yaml`; `arch-rules validate` para CI |
| 3B | `docs-schema` | Sonnet | JSON Schema `arch-rules.v1.json`, README, guia de migração de schema, exemplo completo de política multicamada |

**Gate:** um desenvolvedor que nunca viu a lib consegue, só com o README, instalar o pacote, escrever
uma política de 3 camadas e ver o erro na IDE.

---

## Fase 4 — Verificação (paralela, com um agente adversarial)

| Trilha | Papel | Modelo | Escopo |
|---|---|---|---|
| 4A | `dogfooding` | Sonnet | Rodar a lib contra um repositório real do time; medir falsos positivos e tempo de build antes/depois |
| 4B | `adversarial-review` | Opus | Revisar buscando: `AD0001` em cenários de borda, vazamento de `SymbolEqualityComparer`, cache servindo config obsoleta na IDE, YAML malformado gerando avalanche, comportamento com `partial`/genéricos/tipos aninhados |
| 4C | `perf-audit` | Sonnet | Perfilar o analyzer numa solução grande; confirmar ausência de alocação por símbolo no caminho quente |

**4B é obrigatório e não pode ser o mesmo agente que escreveu o código** — revisão adversarial só
vale se quem revisa não tem investimento no que foi escrito.

---

## Mapa de paralelização

```
Fase 0  [spike-roslyn]                                  ← gate bloqueante
           │
Fase 1  ├─ 1A config-parser ──┐
        ├─ 1B analyzer-core ──┤                         ← 3 em paralelo
        └─ 1C infra-tests ────┘
           │
Fase 2  ├─ 2A forbidden-call ──→ 2B route-through
        ├─ 2C naming ─────────┤                         ← 3 em paralelo, 2B encadeada
        └─ 2D max-deps ───────┘
           │
Fase 3  ├─ 3A cli-generator ──┐                         ← 2 em paralelo
        └─ 3B docs-schema ────┘
           │
Fase 4  ├─ 4A dogfooding ─────┐
        ├─ 4B adversarial ────┤                         ← 3 em paralelo
        └─ 4C perf-audit ─────┘
```

---

## Regras de delegação

1. **Sem sub-delegação.** Nenhum subagent invoca outro; a orquestração é centralizada.
2. **Modelo por custo real da tarefa.** Haiku para mecânico, Sonnet para o grosso, Opus só onde
   correção semântica do Roslyn ou revisão adversarial justificam.
3. **Isolamento por arquivo.** Duas trilhas paralelas nunca compartilham arquivo. Se precisarem,
   o contrato compartilhado sobe para a fase anterior.
4. **Contrato no prompt.** Todo prompt delegado carrega as interfaces relevantes e o trecho do ADR
   que define a semântica — o subagent não infere política.
5. **Gate antes de avançar.** Fase seguinte só dispara com o checklist da anterior fechado.
6. **Teste é entregável, não sobra.** Trilha sem teste de falso positivo não fecha.

---

## Riscos e respostas

| Risco | Sinal | Resposta |
|---|---|---|
| `AD0001` no pool de slots | Fase 0 falha | ADR volta à Opção B temporariamente; reavaliar Opção D |
| ILRepack quebra em alguma versão do SDK | Smoke de consumo falha no CI | Trocar YamlDotNet por SharpYaml ou parser próprio do subset |
| `AnalyzerConfigFiles` ausente no harness | Fase 0, item de gate | `SolutionTransforms` + `Solution.AddAnalyzerConfigDocument` |
| Overhead de build acima de 5% | Benchmark da Fase 2 | Cortar `PropertyReference`/`FieldReference` do `forbidden-call` para v1 |
| Cache servindo config obsoleta na IDE | Reportado em 4B | Chavear a `ConditionalWeakTable` pela instância de `SourceText`, nunca por caminho nem por checksum |
| Divergência YAML × `.globalconfig` | Reportado em dogfooding | `ARCH9005` + `arch-rules validate` obrigatório no CI |

---

## Entregável de v1

Um `.nupkg` que, instalado num projeto .NET com um `arch-rules.yaml` ao lado do `.csproj`, produz
diagnósticos de arquitetura na IDE e quebra o build em CI conforme a política da organização — sem
uma linha de regra específica de empresa dentro da lib.
