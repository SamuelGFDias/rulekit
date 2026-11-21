# Referência do `arch-rules.yaml` (schema `arch-rules/v1`)

Tudo aqui foi conferido contra o código da lib (`Arch.Config`, `Arch.Rules`, `Arch.Analyzer`). O JSON
Schema para editores está em
<https://raw.githubusercontent.com/SamuelGFDias/rulekit/main/docs/schema/arch-rules.v1.json>.
Coloque na **primeira linha** do arquivo:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/SamuelGFDias/rulekit/main/docs/schema/arch-rules.v1.json
```

## Subset de YAML aceito

Sem anchors (`&x`), sem aliases (`*x`), sem multi-documento (`---` repetido), sem tags customizadas
(`!!algo`). Qualquer um desses é rejeitado com `ARCH9001` (bloqueante) e **nenhuma regra é avaliada**.
Isso inclui copiar blocos com anchor/alias para "não repetir" camadas: repita o texto, ou use
`extends:`. Chave complexa (`? [a, b] : v`) também é `ARCH9001`.

## Raiz

Chaves permitidas: `schema`, `extends`, `layers`, `severities`, `defaults`, `rules`. **Qualquer outra
chave na raiz é `ARCH9001` (bloqueante).**

| Chave | Obrigatória | Forma |
| --- | --- | --- |
| `schema` | sim (o parser exige) | exatamente `arch-rules/v1`. Outra major (`v2`) → `ARCH9001`; formato fora de `arch-rules/vN` → `ARCH9001`. |
| `extends` | não | lista de caminhos de outros YAMLs, relativos **ao arquivo que os declara**. Ver "extends" abaixo. |
| `layers` | não pelo parser; **sim pelo JSON Schema** (`layers` e `rules`, mínimo 1 camada) | mapa `NomeDaCamada` → definição. |
| `severities` | não | mapa `nome-livre` → `error`/`warning`/`suggestion`/`silent`/`none`. |
| `defaults` | não | só a chave `severity` (outra chave → `ARCH9001`). |
| `rules` | não pelo parser; sim pelo JSON Schema | lista de regras. |

## `severities` e `defaults`

Os **nomes** de severidade são da organização (`bloqueante`, `aviso`, `restrita`...). Os **valores** têm
de ser um destes cinco, os que `dotnet_diagnostic.<slot>.severity` aceita:

| Valor | Efeito no compilador |
| --- | --- |
| `error` | erro de compilação (o build falha) |
| `warning` | aviso |
| `suggestion` | sugestão (não aparece no console do `dotnet build`; aparece na IDE) |
| `silent` | oculto (hidden) |
| `none` | desligado |

`info` e `hidden` **não** são valores válidos (são do enum `DiagnosticSeverity`, não do AnalyzerConfig):
o `arch-rules gen`/`validate` recusa com `CLI-SEVERITY`.

`defaults.severity` e `rules[].severity` são **nomes** que precisam existir em `severities:`. O CLI exige
que **toda regra** (inclusive `enabled: false`) resolva uma severidade, direto ou pelo `defaults`; sem
isso falha com `CLI-SEVERITY`. O parser em si não valida essas referências; quem valida é o CLI.

## `layers`

```yaml
layers:
  Controller:                 # nome livre; é o que `from`/`to`/`layer` referenciam
    match:                    # obrigatório; lista; critérios combinados por OR
      - namespace: Acme.Shop.Api.Controllers.**
      - nameSuffix: Controller
    exclude:                  # opcional; lista; também por OR; vence o match
      - namespace: Acme.Shop.Api.Controllers.Internal.**
```

Uma camada só aceita as chaves `match` e `exclude` (outra → `ARCH9001`). Cada item das listas é um mapa
com **exatamente uma** chave de critério (zero ou duas → `ARCH9001`).

| Critério | Casa quando | Observações |
| --- | --- | --- |
| `namespace` | o namespace do tipo bate com o glob | `*` = exatamente **um** segmento; `**` = **zero ou mais** segmentos; sem curinga = namespace **exato** (`Acme.Api` não inclui `Acme.Api.Controllers`). Sensível a maiúsculas. Tipo aninhado usa o namespace do tipo externo. |
| `nameSuffix` | o nome simples do tipo termina com o texto | comparação ordinal (sensível a maiúsculas); `Controller` casa `PedidosController`. |
| `baseType` | algum ancestral na cadeia de herança é esse tipo | nome totalmente qualificado de metadata (`Microsoft.AspNetCore.Mvc.ControllerBase`). Genérico leva a aridade: `Acme.Base`1`. |
| `implements` | o tipo implementa a interface (inclui herdadas transitivamente) | mesmo formato de nome. Uma interface **não** "implementa" a si mesma. |
| `attribute` | o tipo tem esse atributo aplicado | o nome é o da classe do atributo, **com** o sufixo `Attribute` (`Acme.AggregateRootAttribute`). |
| `assembly` | o nome do assembly que contém o tipo bate com o padrão | `ContainingAssembly.Identity.Name`, também para tipos de referências (metadata). Mesma semântica de curinga do `namespace` (`*` = um segmento, `**` = zero ou mais, sem curinga = nome exato). Permite isolar projetos: `assembly: "Acme.Infrastructure.**"`. |

**Placeholders `[Nome]` (em `namespace` e `assembly`).** Um segmento `[Nome]` casa **exatamente um**
segmento e **captura** o valor sob `Nome` (`[A-Za-z][A-Za-z0-9]*`, único por padrão, combinável com `*` e
`**`): `Acme.[Module].Services.**` captura `Vendas` em `Acme.Vendas.Services.PedidoService`. Placeholder
malformado (`[Mod`, `[]`, `[1x]`, `Meu[Module]`) ou repetido no mesmo padrão → `ARCH9001` (bloqueante,
com linha/coluna). A captura é por (tipo, camada) e vale a do **primeiro critério de `match` com
placeholder, na ordem declarada, que casar**; `exclude` não captura. Servem para o campo `when`.

Pontos que costumam enganar:

- Um mesmo tipo pode estar em **várias** camadas (todas que casarem e não excluírem).
- `baseType`/`implements`/`attribute` resolvem o nome por `GetTypeByMetadataName` na compilação. Nome
  não totalmente qualificado, aridade genérica faltando (`IRepository`1`) ou pacote não referenciado
  pelo projeto → `ARCH9004` (warning, **uma vez por nome**) e só **aquele critério** é ignorado; os
  demais critérios da camada continuam valendo.
- Os critérios classificam todo tipo nomeado (classe, interface, struct, enum, record, delegate).
- Referenciar em `from`/`to`/`layer` uma camada que **não existe em `layers`** não gera erro nenhum: a
  regra simplesmente nunca dispara. Confira a grafia dos nomes (sensíveis a maiúsculas) depois de
  escrever.

## `rules`

Chaves comuns a todas as regras:

| Chave | Obrigatória | Observações |
| --- | --- | --- |
| `id` | sim | ID de negócio da organização (`ACME-001`). Aparece na mensagem e em `Diagnostic.Properties["archRuleId"]`. Nunca chega ao Roslyn. Chave de merge no `extends`. |
| `slot` | sim | `ARCH0001`..`ARCH0512`, **único** no projeto. Trim e caixa são normalizados (`" arch0007 "` ≡ `ARCH0007`). Fora do pool ou repetido → `ARCH9002`. |
| `type` | sim | um dos 4 tipos abaixo (trim e caixa ignorados). Desconhecido → `ARCH9003`. |
| `enabled` | não (padrão `true`) | `true`/`false`. Outro valor → `ARCH9001`. Desligada continua no lock file, só marcada. |
| `severity` | não | nome de `severities:`; sem ele vale `defaults.severity`. |
| `message` | não | template; só `forbidden-call` e `must-route-through` o usam (ver abaixo). |
| `help` | não | URL/texto exposto em `Diagnostic.Properties["archHelp"]`. |

**Regra sem `id`, `slot` ou `type` é `ARCH9001` (bloqueante), não `ARCH9008`.** Já campo específico do
tipo faltando é `ARCH9008` (não bloqueante, só aquela regra é descartada).

### Os 4 tipos de regra e seus campos

Fonte: `src/Arch.Config/RuleTypeCatalog.cs`. Os nomes de campo são sensíveis a maiúsculas (`countBy`,
não `countby`). Uma chave que o tipo não declara → `ARCH9008` (e a regra é ignorada).

| `type` | Campo | Obrig. | Forma | Valores aceitos |
| --- | --- | --- | --- | --- |
| `forbidden-call` | `from` | sim | escalar não vazio | nome de camada |
| | `to` | sim | escalar não vazio | nome de camada |
| | `when` | **não** | mapa de **uma** chave → lista: `{ differ: [Module] }` (escalar solto = lista de 1) | chave `same` ou `differ`; valores = nomes de placeholder `[Nome]` capturados pelas camadas `from`/`to` |
| `must-route-through` | `from` | sim | escalar | nome de camada |
| | `to` | sim | escalar | nome de camada |
| | `through` | **não** | lista (`[Service]`) ou escalar solto (lista de 1) | nomes de camada; só alimenta a mensagem |
| | `when` | **não** | igual ao de `forbidden-call` | igual ao de `forbidden-call` |
| `naming-convention` | `layer` | sim | escalar | nome de camada |
| | `require` | sim | mapa de **um** par: `{ suffix: Repository }` | chave `suffix` ou `prefix`; valor = texto não vazio. Forma legada `suffix=Repository` também é aceita. |
| `max-dependencies` | `layer` | sim | escalar | nome de camada |
| | `max` | sim | inteiro `>= 0` | limite **inclusivo** (contagem igual a `max` não reporta) |
| | `countBy` | sim | escalar | `constructor-parameters`, `fields` ou `all` |

#### `forbidden-call`

Nenhum tipo da camada `from` pode **alcançar diretamente** um tipo da camada `to`. "Alcançar" é, exatamente:
invocação de método, criação de objeto (`new`), leitura/escrita de propriedade e de campo. O tipo
classificado como destino é o que **declara** o membro (uma chamada pela interface
`IPedidoRepository` conta como a interface, não como a classe concreta).

Não são detectados: herança, tipos em assinaturas/campos/parâmetros de construtor sem uso, `typeof`,
`using`. Um tipo usando a si mesmo não conta; `nameof(...)` não conta; código gerado não é analisado.
Consequência prática: para "o Controller não pode depender de Repository", a camada `Repository` precisa
cobrir também as **interfaces** que os controllers chamam (ex.: `nameSuffix: Repository`), senão a
chamada é classificada no tipo que declara o membro e passa batida.

`when` (também em `must-route-through`) condiciona a regra às capturas das camadas: `same: [A, B]` só
reporta quando **todos** os nomes existem nos dois lados e são iguais; `differ: [A, B]` só quando todos
existem nos dois lados e **ao menos um** difere. Lado sem a captura → o par não é reportado. Sem `when`,
nada muda. Nome que **nenhum** critério de `match` das camadas `from` e `to` captura → `ARCH9008` (regra
ignorada; checado sobre a config já mesclada com `extends`). Em `Extra` fica `"differ=Module,Tenant"`.

```yaml
layers:
  ModuleServices: { match: [ { namespace: "Acme.[Module].Services.**" } ] }
  ModuleDomain:   { match: [ { namespace: "Acme.[Module].Domain.**" } ] }
rules:
  - { id: ACME-010, slot: ARCH0010, type: forbidden-call, from: ModuleServices, to: ModuleDomain, when: { differ: [Module] } }
```

#### `must-route-through`

Açúcar sobre `forbidden-call`: **mesma detecção** (`from` → `to` direto). `through` só entra na mensagem
(`{through}`); a rota real `from → through → to` **não** é verificada (exigiria o grafo de chamadas).

#### `naming-convention`

Todo tipo nomeado da camada `layer` precisa ter o nome terminando em `suffix` (ou começando com
`prefix`), comparação ordinal e sensível a maiúsculas. Vale para **qualquer** tipo da camada (interface,
enum, record, aninhado), não só classes. Reporta na declaração do tipo.

#### `max-dependencies`

Para cada tipo da camada `layer`, conta **tipos distintos** (dois parâmetros do mesmo tipo = 1;
`IRepo<A>` e `IRepo<B>` = 1) e reporta se a contagem passar de `max`:

- `constructor-parameters`: parâmetros do construtor **público com mais parâmetros**.
- `fields`: campos de instância `private`/`protected` declarados no próprio tipo, ignorando primitivos,
  `string`, `decimal`, `DateTime` e enums.
- `all`: a **união** das duas (o mesmo `IFoo` no parâmetro e no campo conta uma vez).

### Mensagem (`message:`)

Só `forbidden-call` e `must-route-through`. Placeholders: `{from}`, `{to}`, `{symbol}`, `{id}` e qualquer
campo da própria regra em texto (`{through}` vira `Service,Outro`, sem espaço). Placeholder desconhecido
fica literal (nunca é erro). O motor sempre prefixa o ID de negócio: a mensagem final é
`ACME-001: <template expandido>`. Padrões:

- `forbidden-call`: `{from} não pode acessar {to} diretamente ({symbol}).`
- `must-route-through`: `{from} não pode alcançar {to} diretamente ({symbol}). Passe por {through}.`

`naming-convention` e `max-dependencies` emitem mensagem fixa (`ACME-003: tipo 'X' viola a convenção de
nomenclatura (suffix=Repository)`; `ACME-004: 'X' tem 5 dependência(s), máximo permitido é 4`) e
**ignoram** `message:`.

## `extends`

```yaml
schema: arch-rules/v1
extends:
  - arch-rules.base.yaml
```

- O arquivo que faz `extends` **vence**. `layers` e `severities` mesclam por nome; `rules` mescla por `id`
  (a regra do arquivo de maior precedência substitui a da base, na mesma posição); `defaults.severity`
  do mais específico vence.
- Os caminhos são relativos ao arquivo que os declara.
- O analyzer não lê disco: só enxerga `@(AdditionalFiles)`. O pacote inclui automaticamente
  `arch-rules.yaml` e `arch-rules.*.yaml` **ao lado do `.csproj`** (menos `arch-rules.lock.yaml`). Um
  arquivo base com outro nome ou em outra pasta precisa de `<AdditionalFiles Include="..." />` manual no
  `.csproj`, senão `ARCH9006` (warning: a camada ausente é ignorada).
- Ciclo ou profundidade acima do limite (32 níveis / 64 arquivos) → `ARCH9007` (warning, o ramo é cortado).
- No CLI, qualquer problema de `extends` (ausente, ciclo, profundidade) é **bloqueante** (`CLI-EXTENDS`).

## Exemplo completo: arquitetura em camadas

Este é o YAML usado na validação da skill, com o projeto `Acme.Shop` (namespaces
`Acme.Shop.Api.Controllers`, `Acme.Shop.Application`, `Acme.Shop.Infrastructure.Repositories` e
`Acme.Shop.Domain`). **É um exemplo de forma; a política de verdade é a que o usuário disser.**

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/SamuelGFDias/rulekit/main/docs/schema/arch-rules.v1.json
schema: arch-rules/v1

layers:
  # Entra por namespace OU por sufixo (OR). O sufixo pega controller "fora do lugar".
  Controller:
    match:
      - namespace: Acme.Shop.Api.Controllers.**
      - nameSuffix: Controller

  # Toda a camada de aplicação, menos os DTOs (exclude vence o match).
  Service:
    match:
      - namespace: Acme.Shop.Application.**
    exclude:
      - namespace: Acme.Shop.Application.Dtos.**

  # Por namespace OU por implementar a interface genérica (aridade com crase: IRepository`1).
  Repository:
    match:
      - namespace: Acme.Shop.Infrastructure.Repositories.**
      - implements: Acme.Shop.Domain.IRepository`1

  Domain:
    match:
      - namespace: Acme.Shop.Domain.**

# Nomes livres da organização -> valor do AnalyzerConfig.
severities:
  bloqueante: error
  aviso: warning
  dica: suggestion

defaults:
  severity: aviso

rules:
  # Controller não alcança Repository direto (new, método, propriedade, campo).
  - id: ACME-001
    slot: ARCH0001
    type: must-route-through
    from: Controller
    to: Repository
    through: [Service]
    severity: bloqueante
    message: "{from} não pode acessar {to} diretamente ({symbol}). Passe por {through}."
    help: https://wiki.acme.example/arquitetura/ACME-001

  # O domínio não conhece a camada de aplicação.
  - id: ACME-002
    slot: ARCH0002
    type: forbidden-call
    from: Domain
    to: Service

  # Todo tipo da camada Repository termina em "Repository". Sem `severity`: vale o default (aviso).
  - id: ACME-003
    slot: ARCH0003
    type: naming-convention
    layer: Repository
    require: { suffix: Repository }

  # No máximo 4 dependências distintas no maior construtor público.
  - id: ACME-004
    slot: ARCH0004
    type: max-dependencies
    layer: Service
    max: 4
    countBy: constructor-parameters

  # Regra desligada: continua no arquivo e no lock file, mas não gera diagnóstico.
  - id: ACME-005
    slot: ARCH0005
    type: forbidden-call
    from: Service
    to: Controller
    enabled: false
```

Sobre os slots: use números sequenciais e estáveis (`ARCH0001`, `ARCH0002`...) e **nunca reaproveite** o
slot de uma regra que saiu: `#pragma warning disable ARCH0007` e entradas de `.editorconfig` já
existentes passariam a valer para a regra nova (isto é uma recomendação de manutenção, a lib não
impede).
