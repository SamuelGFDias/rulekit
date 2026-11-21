# RuleKit

RuleKit valida padrões arquiteturais de projetos .NET **em tempo de compilação**. É um Roslyn
Analyzer configurável: a lib não embute nenhuma regra de negócio — cada organização declara a sua
política num arquivo YAML (`arch-rules.yaml`) e recebe diagnósticos de violação no `dotnet build`,
no CI e na IDE, sem fork da lib e sem regra hardcoded. A severidade de cada regra é ajustável
individualmente por `.editorconfig`/`.globalconfig` e silenciável pontualmente com
`#pragma warning disable`.

Dois pacotes no NuGet:

| Pacote | O que é |
|---|---|
| `RuleKit.Analyzer` | O analyzer (DLL única, sem dependências). Instalado no projeto que você quer verificar. |
| `RuleKit.Cli` | A ferramenta `arch-rules` (`dotnet tool`): valida o YAML e gera o `.globalconfig` com as severidades. |

## Instalação

```bash
# no projeto que será verificado
dotnet add package RuleKit.Analyzer

# a ferramenta de linha de comando (global; também funciona como ferramenta local do repositório)
dotnet tool install -g RuleKit.Cli        # comando: arch-rules
```

### Requisitos

- **Analyzer (`RuleKit.Analyzer`):** compilado contra `Microsoft.CodeAnalysis.CSharp` 4.8, a menor
  versão suportada — exige compilador do **SDK .NET 8+** ou **Visual Studio 2022 17.8+**. O CI também
  o carrega no MSBuild/csc (.NET Framework) do Visual Studio 18, sem `FileLoadException` nem `AD0001`.
  O projeto consumidor pode ter qualquer `TargetFramework` que o seu SDK compile.
- **CLI (`RuleKit.Cli`):** exige o **runtime .NET 8 ou superior**. A ferramenta é multi-target
  (`net8.0;net10.0`): `dotnet tool install` escolhe o build compatível com o seu SDK (8/9 → `net8.0`,
  10 → `net10.0`) e `RollForward=Major` permite rodar em runtimes mais novos, nunca em mais antigos.
  Sem .NET 8+ instalado, a ferramenta não inicia — veja [Limitações](#limitações-conhecidas).

## Quickstart

1. Crie `arch-rules.yaml` **ao lado do `.csproj`** do projeto verificado:

   ```yaml
   # yaml-language-server: $schema=https://raw.githubusercontent.com/SamuelGFDias/rulekit/main/docs/schema/arch-rules.v1.json
   schema: arch-rules/v1

   layers:
     Controller:
       match:
         - nameSuffix: Controller
     Repository:
       match:
         - nameSuffix: Repository

   severities:
     bloqueante: error

   rules:
     - id: ACME-001
       slot: ARCH0001
       type: forbidden-call
       from: Controller
       to: Repository
       severity: bloqueante
       message: "{from} não pode acessar {to} diretamente ({symbol}). Passe por um Service."
   ```

2. Valide e gere o `.globalconfig` (e o `arch-rules.lock.yaml`) ao lado do YAML:

   ```bash
   arch-rules validate arch-rules.yaml     # OK: configuração válida.
   arch-rules gen arch-rules.yaml          # Gerado: .../.globalconfig  e  .../arch-rules.lock.yaml
   ```

3. Rode `dotnet build`. Um `Controller` que usa um `Repository` diretamente agora **quebra o build**:

   ```text
   Controllers/PedidoController.cs(5,29): error ARCH0001: ACME-001: Controller não pode acessar Repository diretamente (PedidoRepository.Todos). Passe por um Service.
   ```

Nenhuma edição de `.csproj` é necessária: o pacote registra o `arch-rules.yaml` do projeto como
`AdditionalFiles` sozinho, e o compilador descobre o `.globalconfig` na pasta do projeto. Sem rodar
`arch-rules gen`, as regras já funcionam, mas todas reportam o padrão do slot — **warning** — em vez
da severidade declarada no YAML.

## Como funciona: o pool de slots

O Roslyn lê `SupportedDiagnostics` sem contexto de compilação (antes de existir qualquer YAML), e
um diagnóstico com ID não declarado ali vira `AD0001`. Por isso o analyzer pré-declara **512 slots
estáveis, `ARCH0001`…`ARCH0512`**, e o YAML mapeia o ID de negócio da sua organização a um deles:

```yaml
- id: ACME-001      # identidade da organização — aparece na mensagem e em Diagnostic.Properties["archRuleId"]
  slot: ARCH0007    # identidade técnica — é o que o compilador, o .editorconfig e o #pragma enxergam
```

Cada regra precisa de um slot próprio (slot duplicado ou fora do pool → [`ARCH9002`](#diagnósticos-do-motor-arch9001arch9009)).
Quem escolhe o slot é você: o CLI não aloca, só valida. Com isso:

```ini
# .editorconfig — override de severidade por regra, por projeto/pasta
[*.cs]
dotnet_diagnostic.ARCH0007.severity = suggestion
```

```csharp
#pragma warning disable ARCH0007 // silencia só esta regra, só neste trecho
```

A decisão completa e as alternativas descartadas estão no
[ADR-001](https://github.com/SamuelGFDias/rulekit/blob/main/ADR-001-motor-regras-arquiteturais.md).

## Referência do YAML

O schema machine-readable está em
[`docs/schema/arch-rules.v1.json`](https://github.com/SamuelGFDias/rulekit/blob/main/docs/schema/arch-rules.v1.json)
(JSON Schema draft 2020-12). Para ter autocomplete e validação no editor (VS Code com a extensão
YAML da Red Hat, por exemplo), coloque o modeline na primeira linha do arquivo:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/SamuelGFDias/rulekit/main/docs/schema/arch-rules.v1.json
```

O YAML aceito é um subconjunto restrito: **sem anchors/aliases, sem multi-documento, sem tags
customizadas**; chave desconhecida é erro, não silêncio. Arquivos acima de **1 MiB** ou com mais de
**64 níveis** de aninhamento (mapping/sequence) são rejeitados com `ARCH9001`.

### Chaves de nível raiz

| Chave | Obrigatória | Descrição |
|---|---|---|
| `schema` | sim | Sempre `arch-rules/v1`. Outra MAJOR → `ARCH9001`. |
| `extends` | não | Lista de caminhos de outros YAMLs a compor (ver [extends](#extends-e-a-convenção-arch-rulesyaml)). |
| `layers` | sim | Mapa `NomeDaCamada → { match: [...], exclude?: [...] }`. |
| `severities` | não | Mapa `nome-livre → nível real` (`error`, `warning`, `suggestion`, `silent`, `none`). |
| `defaults.severity` | não | Nome de severidade aplicado às regras que não declaram `severity`. |
| `rules` | sim | Lista de regras. |

**Camadas.** Cada item de `match`/`exclude` tem exatamente **uma** chave: `namespace`, `nameSuffix`,
`baseType`, `implements`, `attribute` ou `assembly`. Itens da mesma lista combinam por **OU**; `exclude` é
aplicado depois do `match`. `namespace` aceita `*` (exatamente um segmento) e `**` (zero ou mais):
`Acme.Api.**` casa `Acme.Api` e `Acme.Api.Controllers.V2`, e um namespace sem curinga casa só ele
mesmo, nunca os descendentes. `assembly` compara o padrão com o nome do assembly que contém o tipo
(`ContainingAssembly.Identity.Name`, também para tipos vindos de referências), com a mesma semântica de
curinga do `namespace` — assim as regras existentes valem entre projetos
(`assembly: "Acme.Infrastructure.**"`). Em `namespace` e `assembly`, um segmento `[Nome]`
(`Nome` = letra seguida de letras/dígitos) casa **exatamente um** segmento e **captura** o valor
(`Acme.[Module].Services.**` captura `Vendas` em `Acme.Vendas.Services.PedidoService`); combina com `*` e
`**`, o nome é único por padrão, e placeholder malformado ou repetido é `ARCH9001`. Por tipo e por camada
vale a captura do primeiro critério de `match` **com placeholder**, na ordem declarada, que casar
(`exclude` não captura); as capturas alimentam o campo `when` das regras (abaixo). `baseType`/`implements`/`attribute` pedem o nome totalmente
qualificado; um nome que não resolve na compilação gera `ARCH9004` e o critério é ignorado.

**Severidades.** Os nomes são livres (`bloqueante`, `aviso`…); os valores são só os cinco que o
`dotnet_diagnostic.<ID>.severity` aceita — nunca `info`/`hidden`. `suggestion` vira `Info` e
`silent` vira `Hidden` na escala do compilador. Toda regra precisa resolver para uma severidade
(própria ou `defaults.severity`), senão o `arch-rules validate`/`gen` falha.

### Campos comuns de cada regra

`id` (livre, pode ter hífen), `slot` (`ARCH0001`…`ARCH0512`), `type`, `enabled` (padrão `true`; `false`
mantém a regra documentada sem reportar nada), `severity`, `message`, `help` (URL exposta em
`Diagnostic.Properties["archHelp"]`).

`message` aceita os placeholders `{from}`, `{to}`, `{symbol}`, `{id}` e qualquer campo da regra
(`{through}`, `{layer}`…; sensíveis a caixa, como a chave do YAML), mas só é usado por
`forbidden-call` e `must-route-through` — `naming-convention` e `max-dependencies` emitem uma
mensagem fixa.

### Os quatro tipos de regra

O catálogo é a fonte de verdade do parser (`RuleTypeCatalog`): tipo de regra conhecido com campo
obrigatório ausente, chave que o tipo não aceita ou valor inválido → regra ignorada com
[`ARCH9008`](#diagnósticos-do-motor-arch9001arch9009). `type` ignora caixa e espaços nas pontas
(`Forbidden-Call` vale `forbidden-call`); os nomes de campo, não.

| `type` | Obrigatórios | Opcionais | Semântica |
|---|---|---|---|
| `forbidden-call` | `from`, `to` | `when` | Nenhum tipo da camada `from` pode invocar método, criar instância ou ler propriedade/campo de um tipo da camada `to`. `when` restringe a regra às capturas `[Nome]` das camadas: `{ same: [Nome, ...] }` só reporta quando os valores capturados são iguais nos dois lados, `{ differ: [Nome, ...] }` quando ao menos um difere (um escalar solto vale como lista de um item). Lado sem a captura → o par não é reportado; nome que não seja capturado por um critério de `match` da camada `from` **e** por um da camada `to` → `ARCH9008` (regra ignorada). Ex.: `from: ModuleServices`, `to: ModuleDomain`, `when: { differ: [Module] }` com `Acme.[Module].Services.**` / `Acme.[Module].Domain.**`. |
| `must-route-through` | `from`, `to` | `through`, `when` | Mesma detecção de `forbidden-call` (é açúcar sobre ele), inclusive `when`. `through` (lista de camadas) só alimenta a mensagem (`{through}`) — o caminho real `from → through → to` **não** é verificado. |
| `naming-convention` | `layer`, `require` | — | Todo tipo da camada `layer` precisa obedecer `require: { suffix: X }` ou `require: { prefix: X }` — exatamente um par. |
| `max-dependencies` | `layer`, `max`, `countBy` | — | Tipo da camada `layer` não pode ter mais que `max` (inteiro ≥ 0) dependências **distintas**; limite inclusivo. `countBy`: `constructor-parameters` (maior construtor público), `fields` (campos de instância `private`/`protected` do próprio tipo, não primitivos) ou `all` (união das duas). |

Exemplo com os quatro:

```yaml
rules:
  - { id: ACME-001, slot: ARCH0001, type: forbidden-call,     from: Controller, to: Repository, severity: bloqueante }
  - { id: ACME-002, slot: ARCH0002, type: must-route-through, from: Service, to: Persistence, through: [Repository], severity: aviso }
  - { id: ACME-003, slot: ARCH0003, type: naming-convention,  layer: Repository, require: { suffix: Repository }, severity: aviso }
  - { id: ACME-004, slot: ARCH0004, type: max-dependencies,   layer: Service, max: 5, countBy: constructor-parameters, severity: aviso }
```

Um `type` que esta versão não conhece gera `ARCH9003` (warning) e **só aquela regra** é ignorada —
as demais continuam valendo (política MINOR aditiva do ADR-001 D4).

## extends e a convenção `arch-rules.*.yaml`

`extends: [arch-rules.base.yaml]` compõe políticas: o arquivo que estende vence, e `layers`/`rules`
fazem merge por nome/`id` (a regra do arquivo que estende substitui a base, na mesma posição;
`enabled: false` desliga sem apagar). Os caminhos são relativos ao arquivo que os declara.

O analyzer roda dentro do compilador e **não lê disco**: só enxerga o que o MSBuild entrega em
`AdditionalFiles`. O pacote adiciona automaticamente, do diretório do projeto, o `arch-rules.yaml` e
todo `arch-rules.*.yaml` (menos o `arch-rules.lock.yaml`). Um arquivo de `extends` com outro nome ou
em outro diretório precisa de uma linha no `.csproj`:

```xml
<ItemGroup>
  <AdditionalFiles Include="..\shared\politica-corporativa.yaml" />
</ItemGroup>
```

Sem ela o analyzer avisa com `ARCH9006` dizendo qual caminho faltou. O CLI lê o disco e resolve a
cadeia inteira com qualquer nome — então um `validate` verde no CI **não** prova que o analyzer
enxergou o arquivo.

## Diagnósticos do motor (ARCH9001–ARCH9009)

A faixa `ARCH9xxx` é reservada aos erros do próprio motor, sempre habilitada por padrão e nunca
alocável como slot. Pode ser ajustada ou silenciada como qualquer diagnóstico
(`dotnet_diagnostic.ARCH9006.severity = none`, `#pragma`).

| ID | Severidade | Quando ocorre | Efeito |
|---|---|---|---|
| `ARCH9001` | Error | YAML malformado, `schema` ausente ou de MAJOR não suportada, chave desconhecida (raiz, camada, `defaults`), regra sem `id`/`slot`/`type`. | O arquivo inteiro é rejeitado; nenhuma regra é avaliada. |
| `ARCH9002` | Warning | `slot` fora de `ARCH0001`…`ARCH0512` ou repetido entre regras. | A regra é ignorada. |
| `ARCH9003` | Warning | `type` de regra que esta versão não conhece. | A regra é ignorada. |
| `ARCH9004` | Warning | Nome em `baseType`/`implements`/`attribute` que não resolve na compilação (não qualificado, genérico sem aridade, pacote não referenciado). | O critério é ignorado. Um aviso por nome. |
| `ARCH9005` | Info | A severidade declarada no YAML diverge da efetiva do slot — sinal de `.globalconfig` ausente ou desatualizado. | Informativo; rode `arch-rules gen`. |
| `ARCH9006` | Warning | Um caminho de `extends` não tem `AdditionalFile` correspondente. | Aquela camada é ignorada; o resto da cadeia vale. |
| `ARCH9007` | Warning | Cadeia de `extends` com ciclo ou acima do limite (32 níveis / 64 arquivos). | O ramo problemático é cortado. |
| `ARCH9008` | Warning | Regra de tipo conhecido com campo obrigatório ausente, chave que o tipo não aceita ou valor inválido (`max` não inteiro, `countBy` fora dos aceitos…). | A regra é ignorada. |
| `ARCH9009` | Warning | O [`arch-rules.baseline.yaml`](#baseline-de-violações) está inválido (YAML malformado, `schema` diferente de `arch-rules-baseline/v1`, chave desconhecida, item de `entries` sem `rule`/`file`/`subject`). Aponta a linha/coluna. | O baseline não suprime nada até ser corrigido (ou regerado com `arch-rules baseline`). |

`ARCH9005` é `Info`: não aparece no console do `dotnet build` (nem em `-v:d`); para vê-lo, gere um log
de erros (`dotnet build -p:ErrorLog=build.sarif`).

## CLI: `arch-rules`

```text
arch-rules validate <yaml> [--strict]                  valida o schema, os slots e as severidades; não escreve nada
arch-rules gen <yaml> [--out <diretório>] [--strict]   o mesmo, e gera .globalconfig + arch-rules.lock.yaml
```

Ambos resolvem a cadeia de `extends` do disco e saem com código **1** quando algo bloqueia: YAML
inválido (`ARCH9001`), slot inválido ou duplicado entre regras habilitadas (`ARCH9002`; regra com
`enabled: false` não entra nessa validação, como no analyzer), `extends` ausente/cíclico ou severidade
não resolvida. Problemas que só descartam **uma regra** têm tratamento próprio:

| Aviso | `validate` | `gen` |
|---|---|---|
| `ARCH9008` (campo ausente/inválido — regra malformada) | **erro, saída 1** | `AVISO`, gera e sai 0 |
| `ARCH9003` (tipo desconhecido — pode ser de uma versão futura da lib) | `AVISO`, saída 0 | `AVISO`, gera e sai 0 |
| qualquer aviso com `--strict` (em `validate`, inclui o `ARCH9005` de lock divergente; `gen` não compara com o lock) | **erro, saída 1** | **erro, saída 1, nada é gerado** |

Use `arch-rules validate --strict` no CI para não deixar passar nenhum aviso. `gen` escreve por padrão
ao lado do YAML — onde o compilador descobre o `.globalconfig` sem configuração — e `--out` muda o
destino (nesse caso referencie o arquivo no `.csproj` como `GlobalAnalyzerConfigFiles`).

O `.globalconfig` gerado tem uma linha por regra habilitada:

```ini
is_global = true

dotnet_diagnostic.ARCH0001.severity = error
```

O `arch-rules.lock.yaml` é o retrato `id → slot → tipo → severidade → enabled`, para revisar mudanças
de política em code review. Se ele já existir e divergir do que seria gerado, o `validate` avisa
(`AVISO [ARCH9005]`, ou erro com `--strict`) pedindo para rodar `gen` de novo — comite os dois arquivos.

## Baseline de violações

Num código legado, ligar uma regra como `error` quebraria o build por violações que já existem. O baseline
registra essas violações num arquivo versionado e o analyzer **suprime só elas**: o build quebra apenas por
violação **nova**, e a dívida pode encolher sem nunca voltar a crescer. A supressão fica registrada no SARIF
(`suppressions`) — não é silêncio.

```bash
# 1. build com log de erros (SARIF 2.1); na linha de comando do MSBuild a vírgula é escapada: %2C
dotnet build -p:ErrorLog=build.sarif%2Cversion=2.1

# 2. gera arch-rules.baseline.yaml no diretório do arch-rules.yaml mais próximo de cada violação
arch-rules baseline build.sarif

# 3. comite o arquivo; a partir daqui só violação nova é reportada (e quebra o build se for error)
git add arch-rules.baseline.yaml

# 4. no CI, depois do build com ErrorLog: sai 1 se alguma entrada ficou obsoleta (violação corrigida)
arch-rules baseline build.sarif --check

# 5. para encolher o baseline: remove as entradas obsoletas, nunca acrescenta
arch-rules baseline build.sarif --update
```

```text
arch-rules baseline <sarif>... [--update | --check] [--strict]
```

| Modo | O que faz |
|---|---|
| (sem flag) | Regenera o `arch-rules.baseline.yaml` de cada diretório com **todas** as violações de slot do SARIF (pode acrescentar). |
| `--update` | Ratchet: mantém só as entradas existentes que ainda aparecem no SARIF; nunca acrescenta. Diretório sem baseline é ignorado. |
| `--check` | Não escreve nada; sai **1** se alguma entrada de um baseline existente não aparece mais no SARIF (lista as obsoletas), **0** caso contrário. |
| `--strict` | Qualquer aviso (arquivo-fonte sem `arch-rules.yaml` em nenhum diretório ancestral, resultado sem `archRuleId`/`archSubject`) sai 1 e nada é escrito. |

Aceita vários SARIFs (um por projeto da solução). Cada violação vai para o baseline do diretório ancestral
mais próximo do arquivo-fonte que contém `arch-rules.yaml`, com o caminho relativo a esse diretório. Um
baseline só enxerga os diretórios que aparecem no SARIF: se a **última** violação de um projeto for
corrigida, esse diretório não aparece mais — apague o baseline à mão.

O arquivo (`schema: arch-rules-baseline/v1`, subset de YAML do `arch-rules.yaml`, chave desconhecida é erro)
tem uma entrada por violação, **sem número de linha**:

```yaml
schema: arch-rules-baseline/v1
entries:
  - rule: "ACME-001"                       # ID de negócio da regra
    file: "Services/OrderService.cs"       # relativo ao diretório do baseline, com '/'
    subject: "Acme.Services.OrderService -> Acme.Data.OrderRepository.Load"
```

O **assunto** (`subject`) é o `archSubject` que os avaliadores gravam em `Diagnostic.Properties` (junto de
`archRuleId`): `forbidden-call`/`must-route-through` → `<tipo de origem> -> <tipo alvo>.<membro>` (só o tipo
alvo, na criação de objeto); `naming-convention`/`max-dependencies` → o tipo. Nomes qualificados, sem linha —
então editar o arquivo não invalida o baseline. Todas as ocorrências iguais (mesma regra, arquivo e assunto)
são suprimidas, sem contagem.

O `arch-rules.baseline.yaml` entra em `@(AdditionalFiles)` pela convenção `arch-rules.*.yaml` e é lido por
um `DiagnosticSuppressor` do pacote (suppressors `ARCHB0001`…`ARCHB0512`, um por slot). Baseline ausente → nada
é suprimido; baseline inválido → nada é suprimido e o analyzer reporta [`ARCH9009`](#diagnósticos-do-motor-arch9001arch9009)
com a linha/coluna do problema.

## Limitações conhecidas

- **Sem extensão de terceiros na v1.** Os quatro tipos de regra são fixos; `RuleEvaluatorRegistry`
  é interno e o pacote não expõe contratos públicos para registrar tipos novos
  ([#21](https://github.com/SamuelGFDias/rulekit/issues/21)).
- **Live analysis da IDE não verificada.** O build no host .NET Framework do Visual Studio é provado
  no CI, mas o Error List atualizando enquanto se digita não foi verificado
  ([#1](https://github.com/SamuelGFDias/rulekit/issues/1)), e os erros do motor (`ARCH9xxx`) saem no
  fim da compilação, que pode não rodar na análise ao vivo padrão
  ([#20](https://github.com/SamuelGFDias/rulekit/issues/20)).
- **`ARCH9005` é aproximado.** Compara o YAML com `/warnaserror`, `/nowarn`, ruleset e o
  `.globalconfig` (precedência do compilador), mas **ignora `.editorconfig` por árvore** (a severidade
  pode variar por arquivo) e `TreatWarningsAsErrors` (muda todos os warnings, não o slot).
- **Escopo do que se detecta.** Reflexão, `dynamic` e service locators escapam; código gerado não é
  analisado. A lib previne deriva, não prova conformidade. `must-route-through` não verifica a rota.
- **CLI exige runtime .NET 8+** (ver [Requisitos](#requisitos)).

## Skill para assistente de código

O repositório traz uma skill em [`skills/rulekit-arch-rules`](https://github.com/SamuelGFDias/rulekit/tree/main/skills/rulekit-arch-rules) que guia um assistente como o Claude Code a configurar o RuleKit num projeto: diagnosticar o que já existe, levantar a arquitetura real (namespaces, projetos, sufixos), perguntar a política que você quer em vez de inventá-la, instalar os pacotes, escrever o `arch-rules.yaml`, rodar `arch-rules gen`, conferir o build e montar o job de CI. As referências cobrem o YAML completo, todos os diagnósticos `ARCH9xxx` e o CLI.

Para usar no Claude Code, copie a pasta para `.claude/skills/` do seu projeto (ou para `~/.claude/skills/`, para valer em todos):

```bash
base=https://raw.githubusercontent.com/SamuelGFDias/rulekit/main/skills/rulekit-arch-rules
dest=.claude/skills/rulekit-arch-rules
mkdir -p "$dest/references"
curl -fsSL "$base/SKILL.md" -o "$dest/SKILL.md"
for f in yaml-referencia diagnosticos cli-e-ci; do
  curl -fsSL "$base/references/$f.md" -o "$dest/references/$f.md"
done
```

Depois, é só pedir algo como "configura o RuleKit neste projeto com as regras da nossa arquitetura em camadas". No claude.ai, a mesma pasta compactada em `.zip` pode ser enviada como skill.

## Mais

- [ADR-001](https://github.com/SamuelGFDias/rulekit/blob/main/ADR-001-motor-regras-arquiteturais.md) — decisão de arquitetura, alternativas e trade-offs.
- [CHANGELOG](https://github.com/SamuelGFDias/rulekit/blob/main/CHANGELOG.md) — o que mudou em cada versão.
- [CONTRIBUTING](https://github.com/SamuelGFDias/rulekit/blob/main/CONTRIBUTING.md) — estrutura do repositório, como compilar, testar, empacotar e medir performance.
- [Issues](https://github.com/SamuelGFDias/rulekit/issues) e licença [MIT](https://github.com/SamuelGFDias/rulekit/blob/main/LICENSE).
