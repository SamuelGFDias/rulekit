# Contribuindo com o RuleKit

Este guia é para quem desenvolve a lib. Para *usar* o RuleKit, veja o [README](README.md).
Convenções e contratos internos detalhados (para humanos e agentes) estão no [AGENTS.md](AGENTS.md); a
decisão de arquitetura, no [ADR-001](ADR-001-motor-regras-arquiteturais.md).

## Pré-requisitos

- **SDK .NET 10** — `global.json` fixa `10.0.100` com `rollForward: latestFeature`. O CLI é multi-target `net8.0;net10.0` (o SDK 10 baixa o targeting pack do net8.0 no restore);
  as libs do analyzer são `netstandard2.0`.
- Git com as **tags** buscadas (`git fetch --tags`): a versão dos pacotes vem de tags (ver
  [Versão e release](#versão-e-release)).
- Windows/Visual Studio **não** é necessário: o host .NET Framework é verificado no CI (job `windows-vs`).

## Estrutura do repositório

```
src/
  Arch.Analyzer/             netstandard2.0 — DiagnosticAnalyzer (ArchRulesAnalyzer), pool de 512 descriptors
                             ARCH0001..ARCH0512 + faixa ARCH9xxx (ArchDiagnostics), LayerResolver/NamespaceTrie,
                             RuleEvaluatorRegistry (despacho), SeverityDivergence (ARCH9005). É o PackageId
                             RuleKit.Analyzer; assembly e namespaces continuam Arch.Analyzer.
  Arch.Analyzer.Contracts/   netstandard2.0 — IRuleEvaluator / ILayerResolver (dependem de Roslyn). Projeto próprio
                             para evitar o ciclo Arch.Analyzer <-> Arch.Rules.
  Arch.Config/               netstandard2.0, sem Roslyn — modelo (ArchConfig, LayerDefinition, RuleDefinition...),
                             ArchConfigParser (YamlDotNet), RuleTypeCatalog (fonte de verdade de tipos/campos),
                             SlotPool, SeverityVocabulary, ExtendsChainResolver.
  Arch.Rules/                netstandard2.0 — um IRuleEvaluator por arquivo: forbidden-call, must-route-through,
                             naming-convention, max-dependencies.
  Arch.Cli/                  net8.0;net10.0, dotnet tool (PackageId RuleKit.Cli, comando arch-rules): gen e validate.
tests/
  Arch.Config.Tests/         parser/catálogo, sem Roslyn
  Arch.Analyzer.Tests/       harness (código + config) -> diagnósticos; pipeline completo do analyzer
  Arch.Rules.Tests/          os 4 avaliadores
  Arch.Cli.Tests/            gen/validate
  Arch.TestHarness/          compilação in-memory reutilizável (e seus próprios testes)
  Arch.Benchmarks/           BenchmarkDotNet (memoização do LayerResolver, cache de config); só compila no CI
  Arch.Dogfood/              consumidor real do pacote (.nupkg, não ProjectReference) — não está na RuleKit.sln
build/
  ilrepack.targets           funde as 5 DLLs (Arch.Analyzer, YamlDotNet, Arch.Config, Contracts, Rules) com
                             /internalize — SÓ durante `dotnet pack`, nunca num build comum
  RuleKit.Analyzer.targets   empacotado em build/ no .nupkg; registra arch-rules.yaml e arch-rules.*.yaml em
                             @(AdditionalFiles). O nome PRECISA ser build/<PackageId>.targets
eng/perf/                    solução sintética de referência e script de medição do orçamento de performance
docs/schema/                 arch-rules.v1.json (JSON Schema publicado via raw.githubusercontent.com)
.github/workflows/           ci.yml e publish.yml
Directory.Build.props        metadados de pacote, MinVer, SourceLink — comuns a todos os projetos
Directory.Packages.props     Central Package Management: as versões vivem só aqui
```

`Arch.Config`, `Arch.Rules` e `Arch.Analyzer.Contracts` são fundidos em `Arch.Analyzer.dll` no pack — a
separação em projetos existe para testabilidade e para evitar ciclos, não para distribuição. O
YamlDotNet é internalizado (ILRepack `/internalize`) e **nunca** vai como DLL separada em
`analyzers/dotnet/cs` (é o que elimina a classe de falhas do
[roslyn#41421](https://github.com/dotnet/roslyn/issues/41421)).

## Compilar e testar

```bash
dotnet restore RuleKit.sln
dotnet build RuleKit.sln -c Release
dotnet test  RuleKit.sln -c Release
```

Os testes ficam em 5 projetos (`Arch.Config.Tests`, `Arch.Analyzer.Tests`, `Arch.Rules.Tests`,
`Arch.Cli.Tests`, `Arch.TestHarness`); `Arch.Benchmarks` só compila. Para rodar os benchmarks:

```bash
dotnet run -c Release --project tests/Arch.Benchmarks -- --filter '*'
```

## Empacotar

```bash
dotnet pack src/Arch.Analyzer -c Release -o artifacts   # RuleKit.Analyzer.<versão>.nupkg
dotnet pack src/Arch.Cli      -c Release -o artifacts   # RuleKit.Cli.<versão>.nupkg + .snupkg
```

O merge do ILRepack roda dentro do `pack`. Os dois pacotes embutem o `README.md` da raiz como
`PackageReadmeFile` — por isso o README só usa **URLs absolutas** (links relativos quebram no
nuget.org) — e o `THIRD-PARTY-NOTICES.txt`.

## Dogfooding: consumir o pacote como um time externo

`tests/Arch.Dogfood/Arch.Dogfood.Sample` instala o `RuleKit.Analyzer` por `.nupkg`. Ele é o
teste de ponta a ponta do que o pacote realmente entrega (o `build/RuleKit.Analyzer.targets`, a DLL
fundida, a política `arch-rules.yaml` com os 4 tipos de regra).

```bash
dotnet pack src/Arch.Analyzer -c Release -o tests/_local-nuget-feed
rm -rf ~/.nuget/packages/rulekit.analyzer      # o NuGet cacheia por versão: reempacotar a mesma não invalida
dotnet build tests/Arch.Dogfood/Arch.Dogfood.Sample \
  -p:RuleKitAnalyzerVersion=<versão do .nupkg gerado>
```

Cada arquivo de `Contoso/` diz no próprio XML doc se é um caso limpo ou uma violação proposital
(e de qual slot): é essa marcação que define o esperado. O build deve produzir diagnósticos
`ARCH0001`–`ARCH0004` e **nenhum** `AD0001`.

## Orçamento de performance

O requisito é **< 5% de overhead** no tempo de build, medido (não estimado) numa solução sintética
de referência. Metodologia, números e por que o gate usa a mediana de pares de parede estão em
[eng/perf/README.md](eng/perf/README.md).

```bash
dotnet pack src/Arch.Analyzer -c Release -o artifacts
eng/perf/measure.sh --feed artifacts --version <versão> --runs 11 --threshold 5 --work /tmp/rulekit-perf
```

## CI

`.github/workflows/ci.yml` (push na `main`, PRs e `workflow_dispatch`):

| Job | O que garante |
|---|---|
| `build-test` | restore, build e `dotnet test` da solução; `pack` dos dois pacotes (artefato `nupkg`). |
| `smoke` | consome o pacote local no dogfood (exige `ARCH0xxx`, proíbe `AD0001`) e instala o `RuleKit.Cli` como dotnet tool rodando `validate`/`gen`. |
| `windows-vs` | builda uma cópia do dogfood com o MSBuild/csc **.NET Framework** do Visual Studio (`RoslynCompilerType=Framework`) e falha se o compilador não for o do VS ou se aparecer `AD0001`. Não prova a live analysis na IDE — issue [#1](https://github.com/SamuelGFDias/rulekit/issues/1). |
| `perf` | `eng/perf/measure.sh` com 11 pares e limite de 5%. |

## Versão e release

A versão vem do **MinVer**, a partir de tags git `vMAJOR.MINOR.PATCH` — não fixe `<Version>` em nenhum
`.csproj`. Sem tag, a versão é `0.0.0-alpha.0.<altura>`.

Para publicar uma versão:

1. Garanta que a `main` está verde e que o `CHANGELOG.md` tem a seção da versão.
2. Crie e envie a tag **num commit da `main`**: `git tag v0.1.0 && git push origin v0.1.0`.
3. O `.github/workflows/publish.yml` roda build + testes + pack, confere que o nome dos pacotes bate
   com a tag, publica no nuget.org por **Trusted Publishing** (OIDC, `NuGet/login@v1` com o usuário
   `samueldias21`, sem API key no repositório e **sem `environment:`** no job) e cria a GitHub Release
   com os pacotes. Tag com sufixo (`v1.0.0-rc.1`) vira pré-lançamento.

## Convenções de contribuição

- **Teste primeiro** para mudança de comportamento; a suíte inteira (`dotnet test RuleKit.sln -c Release`)
  precisa passar.
- **Novo tipo de regra:** adicione o `RuleTypeSchema` em `RuleTypeCatalog.Default`
  (`src/Arch.Config/RuleTypeCatalog.cs`), um `IRuleEvaluator` em `src/Arch.Rules/` e a entrada em
  `RuleEvaluatorRegistry.Default`; um teste garante que catálogo e registro não divergem. Atualize o
  `docs/schema/arch-rules.v1.json` e a tabela de tipos do README.
- **Novo diagnóstico do motor:** declare em `ArchDiagnostics` (`SupportedDiagnostics` é cacheado por
  sessão da IDE), documente na tabela do README e no `CHANGELOG.md`.
- O analyzer **não faz I/O** (RS1035 está ligado): tudo que ele lê vem de `AdditionalFiles`.
- Valores específicos de máquina (caminhos, hosts, tokens) não entram no repositório.

## Histórico do projeto

O v1 foi construído em fases (spike de viabilidade, fundações, tipos de regra, ergonomia,
verificação adversarial); o plano original está em [PLANO-implementacao.md](PLANO-implementacao.md)
e o que cada versão entrega, no [CHANGELOG](CHANGELOG.md).
