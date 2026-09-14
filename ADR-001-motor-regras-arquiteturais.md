# ADR-001: Motor de regras arquiteturais como Roslyn Analyzer configurável

**Status:** Proposto
**Data:** 2026-09-13
**Deciders:** Samuel + time de arquitetura
**Escopo:** fecha os 4 pontos em aberto da seção 9 do documento de requisitos

---

## 1. Contexto

O requisito é um pacote NuGet que valide padrões arquiteturais em tempo de compilação, sem regras
hardcoded: a lib é motor, o consumidor entrega a política num arquivo externo. Três forças
dominam o desenho:

1. **O contrato do Roslyn é rígido onde o requisito pede flexibilidade.** O requisito pede IDs de
   diagnóstico customizados por organização (`ACME-001`). O Roslyn exige que o conjunto de IDs seja
   declarado em `DiagnosticAnalyzer.SupportedDiagnostics` — uma propriedade lida **antes** de existir
   qualquer contexto de compilação, e cacheada por assembly do analyzer ao longo de toda a sessão da
   IDE. Os `AdditionalFiles` (onde o YAML vive) só aparecem via `context.Options` dentro de um
   contexto de **ação** — `CompilationStartAnalysisContext`, `OperationAnalysisContext`,
   `SymbolAnalysisContext` etc.; o `AnalysisContext` de `Initialize` não expõe `Options`.
   Reportar um diagnóstico com ID não declarado não gera o diagnóstico: gera `AD0001` e a mensagem
   some. Pior: `DiagnosticAnalysisContextHelpers` valida o ID com
   `UnicodeCharacterUtilities.IsValidIdentifier` — **um ID com hífen, como `ACME-001`, nunca poderia
   ser um ID de diagnóstico real**, nem que fosse construído em runtime.
   **Este é o ponto de projeto que define toda a arquitetura da lib.**
2. **`netstandard2.0` + dependências é território minado.** Analyzers rodam dentro do host do
   compilador, junto com todos os outros analyzers da solução. Dois analyzers carregando versões
   diferentes do mesmo assembly resultam em `FileLoadException` no build
   ([roslyn#41421](https://github.com/dotnet/roslyn/issues/41421), aberto desde 2020). O sintoma não
   é uniforme entre o host .NET Core (build de CLI, onde o issue foi reportado) e o host .NET
   Framework do VS — o que torna a falha ainda pior: passa na IDE e quebra no CI. Escolher YAML
   significa embarcar um parser, e isso precisa ser resolvido explicitamente.
3. **Multiempresa é requisito, não aspiração.** Nenhuma decisão pode assumir a convenção de uma
   organização específica — nem nos nomes dos níveis, nem nos prefixos de ID, nem no layout de
   namespaces.

Prior art considerada: **ArchUnitNET / NetArchTest** (roda como suíte de testes — perde o Error List
da IDE e o gate nativo de build) e **ArchRoslyn.Analyzers / ArchitectureAnalyzer** (Roslyn + contrato
externo em JSON, mesma tese deste ADR; confirma a viabilidade do caminho).

---

## 2. Decisão

Construir a lib como `DiagnosticAnalyzer` (`netstandard2.0`) com **quatro decisões estruturais**:

| # | Ponto em aberto (seção 9) | Decisão |
|---|---|---|
| D1 | Formato do arquivo | **YAML**, com parser embarcado via ILRepack `/internalize` |
| D2 | Sintaxe de camadas e regras | `layers` por critérios OR-combinados + `rules` tipadas, com **pool de slots** |
| D3 | Nomenclatura dos níveis | Nomes livres definidos pelo consumidor; severidade real vive no `.globalconfig` |
| D4 | Versionamento de schema | `schema: arch-rules/v1` obrigatório, política MAJOR-quebra / MINOR-aditivo |

A decisão central que amarra as outras é **D2 — pool de slots**: a lib pré-declara N descriptors
estáveis (`ARCH0001`…`ARCH0512`) e o arquivo de configuração **mapeia** o ID customizado da
organização a um slot. O ID `ACME-001` aparece na mensagem e em `Diagnostic.Properties`; o slot
`ARCH0007` é o que o compilador, o `.editorconfig` e as ferramentas enxergam.

---

## 3. Opções consideradas para D2 (IDs customizados)

### Opção A: Descriptors construídos em runtime a partir do YAML

| Dimensão | Avaliação |
|---|---|
| Complexidade | Baixa (aparente) |
| Viabilidade | **Nula** |
| Custo | Descoberto tarde, em produção |

**Prós:** é a leitura literal do requisito — cada organização tem seus IDs de verdade.
**Contras:** não funciona. `SupportedDiagnostics` é consultada sem contexto de compilação; qualquer
diagnóstico fora dela vira `AD0001`. Rejeitada.

### Opção B: Um descriptor fixo por *tipo* de regra

| Dimensão | Avaliação |
|---|---|
| Complexidade | Baixa |
| Granularidade | Ruim |
| Override por projeto | Quebrado |

A lib declara `ARCH001` (chamada proibida), `ARCH002` (intermediação), `ARCH003` (nomenclatura),
`ARCH004` (acoplamento). O ID da organização entra só na mensagem.

**Prós:** trivial de implementar; lista de regras enxuta na IDE.
**Contras:** **mata o requisito §4.** Com um ID por tipo, `dotnet_diagnostic.ARCH001.severity = none`
desliga *todas* as regras de chamada proibida de uma vez. Não há como um projeto relaxar uma regra
específica. Também impede severidades diferentes entre regras do mesmo tipo. Rejeitada.

### Opção C: Pool de slots pré-declarados — **escolhida**

| Dimensão | Avaliação |
|---|---|
| Complexidade | Média |
| Granularidade | Por regra (requisito §4 atendido) |
| Override por projeto | Nativo, via `.editorconfig` |
| Ergonomia | Ponto fraco: exige mapear slot |

A lib declara `ARCH0001`…`ARCH0512` (descriptors são objetos baratos; 512 cobre qualquer política
realista). O YAML declara o vínculo:

```yaml
- id: ACME-001      # identidade da organização, aparece na mensagem
  slot: ARCH0007    # identidade técnica, é o que o compilador vê
```

**Prós:** override por regra sai de graça (`dotnet_diagnostic.ARCH0007.severity = warning`);
funciona em live analysis na IDE sem build; sem MSBuild task; supressão pontual com
`#pragma warning disable ARCH0007` funciona.
**Contras:** o slot é um número mágico no arquivo de config; alguém precisa gerenciar a alocação
(resolvido pelo gerador de lock file, abaixo).

### Opção D: MSBuild task gerando `.globalconfig` e alocando slots automaticamente

| Dimensão | Avaliação |
|---|---|
| Complexidade | Alta |
| Ergonomia | Ótima |
| Risco de IDE | Real (design-time build) |

Uma task lê o YAML durante o build, valida o schema, aloca slots deterministicamente e emite um
`.globalconfig` em `obj/`.

**Prós:** remove o slot da vida do usuário; a severidade declarada no YAML vira severidade real sem
passo manual.
**Contras:** adiciona uma task MSBuild (mais um lugar para o parser YAML conflitar), e a análise ao
vivo na IDE passa a depender de design-time build ter rodado — o feedback fica atrás do editor.
**Não rejeitada: adiada para v2**, como evolução por cima da Opção C, não como substituta.

---

## 4. Decisões detalhadas

### D1 — Formato: YAML com parser internalizado

YAML foi escolhido (decisão do time) por legibilidade: quem escreve política arquitetural escreve à
mão, e comentários importam.

O parser (YamlDotNet, MIT, suporta `netstandard2.0`) **não** é distribuído como DLL separada em
`analyzers/dotnet/cs`. Ele é fundido no assembly do analyzer com **ILRepack `/internalize`**, virando
tipos `internal`. Isso elimina a classe inteira de falhas do roslyn#41421: o pacote entrega
exatamente um DLL e não existe superfície de assembly para conflitar com outro analyzer.

Restrições deliberadas sobre o YAML aceito, para manter o parsing previsível e a mensagem de erro boa:

- sem anchors (`&`) e aliases (`*`);
- sem multi-documento (`---` repetido);
- sem tags customizadas;
- chaves desconhecidas são erro de schema, não silêncio.

**Custo aceito:** o pipeline de build da lib ganha um passo de ILRepack e a obrigação de atribuição
de licença do YamlDotNet no pacote.

### D2 — Sintaxe

```yaml
schema: arch-rules/v1

extends:
  - ./arch-rules.base.yaml        # composição: base da organização + override do projeto

layers:
  Controller:
    match:                        # critérios combinados por OR
      - namespace: "Acme.Api.Controllers.**"
      - nameSuffix: "Controller"
      - baseType: "Microsoft.AspNetCore.Mvc.ControllerBase"
  Service:
    match:
      - implements: "Acme.Application.IService"
      - nameSuffix: "Service"
  Repository:
    match:
      - nameSuffix: "Repository"
      - attribute: "Acme.Architecture.RepositoryAttribute"
    exclude:
      - namespace: "Acme.*.Tests.**"

severities:                       # nomes livres -> severidade do AnalyzerConfig
  restrita: error
  media: warning
  livre: suggestion

defaults:
  severity: media

rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
    severity: restrita
    message: "{from} não pode chamar {to} diretamente ({symbol}). Passe por um Service."
    help: https://wiki.acme/arq/ACME-001

  - id: ACME-002
    slot: ARCH0002
    type: must-route-through
    from: Controller
    to: Repository
    through: [Service]

  - id: ACME-003
    slot: ARCH0003
    type: naming-convention
    layer: Repository
    require: { suffix: Repository }

  - id: ACME-004
    slot: ARCH0004
    type: max-dependencies
    layer: Service
    max: 7
    countBy: constructor-parameters
```

**Semântica de composição (`extends`):** merge por chave, o arquivo que faz `extends` vence.
`layers` e `rules` fazem merge por nome/`id`; uma regra pode ser desligada no projeto com
`enabled: false` em vez de ser apagada, preservando rastreabilidade.

**Nota sobre `must-route-through`:** semanticamente, "A só alcança C passando por B" é, em análise
estática, o mesmo que "A não chama C diretamente". A rota real (A→B→C) não é verificável de forma
útil sem construir o grafo de chamadas completo da compilação — custo de build alto e resultado
frágil com DI, reflexão e delegates. Portanto `must-route-through` é **açúcar sintático sobre
`forbidden-call`**, com a diferença de que `through` alimenta a mensagem de erro e, no futuro, o
Code Fix. Isso é uma limitação a documentar, não a esconder.

**Limites conhecidos de qualquer analyzer:** reflexão, `dynamic`, código gerado excluído da análise e
service locators escapam da verificação. A lib previne deriva; não prova conformidade.

### D3 — Níveis de análise

- Os nomes (`restrita`, `media`, `livre`) são **chaves livres** no bloco `severities`, mapeadas aos
  valores que o AnalyzerConfig aceita. Atenção ao descasamento entre as duas escalas do .NET: o enum
  `DiagnosticSeverity` é `Hidden/Info/Warning/Error`, mas `dotnet_diagnostic.<ID>.severity` aceita
  **`error`, `warning`, `suggestion`, `silent`, `none`, `default`** — `info` e `hidden` não são
  valores válidos ali. O mapeamento canônico da lib é:

  | YAML (`severities:`) | AnalyzerConfig | `DiagnosticSeverity` |
  |---|---|---|
  | qualquer nome → `error` | `error` | `Error` |
  | qualquer nome → `warning` | `warning` | `Warning` |
  | qualquer nome → `suggestion` | `suggestion` | `Info` |
  | qualquer nome → `silent` | `silent` | `Hidden` |
  | qualquer nome → `none` | `none` | (suprimido) |

  O gerador `arch-rules gen` emite `suggestion`/`silent`, nunca `info`/`hidden`. Nada é hardcoded do
  lado dos nomes; a escala de destino é a do .NET.
- A **severidade efetiva** não vem do YAML em runtime: vem de `.editorconfig`/`.globalconfig` sobre o
  slot. Um gerador CLI (`arch-rules gen`) lê o YAML e emite
  `dotnet_diagnostic.ARCH0007.severity = error` num `.globalconfig` commitado.
- Isso é uma escolha, não um contorno: dá override por projeto de graça (`.editorconfig` tem
  precedência sobre `.globalconfig`), respeita a hierarquia de pastas e funciona com todo o
  ferramental .NET existente sem a lib reimplementar nada.

**Consequência a aceitar:** o YAML e o `.globalconfig` podem divergir se alguém editar um sem
regenerar o outro. Mitigação: a lib emite `ARCH9005` (info) quando a severidade efetiva de um slot
diverge da declarada no YAML.

### D4 — Versionamento de schema

- Primeira chave obrigatória: `schema: arch-rules/v1`.
- **MAJOR** (`v1` → `v2`): quebra. Analyzer que não suporta a major recusa o arquivo inteiro e emite
  `ARCH9001` (error) — falha ruidosa, nunca silenciosa.
- **MINOR** é implícito e aditivo: novos campos e novos `type` de regra. Um `type` desconhecido gera
  `ARCH9003` (warning) — "regra ignorada, requer a lib >= x.y" — e as demais regras continuam
  valendo.
- Faixa **`ARCH9xxx` é reservada** para erros do próprio motor (schema inválido, camada
  referenciada e inexistente, tipo do `implements` não resolvido, slot duplicado). Sempre habilitada
  por padrão, nunca alocável por configuração — mas **sem** `WellKnownDiagnosticTags.NotConfigurable`,
  porque essa tag desliga `#pragma warning disable` e `.editorconfig` para o ID, e o time precisa
  conseguir silenciar um aviso de motor pontualmente.
- Validação de schema roda **antes** de qualquer regra (requisito §6): um YAML malformado produz um
  diagnóstico claro apontando linha/coluna, não uma avalanche de falsos positivos.

---

## 5. Não-funcionais

**Performance.** O analyzer roda a cada tecla na IDE; é o requisito não-funcional que mais influencia
o código.

- `RegisterCompilationStartAction` parseia a config **uma vez por compilação**; cache em
  `ConditionalWeakTable<SourceText, ArchConfig>` chaveado pela **instância** de `SourceText` — que é
  exatamente o objeto que a IDE reaproveita entre compilações recriadas com a config inalterada.
  Não chavear por `SourceText.GetChecksum()`: ele retorna `ImmutableArray<byte>` (struct, inválido
  como chave de `ConditionalWeakTable`, que exige `class` e compara por referência) e a própria
  documentação desaconselha usá-lo para igualdade — conteúdos idênticos podem ter checksums
  diferentes por BOM/encoding. Se um cache por valor for necessário, use
  `ConcurrentDictionary<string, ArchConfig>` com o checksum em hex, ciente dessa limitação.
- Nomes de tipo (`implements`, `baseType`, `attribute`) resolvidos uma única vez no
  compilation-start via `Compilation.GetTypeByMetadataName`. Não resolvido → `ARCH9004` uma vez, não
  por símbolo.
- Globs de namespace compilados para um **trie de prefixos**, não regex avaliada por símbolo.
- Classificação símbolo→camada memoizada em `ConcurrentDictionary<INamedTypeSymbol, LayerSet>` com
  `SymbolEqualityComparer.Default`.
- `RegisterOperationAction` (`Invocation`, `ObjectCreation`, `PropertyReference`, `FieldReference`)
  para chamadas; `RegisterSymbolAction(SymbolKind.NamedType)` para nomenclatura e acoplamento.
  Nenhuma varredura sintática de árvore completa.
- `EnableConcurrentExecution()` + `ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)`.
- **Saída rápida:** sem arquivo de config nos `AdditionalFiles`, nenhuma ação é registrada — custo
  próximo de zero para quem instalou e ainda não configurou.
- Orçamento: **< 5% de overhead** no tempo de build de uma solução de referência. Medido em CI, não
  estimado.

**Testabilidade.** `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` (o pacote `...Testing.XUnit`
está deprecado no nuget.org desde 2024 e já não é necessário), com o YAML injetado via
`TestState.AdditionalFiles`. A injeção do `.globalconfig` deve ser **validada no spike**:
`TestState.AnalyzerConfigFiles` não está confirmado na árvore atual do roslyn-sdk; o plano B
garantido é `SolutionTransforms` + `Solution.AddAnalyzerConfigDocument`. Três camadas:

1. testes puros do parser/validador de schema (sem Roslyn, rápidos);
2. testes do analyzer: `(código, config) → diagnósticos esperados`;
3. teste de regressão de performance sobre um projeto sintético grande.

**Empacotamento.**

- `IncludeBuildOutput=false`, `DevelopmentDependency=true`, `SuppressDependenciesWhenPacking=true`;
  o DLL vai para `analyzers/dotnet/cs` via item `None` com `PackagePath`.
- `build/<PackageId>.targets` no pacote adiciona `arch-rules.yaml` aos `AdditionalFiles` **por
  convenção**, para que instalar o pacote e criar o arquivo baste — sem edição manual de `.csproj`.
  Usar `.targets` e não `.props`: o `.props` é importado antes do corpo do projeto e um glob
  relativo pode ser avaliado cedo demais. Se ficar no `.props`, use caminho explícito com
  `Condition="Exists(...)"`.
- Referência a `Microsoft.CodeAnalysis.CSharp` **fixada na menor versão suportada** com
  `PrivateAssets=all`, para não exigir SDK novo dos consumidores.

---

## 6. Trade-offs centrais

| Tensão | Como foi resolvida |
|---|---|
| IDs da organização × contrato do Roslyn | Dois níveis de identidade: slot técnico (compilador) + ID de negócio (mensagem e `Properties`) |
| Legibilidade do YAML × risco de dependência | YAML mantido, risco eliminado por internalização em vez de troca de formato |
| Severidade no YAML × override por projeto | Severidade real mora no `.editorconfig`; YAML é a fonte que o gerador materializa |
| Regra transitiva "real" × custo de build | `must-route-through` é açúcar sobre proibição direta; grafo completo fica fora de escopo |
| Ergonomia (slot automático) × feedback ao vivo na IDE | v1 prioriza feedback ao vivo; MSBuild task fica para v2 |

---

## 7. Consequências

**Fica mais fácil:** adotar em nova organização (só um YAML); relaxar uma regra num projeto
específico; suprimir um ponto pontual com `#pragma`; testar uma regra isoladamente.

**Fica mais difícil:** alocar slots à mão até o gerador existir; depurar divergência entre YAML e
`.globalconfig`; evoluir o schema sem quebrar consumidores (por isso a política MAJOR/MINOR é parte
do v1, não um adendo).

**A revisitar:** (a) o teto de 512 slots, se alguma organização estourar; (b) a MSBuild task da
Opção D, quando o atrito do slot manual doer mais que o risco de design-time build; (c) Code Fix
automático, que depende de `must-route-through` conhecer a camada intermediária — já previsto no
schema por isso.

---

## 8. Fora de escopo (confirmado)

Regras de negócio de qualquer empresa; UI de criação de regras; geração de código; grafo de chamadas
transitivo completo; análise cross-assembly além do que os `MetadataReference` da compilação já
expõem.

---

## 9. Action items

1. [ ] Spike de viabilidade end-to-end: pool de slots + `AdditionalFiles` + ILRepack num analyzer mínimo
2. [ ] Fixar as interfaces internas (`IRuleEvaluator`, `ILayerResolver`, `ArchConfig`) antes de paralelizar
3. [ ] Publicar o JSON Schema do `arch-rules/v1` junto com o pacote (autocompletar em IDE)
4. [ ] Definir a solução de referência para o benchmark de performance
5. [ ] Decidir se o gerador CLI é `dotnet tool` global ou local ao repositório

---

## Fontes

- [Using Additional Files — dotnet/roslyn](https://github.com/dotnet/roslyn/blob/main/docs/analyzers/Using%20Additional%20Files.md)
- [Analyzers referencing different versions of the same assembly → FileLoadException (roslyn#41421)](https://github.com/dotnet/roslyn/issues/41421)
- [Analyzer packages are hard to create and do right (roslyn#24885)](https://github.com/dotnet/roslyn/issues/24885)
- ["Reported diagnostic with ID is not supported by the analyzer" (roslyn-analyzers#1689)](https://github.com/dotnet/roslyn-analyzers/issues/1689)
- [Analyzer Configuration — dotnet/roslyn-analyzers](https://github.com/dotnet/roslyn-analyzers/blob/main/docs/Analyzer%20Configuration.md)
- [Customize Roslyn analyzer rules — Microsoft Learn](https://learn.microsoft.com/en-us/visualstudio/code-quality/use-roslyn-analyzers?view=visualstudio)
- [Enforcing C# Architecture Rules at Compile Time with a Roslyn Analyzer](https://dev.to/loach2009/enforcing-c-architecture-rules-at-compile-time-with-a-roslyn-analyzer-4fbj)
- [ArchUnitNET](https://github.com/TNG/ArchUnitNET)
- [Supporting multiple SDK versions in analyzers and source generators — Andrew Lock](https://andrewlock.net/supporting-multiple-sdk-versions-in-analyzers-and-source-generators/)
- [YamlDotNet](https://github.com/aaubry/YamlDotNet) · [SharpYaml (alternativa)](https://github.com/xoofx/SharpYaml)
