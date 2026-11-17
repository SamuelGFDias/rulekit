# `arch-rules` (RuleKit.Cli) e CI

`RuleKit.Cli` é um dotnet tool (`net10.0`, com roll-forward para runtimes mais novos); o comando é
`arch-rules`. Exige o runtime .NET 10+ na máquina. Conferido em `src/Arch.Cli/`.

## Instalar

```bash
# local ao repositório (recomendado: a versão fica no manifesto e o CI usa a mesma)
dotnet new tool-manifest            # só se ainda não existir .config/dotnet-tools.json ou dotnet-tools.json
dotnet tool install RuleKit.Cli     # sem --version
dotnet arch-rules --help            # ou: dotnet tool run arch-rules

# global
dotnet tool install -g RuleKit.Cli
arch-rules --help
```

Depois de clonar um repositório que já tem o manifesto: `dotnet tool restore`.

Se o pacote ainda não estiver no nuget.org (a URL `.../rulekit.cli/index.json` devolve 404), use um feed
local com `--add-source <dir-dos-nupkg>`.

## Comandos

```text
arch-rules gen <caminho-do-yaml> [--out <diretorio>]
arch-rules validate <caminho-do-yaml>
arch-rules -h | --help
```

Não há outras opções. Parsing feito à mão: um argumento posicional e, no `gen`, só `--out`.

### `validate`

Mesmo parsing e mesmas validações do `gen` (cadeia de `extends` lida do disco, schema, slots únicos no
pool, severidades), **sem gerar arquivo**.

Saída (uma linha por problema):

```text
AVISO [ARCH9008] linha 9, coluna 5: Regra 'T-1' (type 'forbidden-call') ignorada: campo obrigatório ausente: 'to'.
ERRO [ARCH9002] linha 0, coluna 0: Slot 'ARCH0001' duplicado: declarado tanto em 'T-1' quanto em 'T-2'.
OK: configuração válida.
```

| Resultado | Código de saída |
| --- | --- |
| `OK: configuração válida.` | **0** |
| Há linhas `ERRO` (schema `ARCH9001`, slot `ARCH9002`, `CLI-SEVERITY`, `CLI-EXTENDS`) | **1** |
| Arquivo não encontrado / uso errado | **1** |

**Atenção, armadilha para CI:** `ARCH9003` (tipo desconhecido), `ARCH9008` (campo ausente/inválido) e a
divergência do lock saem como `AVISO` e o código de saída continua **0**, mesmo com a regra sendo
ignorada. Para o CI falhar nesses casos, procure `^AVISO` na saída (exemplo abaixo).

Se existir um `arch-rules.lock.yaml` ao lado do YAML, `validate` compara com o que `gen` geraria agora e
imprime `AVISO [ARCH9005] ... 'arch-rules.lock.yaml' existente diverge ...` (sem falhar). Ele compara só
o **lock**, não o `.globalconfig`.

### `gen`

Faz a mesma validação e, **só se não houver `ERRO`**, escreve dois arquivos no diretório do YAML (ou em
`--out`, que é criado se não existir):

```text
Gerado: /caminho/.globalconfig
Gerado: /caminho/arch-rules.lock.yaml
```

Com qualquer erro bloqueante, **nenhum arquivo é gerado** (código de saída 1). A saída é determinística
(ordenada por slot), então o diff em PR é legível e regenerar sem mudar o YAML não altera nada.

**`.globalconfig`**: `is_global = true` e uma linha `dotnet_diagnostic.<slot>.severity = <valor>` por
regra **habilitada**, ordenada por slot. Regras `enabled: false` não entram. Regras descartadas por
`ARCH9003`/`ARCH9008` também não.

```ini
is_global = true

dotnet_diagnostic.ARCH0001.severity = error
dotnet_diagnostic.ARCH0002.severity = warning
dotnet_diagnostic.ARCH0003.severity = warning
dotnet_diagnostic.ARCH0004.severity = warning
```

Gerado **ao lado do `.csproj`** (o padrão, quando o YAML mora lá), o `.globalconfig` é aplicado pelo
compilador sem mexer no `.csproj` (verificado: uma regra `error` quebrou o build). Se usar `--out` para
outro diretório, garanta por conta própria que o arquivo chegue ao projeto.

**`arch-rules.lock.yaml`**: retrato `id → slot → severidade efetiva → enabled` de **todas** as regras
(inclusive as desligadas), ordenado por slot:

```yaml
schema: arch-rules-lock/v1
rules:
  - id: ACME-001
    slot: ARCH0001
    type: must-route-through
    severity: error
    enabled: true
  - id: ACME-005
    slot: ARCH0005
    type: forbidden-call
    severity: warning
    enabled: false
```

Versione os **dois** arquivos. O lock **não** é uma camada de `extends` (o pacote o exclui de
`@(AdditionalFiles)`).

## Por que a severidade vive no `.globalconfig`

O compilador lê a severidade de `dotnet_diagnostic.<ID>.severity`, não do YAML (ADR-001 D3). Editar a
`severity` no YAML **sem** rodar `arch-rules gen` deixa o build com a severidade antiga e o analyzer
emite `ARCH9005` (info) apontando a regra. Regra prática: mudou o YAML → `arch-rules gen` → commitar YAML,
`.globalconfig` e lock juntos.

## CI: exemplo (GitHub Actions)

Passos verificados localmente com um repositório git de teste (em sincronia: sai 0; YAML alterado sem
`gen`: falha; regra com chave inválida: falha mesmo com `validate` saindo 0).

```yaml
name: arquitetura
on: [push, pull_request]

jobs:
  arch-rules:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x

      - name: Validar e conferir que os arquivos gerados estão em sincronia
        shell: bash
        run: |
          set -euo pipefail
          dotnet tool restore
          dotnet arch-rules validate src/Acme.Shop/arch-rules.yaml | tee validate.log
          if grep -q '^AVISO' validate.log; then
            echo "::error::arch-rules validate emitiu AVISO (regra ignorada ou lock desatualizado)"; exit 1
          fi
          dotnet arch-rules gen src/Acme.Shop/arch-rules.yaml
          if [ -n "$(git status --porcelain -- src/Acme.Shop/.globalconfig src/Acme.Shop/arch-rules.lock.yaml)" ]; then
            echo "::error::.globalconfig / arch-rules.lock.yaml fora de sincronia; rode 'arch-rules gen' e versione"; exit 1
          fi

      - name: Build (as regras com severidade error quebram aqui)
        run: dotnet build src/Acme.Shop -c Release
```

Notas:

- O `git status --porcelain` (e não `git diff`) também pega arquivo gerado **ainda não versionado**.
- Regras com severidade `error` já quebram o `dotnet build`. Para um `warning` quebrar também, use
  `-warnaserror` (todos) ou `-warnaserror:ARCH0004` (só aquele slot).
- Em solução com vários projetos, cada projeto com `arch-rules.yaml` próprio roda `validate`/`gen`
  separadamente (o analyzer procura um `arch-rules.yaml` entre os `AdditionalFiles` **do projeto**).
- Se o feed for local ou privado, passe `--add-source` ao `dotnet tool install`/`dotnet add package`, ou
  use um `NuGet.Config` ao lado da solução (foi o que a validação desta skill usou).
