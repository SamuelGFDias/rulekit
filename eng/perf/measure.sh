#!/usr/bin/env bash
# Mede o overhead do RuleKit.Analyzer no build da solução sintética de referência (issue #10,
# ADR-001 "Não-funcionais/Performance": orçamento < 5%). Metodologia em eng/perf/README.md.
#
# Uso:
#   eng/perf/measure.sh --feed <dir-com-o-nupkg> --version <versão-do-RuleKit.Analyzer> \
#       [--runs 5] [--threshold 5] [--work <dir>] [--modules 10] [--per-module 10]
#
# Métrica principal (gate, definição do ADR: overhead no tempo de build): por execução, um par de
# builds com e sem o analyzer, em ordem alternada; overhead = (parede com - parede sem) / parede sem;
# gate = mediana dos pares. Informativo: tempo de CPU do RuleKit reportado pelo csc
# (-p:ReportAnalyzer=true) dividido pela parede do build — é um teto pessimista, porque o analyzer
# roda em paralelo à compilação e esse tempo não se soma 1:1 à parede. Sai com 1 se a mediana passar do limite
# ou se alguma asserção de sanidade falhar (analyzer não rodou, AD0001, sem diagnósticos).
set -euo pipefail
export LC_ALL=C DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

FEED="" VERSION="" RUNS=5 THRESHOLD=5 WORK="" MODULES=10 PER_MODULE=10
while [[ $# -gt 0 ]]; do
  case "$1" in
    --feed) FEED="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --runs) RUNS="$2"; shift 2 ;;
    --threshold) THRESHOLD="$2"; shift 2 ;;
    --work) WORK="$2"; shift 2 ;;
    --modules) MODULES="$2"; shift 2 ;;
    --per-module) PER_MODULE="$2"; shift 2 ;;
    *) echo "measure.sh: argumento desconhecido '$1'" >&2; exit 2 ;;
  esac
done
[[ -n "$FEED" && -n "$VERSION" ]] || { echo "measure.sh: --feed e --version são obrigatórios" >&2; exit 2; }
FEED="$(cd "$FEED" && pwd)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[[ -n "$WORK" ]] || WORK="$(mktemp -d)"
mkdir -p "$WORK"; WORK="$(cd "$WORK" && pwd)"
SOL="$WORK/sol"
rm -rf "$SOL"

"$HERE/generate.sh" "$SOL" "$MODULES" "$PER_MODULE"
cd "$SOL"

COMMON=(-c Release --no-restore --no-incremental -v:d -nodeReuse:false
        -p:UseSharedCompilation=false -p:ReportAnalyzer=true "-p:RuleKitAnalyzerVersion=$VERSION")

echo "== restore (feed local: $FEED)"
dotnet restore "-p:RuleKitAnalyzerVersion=$VERSION" "-p:RestoreSources=$FEED;https://api.nuget.org/v3/index.json" >/dev/null

now() { date +%s.%N; }

# build_once <on|off> <logfile>  -> imprime o tempo de parede em segundos
build_once() {
  local mode=$1 log=$2 extra=() t0 t1 rc=0
  [[ "$mode" == off ]] && extra=(-p:PerfRuleKit=off)
  t0=$(now)
  dotnet build "${COMMON[@]}" "${extra[@]}" >"$log" 2>&1 || rc=$?
  t1=$(now)
  if (( rc != 0 )); then
    echo "measure.sh: build ($mode) falhou (exit $rc); últimas linhas:" >&2
    grep -E "error |Build FAILED" "$log" | sort -u | head -20 >&2 || true
    exit 1
  fi
  awk -v a="$t0" -v b="$t1" 'BEGIN { printf "%.3f", b - a }'
}

# Tempo (s) do RuleKit na tabela do ReportAnalyzer; "<0.001" vira 0.
rulekit_seconds() {
  awk '/ Arch\.Analyzer\.ArchRulesAnalyzer \(/ { gsub(/</, "", $1); print $1; found=1; exit } END { if (!found) exit 3 }' "$1"
}

median() { sort -g | awk '{ v[NR] = $1 } END { if (NR % 2) print v[(NR + 1) / 2]; else printf "%.4f\n", (v[NR / 2] + v[NR / 2 + 1]) / 2 }'; }

echo "== warm-up (não medido): 1 build com e 1 sem o analyzer"
build_once on "$WORK/warmup-on.log" >/dev/null
build_once off "$WORK/warmup-off.log" >/dev/null

ratios=() shares=() ons=() offs=() analyzers=()
for (( r = 1; r <= RUNS; r++ )); do
  # Ordem alternada para não favorecer sistematicamente um dos lados (cache de disco, turbo).
  if (( r % 2 )); then
    w_on=$(build_once on "$WORK/run$r-on.log"); w_off=$(build_once off "$WORK/run$r-off.log")
  else
    w_off=$(build_once off "$WORK/run$r-off.log"); w_on=$(build_once on "$WORK/run$r-on.log")
  fi
  a=$(rulekit_seconds "$WORK/run$r-on.log") || { echo "measure.sh: linha do ArchRulesAnalyzer ausente no relatório do csc (run $r) — o analyzer não rodou" >&2; exit 1; }
  ratio=$(awk -v a="$w_on" -v b="$w_off" 'BEGIN { printf "%.4f", 100 * (a - b) / b }')
  share=$(awk -v a="$a" -v w="$w_on" 'BEGIN { printf "%.4f", 100 * a / w }')
  printf 'run %d: build(com)=%ss  build(sem)=%ss  overhead=%s%%  (CPU do analyzer=%ss, %s%% da parede)\n' "$r" "$w_on" "$w_off" "$ratio" "$a" "$share"
  ratios+=("$ratio"); shares+=("$share"); ons+=("$w_on"); offs+=("$w_off"); analyzers+=("$a")
done

med_ratio=$(printf '%s\n' "${ratios[@]}" | median)
med_share=$(printf '%s\n' "${shares[@]}" | median)
med_on=$(printf '%s\n' "${ons[@]}" | median)
med_off=$(printf '%s\n' "${offs[@]}" | median)
med_an=$(printf '%s\n' "${analyzers[@]}" | median)

# Sanidade: o analyzer realmente avaliou a política (diagnósticos ARCH0 presentes, 4 regras
# distintas) e nunca falhou (AD0001).
diag_total=$(grep -E "warning ARCH0[0-9]+" "$WORK/run1-on.log" | sed -E 's/.*warning (ARCH0[0-9]+): ([^[]*).*/\1: \2/' | sort -u | wc -l)
diag_slots=$(grep -E "warning ARCH0[0-9]+" "$WORK/run1-on.log" | sed -E 's/.*warning (ARCH0[0-9]+).*/\1/' | sort -u | tr '\n' ' ')
ad0001=$(grep -cE "(warning|error) AD0001" "$WORK/run1-on.log" || true)
types=$(find "$SOL" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | wc -l)
lines=$(find "$SOL" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print0 | xargs -0 cat | wc -l)
slots_n=$(echo "$diag_slots" | wc -w)

verdict=PASS
reason=""
if awk -v m="$med_ratio" -v t="$THRESHOLD" 'BEGIN { exit !(m > t) }'; then verdict=FAIL; reason="overhead mediano ${med_ratio}% > limite ${THRESHOLD}%"; fi
if (( diag_total == 0 )); then verdict=FAIL; reason="${reason:+$reason; }nenhum diagnóstico ARCH0 apareceu (o analyzer não avaliou a política)"; fi
if (( slots_n < 4 )); then verdict=FAIL; reason="${reason:+$reason; }só ${slots_n}/4 regras (ARCH0001..4) produziram diagnóstico: $diag_slots"; fi
if (( ad0001 > 0 )); then verdict=FAIL; reason="${reason:+$reason; }AD0001 presente (falha do analyzer)"; fi

summary=$(cat <<EOF
### RuleKit — orçamento de performance (< ${THRESHOLD}%): **${verdict}**

| Métrica | Valor |
|---|---|
| **Overhead mediano (gate)** = mediana de (parede com - parede sem) / parede sem, por par | **${med_ratio}%** (limite ${THRESHOLD}%) |
| Tempo mediano de build com o analyzer (parede) | ${med_on} s |
| Tempo mediano de build sem o RuleKit (parede) | ${med_off} s |
| Tempo mediano de CPU do RuleKit reportado pelo csc (informativo) | ${med_an} s |
| CPU do RuleKit / parede do build (informativo, teto pessimista) | ${med_share}% |
| Execuções medidas / warm-up | ${RUNS} / 1 |
| Solução sintética | ${types} arquivos .cs, ${lines} linhas, ${MODULES}x${PER_MODULE}x5 tipos |
| Diagnósticos ARCH0 distintos / slots / AD0001 | ${diag_total} / ${diag_slots}/ ${ad0001} |
| RuleKit.Analyzer | ${VERSION} |

Por execução (com s / sem s): $(for (( i = 0; i < RUNS; i++ )); do printf '%s/%s ' "${ons[$i]}" "${offs[$i]}"; done)
EOF
)
echo
echo "$summary"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then echo "$summary" >> "$GITHUB_STEP_SUMMARY"; fi

dotnet build-server shutdown >/dev/null 2>&1 || true

if [[ "$verdict" == FAIL ]]; then
  echo "measure.sh: FALHOU — $reason" >&2
  exit 1
fi
echo "measure.sh: OK — overhead mediano ${med_ratio}% <= ${THRESHOLD}%"
