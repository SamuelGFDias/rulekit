# Diagnósticos do RuleKit: `ARCH0xxx` e `ARCH9xxx`

O analyzer reporta duas famílias de IDs. Conferido contra `src/Arch.Analyzer/ArchDiagnostics.cs`,
`src/Arch.Config/Internal/ArchConfigDocumentParser.cs` e rodando `dotnet build` de verdade.

## `ARCH0001`..`ARCH0512`: as violações das regras do usuário

São os **slots** do pool. Cada regra do YAML aponta para um (`slot: ARCH0007`); quando a regra é violada,
o diagnóstico sai com **esse** ID. Um slot sem regra vinculada nunca reporta nada.

O que o usuário vê no `dotnet build` (saída real do projeto de validação):

```
Api/Controllers/Controllers.cs(9,20): error ARCH0001: ACME-001: Controller não pode acessar Repository diretamente (new PedidoRepository). Passe por Service.
Application/Services.cs(10,14): warning ARCH0004: ACME-004: 'PedidoService' tem 5 dependência(s), máximo permitido é 4
```

- A mensagem sempre começa com o **ID de negócio** (`ACME-001:`); o `ARCH0001` é o ID técnico.
- `Diagnostic.Properties` (para ferramentas e CI): `archRuleId`, `archRuleType`, `archFrom`, `archTo`,
  `archHelp` (os de `from`/`to`/`help` só nas regras `forbidden-call`/`must-route-through`).
- A severidade **padrão** de todo slot é `warning`. A **efetiva** vem do `.globalconfig` gerado por
  `arch-rules gen` (ou de um `.editorconfig`). O descriptor nunca decide política.

### Como silenciar ou mudar uma violação (funciona por slot)

```csharp
#pragma warning disable ARCH0001   // pontual, no arquivo/trecho
```

```ini
# .editorconfig (por arquivo/pasta)
[*.cs]
dotnet_diagnostic.ARCH0001.severity = none
```

Os dois foram verificados: o diagnóstico some. Para o efeito valer em **todo** o projeto e ficar
rastreável, prefira mudar a `severity` da regra no YAML e rodar `arch-rules gen` (ou `enabled: false`).
Um `.editorconfig` que muda a severidade de um slot gera **divergência com o YAML** (ver `ARCH9005`
abaixo, que só enxerga o `.globalconfig`, não o `.editorconfig` por arquivo).

## `ARCH9001`..`ARCH9008`: erros do próprio motor

Faixa reservada: sempre habilitada por padrão e **nunca** alocável como slot. Não tem a tag
`NotConfigurable`, então `#pragma`/`.editorconfig` funcionam nela também (dá para silenciar um aviso do
motor pontualmente).

| ID | Severidade padrão | Bloqueante? | Causa | Correção |
| --- | --- | --- | --- | --- |
| `ARCH9001` | **Error** | **Sim**: o arquivo inteiro é invalidado e **nenhuma regra roda** | YAML malformado; `schema` ausente ou de outra major; chave desconhecida na raiz, em camada, em critério ou em `defaults`; anchor/alias/multi-documento/tag; camada sem `match`; critério com zero ou 2+ chaves; placeholder `[Nome]` malformado ou repetido em `namespace`/`assembly`; `enabled` não booleano; regra sem `id`, `slot` ou `type` | Ler a mensagem (traz linha e coluna do YAML). Corrigir a chave/estrutura. Rodar `arch-rules validate`. |
| `ARCH9002` | Warning | Não: a regra é ignorada, as demais valem | `slot` fora de `ARCH0001`..`ARCH0512` (inclui `ARCH9xxx` e `ARCH0513`) ou **repetido** entre regras | Dar a cada regra um slot único do pool. Dois IDs de negócio no mesmo slot impediriam o override de severidade de um sem afetar o outro. (O **CLI** trata isso como erro e se recusa a gerar arquivos.) |
| `ARCH9003` | Warning | Não: a regra é ignorada | `type` não existe nesta versão da lib (`forbidden-call`, `must-route-through`, `naming-convention`, `max-dependencies` são os 4 de v1) | Corrigir a grafia do tipo ou atualizar o pacote, se o tipo for de uma versão mais nova. **Não há extensão de terceiros na v1.** |
| `ARCH9004` | Warning | Não: só o **critério** é ignorado | Nome em `implements`/`baseType`/`attribute` não foi resolvido por `GetTypeByMetadataName` na compilação. Reportado uma vez por nome. | Usar o nome totalmente qualificado; genérico com aridade (`Ns.IRepository`1`); atributo com sufixo `Attribute`; conferir que o projeto referencia o pacote que define o tipo. |
| `ARCH9005` | **Info** | Não | A severidade declarada no YAML difere da efetiva do slot na compilação: o `.globalconfig` está ausente ou desatualizado. Um por regra, apontando a regra no YAML. | Rodar `arch-rules gen` e versionar o `.globalconfig`. Só compara com `/warnaserror`, `/nowarn`, ruleset e `.globalconfig`; **não** enxerga `.editorconfig` por árvore. |
| `ARCH9006` | Warning | Não: a camada ausente é ignorada | Um caminho em `extends:` não está em `@(AdditionalFiles)` | Renomear para `arch-rules.<algo>.yaml` ao lado do `.csproj`, ou adicionar `<AdditionalFiles Include="caminho/base.yaml" />` no `.csproj`. |
| `ARCH9007` | Warning | Não: o ramo é cortado | Ciclo em `extends` (A estende B que estende A) ou profundidade acima do limite (32 níveis / 64 arquivos) | Quebrar o ciclo / achatar a cadeia. |
| `ARCH9008` | Warning | Não: a regra é ignorada, **de forma ruidosa** | Regra de `type` conhecido com campo obrigatório ausente, **chave que o tipo não aceita** ou valor inválido (`max` não inteiro/negativo, `countBy` ou `require` fora dos valores aceitos, `when` com forma inválida), ou `when` que cita um nome que nenhum critério de `match` das camadas `from`/`to` captura | Ler a mensagem: ela lista todos os problemas da regra. Ver a tabela de campos em `yaml-referencia.md`. |

### Detalhes que importam

- **`ARCH9005` não aparece no console do `dotnet build`** (é `Info`): aparece na IDE e no log SARIF
  (`dotnet build -p:ErrorLog=out.sarif`). Verificado. Para CI, a checagem confiável é
  `arch-rules validate` (que compara com o `arch-rules.lock.yaml`) e um `arch-rules gen` seguido de
  `git status`/`git diff` (ver `cli-e-ci.md`).
- **`ARCH9001` vem em dois sabores**: o do analyzer (no build, ancorado em `arch-rules.yaml(linha,coluna)`)
  e o do CLI. O CLI também imprime `ERRO [ARCH9001] linha 0, coluna 0: Falha inesperada...` se algo
  inesperado estourar; trate como erro de configuração/ambiente.
- Não existe diagnóstico para **nome de camada inexistente** em `from`/`to`/`layer`: a regra só nunca
  dispara. Se uma regra "não funciona", confira os nomes primeiro.
- No **build**, `ARCH9002`/`ARCH9003`/`ARCH9008` são avisos e o build continua. No **CLI**, `ARCH9002`
  (slot) é erro em `validate` e `gen`; `ARCH9008` é erro (código 1) em `validate` e aviso em `gen`;
  `ARCH9003` é aviso (código 0). Com `--strict`, qualquer aviso falha — é o modo recomendado no CI.
- Para silenciar um aviso do motor de propósito: `dotnet_diagnostic.ARCH9006.severity = none`
  (ou `#pragma`), por exemplo.

## Códigos que só existem no CLI

Não são `ARCH9xxx`:

| Código | Quando |
| --- | --- |
| `CLI-SEVERITY` | regra sem severidade resolvível (sem `severity` e sem `defaults.severity`), nome não declarado em `severities:`, ou valor fora de `error`/`warning`/`suggestion`/`silent`/`none` (`info`/`hidden` são recusados). Vale para **toda** regra, inclusive `enabled: false`. |
| `CLI-EXTENDS` | `extends` ausente no disco, ciclo, ou cadeia longa demais. Sempre bloqueante no CLI. |
