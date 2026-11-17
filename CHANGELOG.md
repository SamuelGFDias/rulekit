# Changelog

Todas as mudanças relevantes deste projeto são documentadas aqui.

O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto adota o
[Versionamento Semântico](https://semver.org/lang/pt-BR/). A versão dos pacotes é derivada de tags
git `vX.Y.Z` (MinVer).

## [Unreleased]

## [0.1.0] - 2026-11-17

Primeira versão publicada. Entrega o motor de regras arquiteturais completo (fases 0–4 do
[plano](PLANO-implementacao.md)) e o ciclo de preparação para publicação no nuget.org.

### Adicionado

- **Analyzer Roslyn configurável** (`RuleKit.Analyzer`): a política vive num `arch-rules.yaml` externo e
  não há regra hardcoded. Pool de **512 slots** pré-declarados (`ARCH0001`…`ARCH0512`) que o YAML
  mapeia aos IDs de negócio da organização — dá override de severidade por regra via
  `.editorconfig`/`.globalconfig` e `#pragma warning disable` (ADR-001, D2 Opção C). Provado sem
  `AD0001` no spike da Fase 0.
- **Quatro tipos de regra:** `forbidden-call`, `must-route-through` (açúcar sobre o anterior; `through`
  só compõe a mensagem), `naming-convention` (`require: { suffix | prefix }`) e `max-dependencies`
  (`countBy`: `constructor-parameters`, `fields` ou `all`).
- **Camadas** por critérios `namespace` (globs `*`/`**`, compilados num trie de prefixos), `nameSuffix`,
  `baseType`, `implements` e `attribute`, combinados por OU, com `exclude`; memoização por símbolo.
- **`extends`** com merge por nome/`id`, resolvido **no analyzer** a partir de `AdditionalFiles` (sem I/O) e
  no CLI a partir do disco, com o mesmo resolvedor (`ExtendsChainResolver`). O pacote registra
  automaticamente `arch-rules.yaml` e `arch-rules.*.yaml` do projeto via `build/RuleKit.Analyzer.targets`
  — instalar o pacote e criar o YAML basta.
- **CLI `arch-rules`** (`RuleKit.Cli`, `dotnet tool`): `validate` (schema, slots, severidades, cadeia de
  `extends`) e `gen` (`.globalconfig` com a severidade de cada regra + `arch-rules.lock.yaml`).
- **Diagnósticos do motor `ARCH9001`–`ARCH9008`:** `ARCH9005` agora é emitido de verdade quando a
  severidade declarada no YAML diverge da efetiva do slot na compilação; `ARCH9008` (novo) descarta, de
  forma ruidosa, a regra de tipo conhecido com campo obrigatório ausente, chave que o tipo não aceita
  ou valor inválido. `ARCH9006`/`ARCH9007` cobrem `extends` ausente e ciclo/profundidade.
- **`RuleTypeCatalog`** em `Arch.Config`: fonte única de tipos de regra e campos (obrigatórios/opcionais,
  valores aceitos), usada pelo parser; `RuleDefinition.Properties` tipado; `SlotPool` e
  `SeverityVocabulary` compartilhados entre analyzer e CLI. `type` ignora caixa e espaços nas pontas.
- **JSON Schema** `docs/schema/arch-rules.v1.json` (draft 2020-12) alinhado ao catálogo — `through`
  opcional em `must-route-through`, `require` com um único par `suffix`/`prefix`, `max` inteiro ≥ 0,
  `countBy` restrito, campos por tipo — com `$id` resolvível em `raw.githubusercontent.com` e modeline
  `# yaml-language-server: $schema=…` documentado.
- **CI** (`ci.yml`): build e testes da solução, pack dos dois pacotes, smoke de consumo (dogfood contra o
  `.nupkg` local + instalação do `RuleKit.Cli` como dotnet tool), job `windows-vs` que carrega o analyzer
  no MSBuild/csc **.NET Framework** do Visual Studio e falha se aparecer `AD0001`, e job `perf` com o
  gate de < 5% de overhead de build (mediana de pares de parede, solução sintética em `eng/perf/`).
- **Publicação** (`publish.yml`): tag `vX.Y.Z` na `main` → build, testes e pack → nuget.org por
  **Trusted Publishing** (OIDC, sem API key no repositório) → GitHub Release com os pacotes.
- Documentação orientada a consumidor (`README.md`, também o `PackageReadmeFile` dos dois pacotes),
  `CONTRIBUTING.md` e este `CHANGELOG.md`.

### Alterado

- **Pacotes renomeados** para `RuleKit.Analyzer` e `RuleKit.Cli` (o `PackageId` do analyzer coincide com
  `build/RuleKit.Analyzer.targets`; assembly e namespaces continuam `Arch.*`). Versão via MinVer;
  versões de dependências centralizadas (`Directory.Packages.props`); metadados, SourceLink e licença
  MIT (`LICENSE`, `THIRD-PARTY-NOTICES.txt`).
- Tipo de regra **desconhecido** gera só `ARCH9003` e seus campos não são examinados; a checagem de
  `type` passou a ser do catálogo no parser (o registro do analyzer é só despacho, e um teste garante
  que os dois não divergem).
- Diagnósticos do motor apontam a **linha/coluna da regra** no arquivo da cadeia de `extends` que a
  declarou.
- O CLI converte falhas inesperadas em um `ERRO [ARCH9001]` limpo, sem stack trace. Contagem de
  dependências de `max-dependencies` normaliza genéricos por `OriginalDefinition`. Regras RS1035
  (`EnforceExtendedAnalyzerRules`) ligadas no analyzer.

### Removido

- Projetos de spike da Fase 0 (`Arch.Spike.Consumer`, `Arch.Spike.ControlAnalyzer`) e o contorno por
  reflexão em `Arch.Benchmarks` (o ILRepack agora só roda no `dotnet pack`).

### Limitações conhecidas

- Sem extensão de terceiros: o registro de avaliadores é interno ([#21](https://github.com/SamuelGFDias/rulekit/issues/21)).
- Live analysis na IDE (Error List durante a digitação) não verificada; erros do motor saem no fim da
  compilação ([#1](https://github.com/SamuelGFDias/rulekit/issues/1), [#20](https://github.com/SamuelGFDias/rulekit/issues/20)).
- `ARCH9005` ignora `.editorconfig` por árvore e `TreatWarningsAsErrors`.
- O `RuleKit.Cli` exige runtime .NET 10 ou superior.

[Unreleased]: https://github.com/SamuelGFDias/rulekit/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/SamuelGFDias/rulekit/releases/tag/v0.1.0
