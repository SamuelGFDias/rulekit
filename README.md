# RuleKit

Motor de regras arquiteturais para .NET, distribuído como Roslyn Analyzer configurável. A lib não
embute nenhuma regra de negócio: cada organização declara sua própria política num arquivo YAML
externo (`arch-rules.yaml`) e recebe diagnósticos de violação em tempo de compilação — no `dotnet
build`, no CI, e ao vivo no Error List da IDE.

> **Status:** projeto em fase de design. A arquitetura está fechada em [ADR-001](ADR-001-motor-regras-arquiteturais.md)
> e o plano de execução em [PLANO-implementacao.md](PLANO-implementacao.md); a implementação ainda
> não começou (Fase 0 — spike de viabilidade — é o próximo passo).

## O problema

Ferramentas de arquitetura em .NET geralmente caem em dois extremos: rodam como suíte de testes
(ArchUnitNET, NetArchTest) e perdem o feedback nativo da IDE, ou exigem regras hardcoded por
organização. O objetivo aqui é um pacote NuGet único que qualquer organização instala e configura
via YAML, sem fork nem regra fixa na lib.

## Como funciona

```yaml
schema: arch-rules/v1

layers:
  Controller:
    match:
      - nameSuffix: "Controller"
  Repository:
    match:
      - nameSuffix: "Repository"

severities:
  restrita: error

rules:
  - id: ACME-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
    severity: restrita
    message: "{from} não pode chamar {to} diretamente. Passe por um Service."
```

A decisão central da arquitetura é o **pool de slots**: a lib pré-declara descriptors estáveis
(`ARCH0001`…`ARCH0512`, exigidos pelo contrato rígido do Roslyn) e o YAML mapeia o ID de negócio da
organização (`ACME-001`) a um slot técnico (`ARCH0007`). A severidade efetiva vive no
`.editorconfig`/`.globalconfig`, gerado a partir do YAML — o que dá override por projeto de graça e
funciona com o ferramental .NET existente. Detalhes completos das quatro decisões estruturais (formato
do arquivo, sintaxe de camadas e regras, nomenclatura dos níveis, versionamento de schema) estão no
ADR.

## Estrutura planejada do repositório

```
src/
  Arch.Analyzer/     # netstandard2.0 — o DiagnosticAnalyzer + pool de slots
  Arch.Config/        # netstandard2.0 — modelo, parser YAML, validação de schema
  Arch.Rules/          # netstandard2.0 — avaliadores (um por tipo de regra)
  Arch.Cli/             # net10.0 — gerador de .globalconfig e lock file
tests/
  Arch.Config.Tests/    # parser puro, sem Roslyn
  Arch.Analyzer.Tests/  # harness (código + config) -> diagnósticos
  Arch.Benchmarks/       # regressão de performance
build/
  ilrepack.targets       # fusão + /internalize do parser YAML
docs/
  adr/ADR-001-....md
  schema/arch-rules.v1.json
```

## Documentação

- [ADR-001](ADR-001-motor-regras-arquiteturais.md) — decisão de arquitetura, opções consideradas e
  trade-offs.
- [PLANO-implementacao.md](PLANO-implementacao.md) — plano de execução por fases, com gates de saída
  e critérios de paralelização.
