# RuleKit

Motor de regras arquiteturais para .NET, distribuído como Roslyn Analyzer configurável. A lib não
embute nenhuma regra de negócio: cada organização declara sua própria política num arquivo YAML
externo (`arch-rules.yaml`) e recebe diagnósticos de violação em tempo de compilação — no `dotnet
build`, no CI, e ao vivo no Error List da IDE.

> **Status:** Fases 0-2 do plano de execução concluídas (spike de viabilidade Roslyn, fundações e os
> 4 tipos de regra do ADR-001), com **94 testes** aprovados e 0 falhas. Fase 3 (ergonomia — CLI
> gerador de `.globalconfig` e esta documentação) em andamento. Ainda não há release publicado num
> feed NuGet público — ver [Instalação](#instalação). Pendência conhecida: validação em host .NET
> Framework/Visual Studio ainda fora do pipeline de CI, rastreada na
> [issue #1](https://github.com/SamuelGFDias/rulekit/issues/1).

## O problema

Ferramentas de arquitetura em .NET geralmente caem em dois extremos: rodam como suíte de testes
(ArchUnitNET, NetArchTest) e perdem o feedback nativo da IDE, ou exigem regras hardcoded por
organização. O objetivo aqui é um pacote NuGet único que qualquer organização instala e configura
via YAML, sem fork nem regra fixa na lib.

## Como funciona (visão geral)

A decisão central da arquitetura é o **pool de slots**: o contrato do Roslyn exige que
`DiagnosticAnalyzer.SupportedDiagnostics` seja conhecido sem contexto de compilação, então a lib
pré-declara descriptors estáveis (`ARCH0001`…`ARCH0512`) e o YAML mapeia o ID de negócio da
organização (`ACME-001`) a um desses slots técnicos (`ARCH0007`) — é o slot, não o ID de negócio,
que o compilador, o `.editorconfig`/`.globalconfig` e `#pragma warning disable` de fato enxergam.
Isso dá override de severidade por regra individual, funciona em live analysis na IDE sem build, e
permite silenciar pontualmente uma regra com `#pragma`.

Detalhes completos das quatro decisões estruturais (formato do arquivo, sintaxe de camadas e
regras, nomenclatura dos níveis de severidade, versionamento de schema) estão no
[ADR-001](ADR-001-motor-regras-arquiteturais.md).

## Instalação

O pacote ainda não está publicado num feed NuGet público. Enquanto isso, o consumo é local/interno:

1. Gere o `.nupkg` a partir do fonte (a Fase 0 já valida esse caminho: `Arch.Config` e `Arch.Rules`
   são fundidos em `Arch.Analyzer.dll` no pack):

   ```bash
   dotnet pack src/Arch.Analyzer -c Release
   ```

2. Aponte seu projeto para o `.nupkg` gerado, por exemplo via uma fonte de pacote local no
   `NuGet.Config`, ou referenciando o caminho do pacote diretamente:

   ```bash
   dotnet nuget add source /caminho/para/o/nupkg --name rulekit-local
   dotnet add package Arch.Analyzer --source rulekit-local
   ```

   Quando um release público existir, esta seção será atualizada com o comando `dotnet add package`
   real contra o feed publicado — não use um comando de exemplo daqui como se já existisse hoje.

## Tutorial: política de 3 camadas do zero

Este tutorial cobre o gate de saída da Fase 3: sair do zero e chegar a um diagnóstico visível na
IDE, usando só o que está documentado aqui.

### 1. Crie o `arch-rules.yaml`

Coloque o arquivo ao lado do `.csproj` do projeto que você quer verificar, com três camadas
(`Controller`, `Service`, `Repository`) e duas regras — uma `forbidden-call` e uma
`naming-convention`:

```yaml
schema: arch-rules/v1

layers:
  Controller:
    match:
      - nameSuffix: "Controller"
      - baseType: "Microsoft.AspNetCore.Mvc.ControllerBase"
  Service:
    match:
      - nameSuffix: "Service"
      - implements: "Acme.Application.IService"
  Repository:
    match:
      - nameSuffix: "Repository"
    exclude:
      - namespace: "Acme.*.Tests.**"

severities:
  restrita: error
  media: warning

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
    type: naming-convention
    layer: Repository
    require: { suffix: Repository }
```

Isso declara: nenhum `Controller` pode chamar um `Repository` diretamente (erro de build), e todo
tipo da camada `Repository` precisa terminar com o sufixo `Repository` (warning, por `defaults`).

Valide a sintaxe do arquivo contra o JSON Schema publicado
(`docs/schema/arch-rules.v1.json`) — qualquer editor com suporte a YAML Language Server (VS Code
com a extensão YAML, por exemplo) pode usar o schema para autocomplete e validação em tempo real
apontando:

```yaml
# yaml-language-server: $schema=./docs/schema/arch-rules.v1.json
schema: arch-rules/v1
# ...
```

### 2. Gere o `.globalconfig`

> A interface abaixo é a planejada para `Arch.Cli` (Fase 3, trilha paralela em construção). Se o
> comando ainda não estiver disponível no seu checkout, trate esta seção como a especificação da
> interface, não como confirmação de que o binário já existe.

```bash
arch-rules validate arch-rules.yaml     # valida o schema antes de gerar nada; falha o CI se inválido
arch-rules gen arch-rules.yaml          # aloca slots, emite .globalconfig e arch-rules.lock.yaml
```

`arch-rules gen` lê o YAML, aloca (ou reaproveita, via o lock file) um slot `ARCH0xxx` para cada
`id`, e escreve um `.globalconfig` como:

```ini
is_global = true

[*.cs]
dotnet_diagnostic.ARCH0001.severity = error
dotnet_diagnostic.ARCH0002.severity = warning
```

Referencie o `.globalconfig` gerado no `.csproj` (ou deixe-o na raiz da solução, onde o SDK do .NET
o descobre automaticamente) e referencie o pacote do analyzer no mesmo projeto.

### 3. Veja o diagnóstico

Ao escrever um `Controller` que chama um `Repository` diretamente:

```csharp
public class PedidoController : ControllerBase
{
    private readonly PedidoRepository _repo; // ARCH0001: Controller não pode chamar Repository diretamente
}
```

o Error List da IDE mostra o diagnóstico assim que o arquivo é digitado (sem esperar build), com a
mensagem expandida (`{from}`, `{to}`, `{symbol}` substituídos) e o ID de negócio (`ACME-001`)
disponível em `Diagnostic.Properties["archRuleId"]`. Rodar `dotnet build` reproduz o mesmo erro e
quebra o build em CI, porque a severidade `error` foi gravada no `.globalconfig`.

## Referência da sintaxe do YAML

Schema machine-readable completo: [`docs/schema/arch-rules.v1.json`](docs/schema/arch-rules.v1.json)
(JSON Schema draft 2020-12). Resumo:

| Chave | Obrigatória | Descrição |
|---|---|---|
| `schema` | sim | Sempre `arch-rules/v1`. Ver versionamento abaixo. |
| `extends` | não | Lista de caminhos para outros `arch-rules.yaml` a compor por merge (o arquivo que estende vence; `layers`/`rules` fazem merge por nome/`id`). |
| `layers` | sim | Mapa nome-livre → `{ match: [...], exclude?: [...] }`. Cada item de `match`/`exclude` tem **exatamente uma** chave: `namespace`, `nameSuffix`, `baseType`, `implements` ou `attribute`. Itens da mesma lista são combinados por OR; `exclude` é aplicado depois do `match`. |
| `severities` | não | Mapa nome-livre → nível real (ver tabela abaixo). |
| `defaults.severity` | não | Nome de severidade aplicado a regras que não declaram `severity` própria. |
| `rules` | sim | Lista de regras (ver campos por tipo abaixo). |

Campos comuns de cada item de `rules`: `id` (identidade de negócio, livre), `slot` (identidade
técnica, `ARCH0001`..`ARCH0512`), `type`, `enabled` (default `true`), `severity` (nome de
`severities`), `message` e `help` (opcionais).

Campos específicos por `type` (`Extra`, confirmados contra os avaliadores reais em
`src/Arch.Rules/`):

| `type` | Campos extras obrigatórios | Semântica |
|---|---|---|
| `forbidden-call` | `from`, `to` | Nenhum tipo da camada `from` pode invocar método, criar instância, ler propriedade ou campo de um tipo da camada `to`. |
| `must-route-through` | `from`, `to`, `through` | Mesma detecção de `forbidden-call` (é açúcar sintático sobre ele — a rota real `from → through → to` não é verificada, só citada na mensagem via `{through}`). |
| `naming-convention` | `layer`, `require` | Tipos da camada `layer` devem obedecer `require: { suffix: "..." }` (documentado no ADR) ou `require: { prefix: "..." }` (suporte adicional do avaliador). |
| `max-dependencies` | `layer`, `max`, `countBy` | Tipos da camada `layer` não podem ter mais que `max` dependências distintas, contadas por `countBy`: `constructor-parameters`, `fields` ou `all`. Limite inclusivo — contagem igual a `max` não reporta. |

O schema JSON **não** trava `type` a estes 4 valores: tipos futuros são aceitos como extensão MINOR
aditiva (ver versionamento abaixo) e só geram aviso em runtime se a versão instalada da lib não os
reconhecer.

### Severidades (ADR-001 D3)

Os nomes em `severities:` são livres; os valores são restritos à escala que
`dotnet_diagnostic.<ID>.severity` aceita no AnalyzerConfig — **nunca** `info`/`hidden`:

| YAML (`severities:`) | AnalyzerConfig | `DiagnosticSeverity` |
|---|---|---|
| qualquer nome → `error` | `error` | `Error` |
| qualquer nome → `warning` | `warning` | `Warning` |
| qualquer nome → `suggestion` | `suggestion` | `Info` |
| qualquer nome → `silent` | `silent` | `Hidden` |
| qualquer nome → `none` | `none` | (suprimido) |

A severidade efetiva vive no `.editorconfig`/`.globalconfig` gerado por `arch-rules gen`, não no
YAML em runtime — isso dá override por projeto de graça (`.editorconfig` tem precedência sobre
`.globalconfig`) e funciona com o ferramental .NET existente. Detalhes: [ADR-001, seção D3](ADR-001-motor-regras-arquiteturais.md#d3--níveis-de-análise).

### Versionamento de schema (ADR-001 D4)

- `schema: arch-rules/v1` é obrigatória.
- **MAJOR** (`v1` → `v2`): quebra o arquivo inteiro. Analyzer que não suporta a major recusa o
  arquivo e emite `ARCH9001` (error).
- **MINOR** é implícito e aditivo: novos campos e novos `type` de regra não invalidam o arquivo. Um
  `type` desconhecido para a versão instalada gera `ARCH9003` (warning) — regra ignorada, demais
  regras continuam valendo.
- A faixa `ARCH9xxx` é reservada a erros do próprio motor (schema inválido, camada inexistente,
  tipo não resolvido, slot duplicado) e sempre habilitada por padrão.

Detalhes completos e trade-offs: [ADR-001, seção D4](ADR-001-motor-regras-arquiteturais.md#d4--versionamento-de-schema).

## Estrutura do repositório

```
src/
  Arch.Analyzer/                 # netstandard2.0 — DiagnosticAnalyzer + pool de 512 descriptors
                                  # (ARCH0001..ARCH0512) e faixa ARCH9xxx, ILayerResolver real,
                                  # cache de ArchConfig por SourceText, RuleEvaluatorRegistry
                                  # populado com os 4 avaliadores de Arch.Rules.
  Arch.Analyzer.Contracts/       # netstandard2.0 — IRuleEvaluator/ILayerResolver, extraídos para
                                  # projeto próprio para quebrar o ciclo Arch.Analyzer <-> Arch.Rules.
                                  # Referencia só Arch.Config + Microsoft.CodeAnalysis.CSharp.
  Arch.Config/                   # netstandard2.0, sem dependência de Roslyn — modelo (ArchConfig,
                                  # LayerDefinition, LayerMatchCriterion, RuleDefinition),
                                  # ArchConfigParser (YamlDotNet) e validação de schema.
  Arch.Rules/                    # netstandard2.0 — um IRuleEvaluator por arquivo: os 4 tipos de
                                  # regra do ADR-001 (forbidden-call, must-route-through,
                                  # naming-convention, max-dependencies). Não depende de
                                  # Arch.Analyzer.
  Arch.Cli/                      # net10.0 — Fase 3: gerador de .globalconfig/lock file
                                  # (arch-rules gen / arch-rules validate).
tests/
  Arch.Config.Tests/             # parser puro, sem Roslyn
  Arch.Analyzer.Tests/           # harness (código + config) -> diagnósticos
  Arch.Rules.Tests/              # testes dos 4 avaliadores
  Arch.TestHarness/              # harness reutilizável de compilação in-memory
  Arch.Benchmarks/                # esqueleto de regressão de performance
  Arch.Spike.Consumer/            # artefato do spike da Fase 0, não faz parte da lib final
  Arch.Spike.ControlAnalyzer/    # artefato do spike da Fase 0, idem
build/
  ilrepack.targets               # fusão + /internalize do parser YAML no assembly do analyzer
docs/
  schema/arch-rules.v1.json      # JSON Schema desta documentação
.github/
  workflows/ci.yml               # build+test+pack+smoke
```

`Arch.Config` e `Arch.Rules` são fundidos em `Arch.Analyzer.dll` no pack — a separação em projetos
existe para testabilidade e paralelização entre trilhas de desenvolvimento, não para distribuição.

## Documentação

- [ADR-001](ADR-001-motor-regras-arquiteturais.md) — decisão de arquitetura, opções consideradas e
  trade-offs.
- [PLANO-implementacao.md](PLANO-implementacao.md) — plano de execução por fases, com gates de saída
  e critérios de paralelização.
- [`docs/schema/arch-rules.v1.json`](docs/schema/arch-rules.v1.json) — JSON Schema completo do
  `arch-rules.yaml`.
