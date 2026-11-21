# Changelog

Todas as mudanças relevantes deste projeto são documentadas aqui.

O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto adota o
[Versionamento Semântico](https://semver.org/lang/pt-BR/). A versão dos pacotes é derivada de tags
git `vX.Y.Z` (MinVer).

## [Unreleased]

### Adicionado

- **Critério de camada `assembly`** em `match`/`exclude`: compara o padrão com o nome do assembly que
  contém o tipo (`ContainingAssembly.Identity.Name`, inclusive tipos de metadata), com a mesma semântica
  de curinga do `namespace` (`*`, `**`, nome exato). As regras existentes passam a valer entre projetos
  (#23).
- **Placeholders `[Nome]`** em critérios `namespace`/`assembly` (um segmento, capturado; combinam com `*`/`**`)
  e campo opcional **`when`** (`same`/`differ`) em `forbidden-call` e `must-route-through`, para isolar
  módulos sem uma regra por par (`Acme.[Module].Services.**` não chama `Acme.[Module].Domain.**` de outro
  módulo). Placeholder malformado/repetido → `ARCH9001`; `when` com nome que não seja capturado pela camada `from` **e** pela `to` → `ARCH9008`.
  `ILayerResolver.GetCaptures` expõe as capturas (#24).

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
- **Publicação** (`publish.yml`): tag `vX.Y.Z` na `main` → job `build` (`contents: read`: build, testes e
  pack) → job `publish` (sem checkout de código; é o único com `id-token: write`) → nuget.org por
  **Trusted Publishing** (OIDC, sem API key no repositório) → GitHub Release com os pacotes. A Release é
  idempotente: se a tag já tiver uma, os pacotes anexados são atualizados (`--clobber`) em vez de falhar.
- **`arch-rules --strict`** (`validate` e `gen`): qualquer aviso (`ARCH9003`, `ARCH9008`, `ARCH9005`…) vira
  erro com saída 1; em `gen`, nada é gerado nesse caso. `--help` documenta as opções e os códigos de saída.
- **Limites de entrada do YAML:** arquivo acima de 1 MiB ou aninhamento de mapping/sequence acima de 64
  níveis é rejeitado com `ARCH9001` (com linha/coluna), antes de carregar a DOM — um `[` repetido 100 mil
  vezes não estoura mais a pilha. Exceções do scanner que não são `YamlException` (ex.: `...` seguido de
  conteúdo) também viram `ARCH9001` com posição.
- Documentação orientada a consumidor (`README.md`, também o `PackageReadmeFile` dos dois pacotes),
  `CONTRIBUTING.md` e este `CHANGELOG.md`.
- Skill `skills/rulekit-arch-rules` para assistentes de código (Claude Code e similares) configurarem o
  RuleKit num projeto consumidor: fluxo guiado, referência do YAML, diagnósticos e CLI/CI.

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
- **`arch-rules validate` sai com 1 quando há `ARCH9008`** (regra malformada é sempre erro do usuário);
  `ARCH9003` (tipo desconhecido, possivelmente de uma versão futura) continua aviso com saída 0, e `gen`
  continua gerando com avisos. Antes, `validate` saía 0 mesmo descartando uma regra.
- **Regra `enabled: false` não entra na validação de slot** do CLI (formato, pool, duplicidade), em
  paridade com o analyzer; o slot dela só aparece no lock file, com `enabled: false`.
- **Caminho de `extends`** normalizado numa única função (`ExtendsChainResolver.NormalizeDeclaredPath`:
  trim + `\` → `/`) usada por analyzer e CLI — `sub\base.yaml` e ` base.yaml ` resolvem nos dois, inclusive
  em Linux.
- **`RuleKit.Cli` multi-target `net8.0;net10.0`** (`RollForward=Major`): exige runtime .NET 8+ em vez de 10+.
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
- O `RuleKit.Cli` exige runtime .NET 8 ou superior.

[Unreleased]: https://github.com/SamuelGFDias/rulekit/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/SamuelGFDias/rulekit/releases/tag/v0.1.0
