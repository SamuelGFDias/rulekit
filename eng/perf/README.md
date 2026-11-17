# Benchmark de performance do RuleKit (solução de referência)

Fecha o **Action Item #4 do ADR-001** ("definir a solução de referência para o benchmark de
performance") e a issue #10. O requisito não-funcional a provar, nas palavras do ADR:

> Orçamento: **< 5% de overhead** no tempo de build de uma solução de referência. Medido em CI, não estimado.

Esta pasta **é** a solução de referência: um gerador determinístico de código sintético
(`generate.sh`) e o script de medição que o CI executa (`measure.sh`, job `perf` de
`.github/workflows/ci.yml`).

## A solução de referência

`generate.sh <dir> [módulos=10] [tipos-por-camada-por-módulo=10]` escreve um projeto único
(`Perf.Synthetic.csproj`, `net10.0`) com o `arch-rules.yaml` ao lado. Com os defaults:

| Item | Valor |
|---|---|
| Tipos | 500 (10 módulos × 10 tipos × 5 camadas) |
| Arquivos / linhas | 500 arquivos `.cs`, ~17 mil linhas |
| Camadas com regra | `Controller`, `Service`, `Repository`, `Persistence` (+ `Domain`, sem regra) |
| Regras | as 4 do ADR-001 D2: `forbidden-call`, `must-route-through`, `naming-convention`, `max-dependencies` |
| Violações | poucas e fixas, só em módulos pares: 25 diagnósticos distintos, as 4 regras disparam |

Cada tipo tem corpo real (laços, LINQ, chamadas entre camadas, construtores com dependências), para que
o analyzer tenha o que visitar. A saída é byte a byte reprodutível. O projeto é gerado **fora do
repositório** (o `Directory.Build.props`/CPM/MinVer da raiz vazariam para ele) e consome o
`RuleKit.Analyzer` por **`.nupkg`** (o pacote que o usuário instala, com o YamlDotNet fundido), nunca por
`ProjectReference`.

Por que sintética em vez de um projeto open-source real: é repetível sem dependência externa nem
rede, o tamanho é um parâmetro, e as violações são conhecidas (permite afirmar que o analyzer de fato
avaliou a política). A desvantagem é o código ser mais regular que o de um produto real; o tamanho
(centenas de tipos, camadas e namespaces aninhados) é o que importa para o custo do analyzer, que cresce
com símbolos e invocações visitados.

## Metodologia

`measure.sh --feed <dir-com-o-nupkg> --version <versão> [--runs 7] [--threshold 5]`:

1. gera a solução, faz **1 restore** com o feed local e **1 warm-up** (um build com e um sem o RuleKit,
   não medidos: JIT, caches do disco, resolução de SDK);
2. repete **N** vezes (CI: 7), em pares: build **com** o RuleKit e build **sem** ele, ambos
   `dotnet build -c Release --no-restore --no-incremental -p:UseSharedCompilation=false -p:ReportAnalyzer=true`.
   `--no-incremental` força recompilar; `UseSharedCompilation=false` evita que um servidor de compilação
   quente esconda o custo de iniciar o analyzer (o pior caso, que é o de um build frio no CI);
3. **métrica principal (gate):** por execução, `tempo do RuleKit no csc / tempo de parede do build com o
   RuleKit`, em %, e a **mediana** das N execuções. O tempo do RuleKit é a linha
   `Arch.Analyzer.ArchRulesAnalyzer` da tabela que o csc imprime com `ReportAnalyzer=true` (visível com
   `-v:d`). Falha (exit 1) se a mediana passar de 5%;
4. **informativo:** tempo de parede mediano com e sem o RuleKit e a diferença percentual. Não é gate
   porque o ruído do runner (±1 s em ~6 s) é do tamanho do efeito: no ambiente de referência a diferença
   fica ora positiva, ora negativa;
5. **sanidade (também falha o job):** a linha do analyzer existe no relatório do csc (ele rodou), há
   diagnósticos `ARCH0` das 4 regras (avaliou a política) e nenhum `AD0001` (não falhou). Sem isso um
   overhead baixo poderia ser só um analyzer que não fez nada.

O resultado vai para `$GITHUB_STEP_SUMMARY` (tabela) e os logs de build ficam no artefato `perf-logs`.

### Por que "tempo do analyzer / tempo de build" e não só "build com − build sem"

A diferença de parede entre dois builds de ~6 s tem ruído de mesma ordem que 5% (300 ms), então
compará-los seria um gate instável. O csc mede o tempo do analyzer **por dentro**, sem o ruído de
inicialização do `dotnet`/MSBuild/restore, e a razão contra o build inteiro responde diretamente "quanto
do build é o RuleKit". É conservadora em dois sentidos: o `ReportAnalyzer` instrumenta o analyzer e
encarece sua execução, e o tempo reportado é soma de CPU, que pode passar do tempo de parede quando
analyzers rodam em paralelo.

Para a baseline "sem o RuleKit", o projeto remove **só** o `RuleKit.Analyzer` da lista de analyzers do
csc (`-p:PerfRuleKit=off`), mantendo os analyzers do SDK ligados nas duas medições. `RunAnalyzers=false`
removeria todos e atribuiria à lib o custo dos analyzers do SDK.

## Resultado de referência

Máquina de desenvolvimento (ARM64, Linux, .NET SDK 10.0.404), `RuleKit.Analyzer 0.0.0-alpha.0.6`, 7 execuções:

| Métrica | Valor |
|---|---|
| Overhead mediano (gate) | **3,10%** (limite 5%) |
| Tempo mediano do RuleKit no csc | 0,183 s (faixa 0,14–0,28 s) |
| Build mediano com / sem o RuleKit (parede) | 5,92 s / 5,93 s (diferença −0,2%, ruído) |
| Diagnósticos | 25 distintos, `ARCH0001`–`ARCH0004`, 0 `AD0001` |

Os números do CI vão para o summary de cada execução e variam com o runner. A margem para o limite
(≈2 pontos percentuais) não é grande: se o job do CI ficar instável, o remédio correto é aumentar `--runs`
ou o tamanho da solução (`--modules`), não subir o limite sem decisão explícita.

## Rodar localmente

```bash
dotnet pack src/Arch.Analyzer -c Release -o artifacts          # gera artifacts/RuleKit.Analyzer.<versão>.nupkg
eng/perf/measure.sh --feed artifacts --version <versão> --runs 7 --work /tmp/rulekit-perf
```

Para inspecionar a solução sem medir: `eng/perf/generate.sh /tmp/rulekit-perf-sol`.
