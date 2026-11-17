---
name: rulekit-arch-rules
description: Use ao instalar e configurar RuleKit.Analyzer (e a ferramenta RuleKit.Cli / `arch-rules`) num projeto .NET, ao escrever ou revisar um `arch-rules.yaml` (camadas, regras `forbidden-call`, `must-route-through`, `naming-convention`, `max-dependencies`), ao gerar o `.globalconfig` e o lock file com `arch-rules gen`, ao ajustar severidade ou silenciar uma regra, ao interpretar diagnósticos `ARCH0001`..`ARCH0512` e `ARCH9001`..`ARCH9008`, ou ao validar a política arquitetural no CI.
---

# Skill: RuleKit — configurar regras arquiteturais num projeto .NET

Esta skill é para quem **consome** os pacotes NuGet `RuleKit.Analyzer` e `RuleKit.Cli`. O RuleKit é um
Roslyn Analyzer sem regra embutida: a organização declara a própria política num `arch-rules.yaml` e
recebe diagnósticos de violação no `dotnet build`, no CI e na IDE.

Carregue só o que a tarefa pedir:

- [`references/yaml-referencia.md`](references/yaml-referencia.md): schema completo do YAML (camadas,
  critérios, os 4 tipos de regra e cada campo), `extends`, e um exemplo completo e validado.
- [`references/diagnosticos.md`](references/diagnosticos.md): `ARCH0xxx` (violações) e `ARCH9001`..`ARCH9008`
  (erros do motor), causa e correção, como silenciar.
- [`references/cli-e-ci.md`](references/cli-e-ci.md): `arch-rules gen`/`validate`, arquivos gerados e um
  job de CI.

## Como a lib pensa (leia uma vez)

O Roslyn lê a lista de diagnósticos de um analyzer **antes** de existir qualquer YAML e a guarda durante
toda a sessão da IDE; um ID inventado em runtime vira `AD0001` e some. Por isso a lib pré-declara 512
**slots** (`ARCH0001`..`ARCH0512`) e o YAML **mapeia** o ID de negócio da organização a um deles:

```yaml
- id: ACME-001       # identidade da organização: aparece na mensagem
  slot: ARCH0007     # identidade técnica: é o que compilador, .editorconfig e #pragma enxergam
```

Consequências práticas: cada regra precisa de um slot **único** que o usuário escolhe (v1 não aloca nada
sozinha); `#pragma warning disable ARCH0007` e `dotnet_diagnostic.ARCH0007.severity` funcionam por regra;
e a severidade **efetiva** não vem do YAML, vem do `.globalconfig` gerado por `arch-rules gen`.

## Passo 1: Diagnosticar o projeto

```bash
# O pacote já está instalado? (com gestão central a versão fica em Directory.Packages.props)
grep -rn "RuleKit" --include=*.csproj --include=Directory.Packages.props --include=Directory.Build.props --include=dotnet-tools.json .
dotnet list package | grep -i rulekit
# Já existe política?
find . \( -name 'arch-rules*.yaml' -o -name '.globalconfig' \) -not -path '*/obj/*' -not -path '*/bin/*'
# Última versão publicada (os dois pacotes saem juntos)
curl -s https://api.nuget.org/v3-flatcontainer/rulekit.analyzer/index.json
```

- Um `404`/`BlobNotFound` na última chamada significa que o pacote **ainda não está no nuget.org**
  (verificado em 2026-11-17: nem `rulekit.analyzer` nem `rulekit.cli` estavam). Nesse caso gere os
  `.nupkg` do repositório (<https://github.com/SamuelGFDias/rulekit>: `dotnet pack src/Arch.Analyzer -c
  Release -o <dir>` e `dotnet pack src/Arch.Cli -c Release -o <dir>`) e use `--source <dir>` ou um
  `NuGet.Config`. Avise o usuário; não invente uma versão.
- Requisito: o analyzer foi compilado contra `Microsoft.CodeAnalysis.CSharp` 4.8, então precisa do
  compilador do **SDK .NET 8+** (ou Visual Studio 2022 17.8+). O `TargetFramework` do projeto é livre. O
  `arch-rules` (CLI) roda em **.NET 8+**.
- Sem pacote → Passo 2 em diante. Com pacote e YAML → vá direto ao que o usuário pediu (revisar regra,
  ajustar severidade, entender um diagnóstico) usando as referências.
- Sem um `arch-rules.yaml` ao lado do `.csproj`, o analyzer **não faz nada e não avisa**: instalar o
  pacote sozinho não produz diagnóstico.

## Passo 2: Levantar a arquitetura real e perguntar as regras

**Não invente política.** Olhe o código para propor, e pergunte ao usuário o que valer:

```bash
find . -name '*.csproj' -not -path '*/obj/*'
grep -rhoE '^namespace [A-Za-z0-9_.]+' --include=*.cs . | sort | uniq -c | sort -rn | head -40
grep -rhoE 'class [A-Za-z0-9_]+(Controller|Service|Repository|Handler|Validator)\b' --include=*.cs . | sort -u | head -30
```

Levante: os namespaces/projetos que formam as camadas; sufixos de nome (`Controller`, `Service`,
`Repository`); interfaces ou classes base que identificam uma camada (`IRepository<T>`,
`ControllerBase`); atributos marcadores. Depois **pergunte** (use a ferramenta de perguntas estruturadas
do agente, se houver): quais camadas existem; quem **não** pode chamar quem; que convenções de nome valem
por camada; qual teto de dependências por construtor; qual severidade cada regra deve ter (`error` quebra
o build); qual padrão de ID de negócio usar (`ACME-001`...). Se o projeto já tem violações, sugira começar
as regras novas como `warning` para dimensionar o problema, e promover a `error` depois.

## Passo 3: Instalar

```bash
dotnet add package RuleKit.Analyzer             # no(s) projeto(s) a validar; sem --version
dotnet new tool-manifest                         # só se não houver manifesto de tools
dotnet tool install RuleKit.Cli                  # ferramenta local; comando: dotnet arch-rules
```

- Não fixe `--version`: o comando sem versão resolve a última. Com gestão central de pacotes, a versão vai
  no `<PackageVersion>` de `Directory.Packages.props`.
- `dotnet add package` já grava o pacote como dependência de desenvolvimento (`PrivateAssets=all`).
- Alternativa global: `dotnet tool install -g RuleKit.Cli` (comando `arch-rules`).

## Passo 4: Escrever o `arch-rules.yaml`

Crie `arch-rules.yaml` **ao lado do `.csproj`** (o pacote o registra sozinho como `AdditionalFiles`, junto
com `arch-rules.*.yaml`). Primeira linha: o modeline do schema, para validação e autocomplete no editor.

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/SamuelGFDias/rulekit/main/docs/schema/arch-rules.v1.json
schema: arch-rules/v1
```

Monte, nesta ordem, com o que o usuário respondeu no Passo 2:

1. `layers`: nome livre → `match` (critérios `namespace`/`nameSuffix`/`baseType`/`implements`/`attribute`,
   **combinados por OR**) e `exclude` opcional (vence o match).
2. `severities` (nomes da organização → `error`/`warning`/`suggestion`/`silent`/`none`) e
   `defaults.severity`.
3. `rules`: cada uma com `id` de negócio, **`slot` único** (`ARCH0001`, `ARCH0002`...), `type` e os campos
   do tipo. Os quatro tipos e os campos de cada um estão em `references/yaml-referencia.md`, junto com um
   exemplo completo (Controller/Service/Repository/Domain) para adaptar.

Conferir a grafia dos nomes de camada nas regras: uma camada inexistente em `from`/`to`/`layer` **não dá
erro**, a regra só nunca dispara.

## Passo 5: Gerar `.globalconfig` e lock file

```bash
dotnet arch-rules validate arch-rules.yaml   # confere schema, slots, severidades, extends; não gera nada
dotnet arch-rules gen arch-rules.yaml        # gera .globalconfig + arch-rules.lock.yaml ao lado do YAML
```

- Leia **todas** as linhas `ERRO`/`AVISO` do `validate`: `ARCH9008` (campo ausente/inválido) sai como
  `ERRO` (código 1); `ARCH9003` (tipo desconhecido) só avisa e a regra fica de fora. Use `--strict` para
  qualquer aviso falhar.
- Versione o `arch-rules.yaml`, o `.globalconfig` e o `arch-rules.lock.yaml`. O `.globalconfig` ao lado do
  `.csproj` é aplicado pelo compilador sem editar o `.csproj`.
- A severidade que o compilador usa vive no `.globalconfig` (ou `.editorconfig`), **não** no YAML. Mudou a
  `severity` no YAML? Rode `arch-rules gen` de novo; sem isso o analyzer reporta `ARCH9005` (info)
  dizendo que o YAML e a severidade efetiva divergem.

## Passo 6: Compilar e conferir

```bash
dotnet build
```

- Esperado: linhas `error|warning ARCH00NN: ACME-NNN: <mensagem>` para as violações reais.
- **Não** pode aparecer `AD0001` nem `ARCH9xxx` inesperado; se aparecer, abra `references/diagnosticos.md`.
- Regra com severidade `error` faz o build falhar: isso é o comportamento pedido, não um defeito.
- Valide a política de ponta a ponta: crie (ou localize) um caso que deve violar cada regra e um que deve
  passar, e confirme que só o primeiro diagnostica. Se uma regra nunca dispara, suspeite primeiro de nome
  de camada digitado errado, ou de uma camada que não cobre a interface/tipo realmente chamado.

## Passo 7: CI

Rode `arch-rules validate --strict` e confira que o `.globalconfig` e o lock gerados
estão em sincronia com o YAML. Job completo em `references/cli-e-ci.md`.

## Armadilhas (e o porquê)

- **Slot fora de `ARCH0001`–`ARCH0512` ou repetido** → `ARCH9002`: a regra é ignorada. Dois IDs no mesmo
  slot impediriam dar override de severidade a um sem afetar o outro. O CLI recusa gerar arquivos.
- **`type` desconhecido** → `ARCH9003` (aviso, regra ignorada): é como a lib permite tipos novos em versões
  futuras sem derrubar o arquivo. Os 4 de v1 estão na referência; **não há extensão de terceiros na v1**.
- **Campo obrigatório faltando, chave que o tipo não aceita ou valor inválido** (`max: abc`, `countBy:
  ctor`) → `ARCH9008`: a regra é ignorada **de forma ruidosa**, nunca em silêncio. No `dotnet build` é um
  aviso e o build segue; leia-o.
- **Regra sem `id`, `slot` ou `type`** é `ARCH9001` (bloqueante), não `ARCH9008`.
- **`extends` fora da convenção** `arch-rules.*.yaml` ao lado do `.csproj` → `ARCH9006`: o analyzer roda
  dentro do compilador e não lê disco, só enxerga `AdditionalFiles`. Renomeie o arquivo base para
  `arch-rules.base.yaml` ou adicione `<AdditionalFiles Include="..." />` no `.csproj`.
- **Ciclo de `extends`** → `ARCH9007` no build (e `CLI-EXTENDS`, bloqueante, no CLI).
- **Nome de tipo não resolvido** em `implements`/`baseType`/`attribute` → `ARCH9004`: use o nome
  totalmente qualificado, a aridade em genéricos (`Ns.IRepository`1`) e o sufixo `Attribute`; o pacote
  que define o tipo precisa estar referenciado pelo projeto. Só aquele critério é ignorado.
- **YAML com anchors (`&`), aliases (`*`), multi-documento (`---`) ou tags (`!!`)** é rejeitado
  (`ARCH9001`): o subset aceito é restrito de propósito. **Chave desconhecida na raiz, em camada, em
  critério ou em `defaults`** também é `ARCH9001`, e invalida o arquivo inteiro (nenhuma regra roda).
- **`info`/`hidden` em `severities:`** não existem: use `error`, `warning`, `suggestion`, `silent` ou
  `none`. Toda regra, até a `enabled: false`, precisa resolver uma severidade (direta ou por
  `defaults.severity`), senão o CLI falha com `CLI-SEVERITY`.
- **`forbidden-call` só vê** chamada de método, `new`, leitura/escrita de propriedade e de campo, e
  classifica pelo tipo que **declara** o membro. Injetar um `Repository` no construtor sem usá-lo não é
  uma violação; chamar pela interface só é pega se a interface também estiver na camada de destino.
- **`must-route-through` não verifica a rota** `from → through → to`: é `forbidden-call` de `from` para
  `to` com `through` na mensagem.
- **`message:`** só é usado por `forbidden-call` e `must-route-through`; os outros dois ignoram.
- **Silenciar/ajustar por regra** funciona por slot: `#pragma warning disable ARCH0007` e
  `dotnet_diagnostic.ARCH0007.severity = none` num `.editorconfig`. Preferível: mudar `severity`/`enabled`
  no YAML e rodar `arch-rules gen`, para o lock refletir a decisão.
- **Reaproveitar o slot** de uma regra removida faria `#pragma`/`.editorconfig` antigos valerem para a regra
  nova; prefira slots novos e estáveis.
- **Live analysis na IDE** (Error List atualizando enquanto digita) **não foi verificada** pelo projeto;
  o que está provado é `dotnet build` e CI. Os erros do motor (`ARCH9xxx`) saem no fim da compilação e
  podem não aparecer na análise ao vivo padrão. Reflexão, `dynamic` e service locators escapam da
  detecção: a lib previne deriva, não prova conformidade.
