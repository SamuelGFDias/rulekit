#!/usr/bin/env bash
# Gera a "solução de referência" sintética do benchmark de performance do RuleKit.
#
# Uso: eng/perf/generate.sh <dir-de-saida> [modulos=10] [tipos-por-camada-por-modulo=10]
#
# Com os defaults: 10 módulos x 10 tipos x 5 camadas = 500 tipos em ~5 camadas arquiteturais
# (Controller, Service, Repository, Persistence + Domain sem regra), ~10 mil linhas, num único
# projeto (uma Compilation, que é a unidade em que o analyzer roda). A saída é DETERMINÍSTICA:
# mesmas entradas, mesmos arquivos byte a byte, para que duas execuções meçam a mesma coisa.
#
# O diretório de saída deve ficar FORA do repositório (o Directory.Build.props/Directory.Packages.props
# da raiz do repo, com MinVer e Central Package Management, vazariam para o projeto gerado).
#
# Não gera NuGet.Config: quem builda aponta o feed local com -p:RestoreSources=... (ver measure.sh).
set -euo pipefail

OUT="${1:?uso: generate.sh <dir-de-saida> [modulos] [tipos-por-camada-por-modulo]}"
MODULES="${2:-10}"
PER_MODULE="${3:-10}"

if [[ -e "$OUT" && -n "$(ls -A "$OUT" 2>/dev/null)" ]]; then
  echo "generate.sh: '$OUT' já existe e não está vazio; escolha um diretório novo." >&2
  exit 1
fi
mkdir -p "$OUT"

ROOT_NS="Contoso.Perf"

# ----------------------------------------------------------------------------------------------
# Projeto + política
# ----------------------------------------------------------------------------------------------
cat > "$OUT/Perf.Synthetic.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <!-- AD0001 = falha do próprio analyzer (id fora de SupportedDiagnostics, exceção, ...). -->
    <WarningsAsErrors>AD0001</WarningsAsErrors>
    <NoWarn>$(NoWarn);CS0169;CS0414;CS0649;CS0219;CS1591;CS8981</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <!-- Versão exata do .nupkg recém-empacotado: -p:RuleKitAnalyzerVersion=<versão>. -->
    <PackageReference Include="RuleKit.Analyzer" Version="$(RuleKitAnalyzerVersion)" PrivateAssets="all" />
  </ItemGroup>

  <!-- Baseline "sem RuleKit": remove SÓ o RuleKit.Analyzer da lista de analyzers do csc, mantendo
       os analyzers do SDK ligados nas duas medições (mesma técnica de
       tests/Arch.Dogfood/Arch.Dogfood.Sample). RunAnalyzers=false desligaria todos e
       superestimaria o custo atribuído à lib. -->
  <Target Name="PerfDropRuleKit" BeforeTargets="CoreCompile" Condition="'$(PerfRuleKit)' == 'off'">
    <ItemGroup>
      <Analyzer Remove="@(Analyzer)"
                Condition="$([System.String]::Copy('%(Identity)').ToLowerInvariant().Contains('rulekit.analyzer'))" />
    </ItemGroup>
  </Target>

</Project>
XML

cat > "$OUT/arch-rules.yaml" <<'YAML'
# Política sintética do benchmark de performance: as 4 regras tipadas do ADR-001 D2.
schema: arch-rules/v1

layers:
  Controller:
    match:
      - namespace: Contoso.Perf.Api.Controllers.**
  Service:
    match:
      - namespace: Contoso.Perf.Application.Services.**
  Repository:
    match:
      - namespace: Contoso.Perf.Infrastructure.Repositories.**
      - nameSuffix: Repository
  Persistence:
    match:
      - namespace: Contoso.Perf.Infrastructure.Persistence.**

severities:
  aviso: warning

defaults:
  severity: aviso

rules:
  - id: PERF-001
    slot: ARCH0001
    type: forbidden-call
    from: Controller
    to: Repository
    severity: aviso
    message: "{from} não pode acessar {to} diretamente ({symbol})."

  - id: PERF-002
    slot: ARCH0002
    type: must-route-through
    from: Service
    to: Persistence
    through: [Repository]
    severity: aviso

  - id: PERF-003
    slot: ARCH0003
    type: naming-convention
    layer: Repository
    require: { suffix: Repository }
    severity: aviso

  - id: PERF-004
    slot: ARCH0004
    type: max-dependencies
    layer: Service
    max: 3
    countBy: constructor-parameters
    severity: aviso
YAML

# ----------------------------------------------------------------------------------------------
# Código. Cada tipo tem corpo "de verdade" (laços, LINQ, chamadas entre camadas) para que o
# analyzer tenha o que visitar; a proporção de violações é pequena e fixa (ver abaixo).
# ----------------------------------------------------------------------------------------------
# Violações propositais (só em módulos pares), uma por regra:
#   i == 3 -> Controller chama Repository direto            (ARCH0001)
#   i == 5 -> Service chama Persistence direto              (ARCH0002)
#   i == 7 -> tipo em Repositories sem sufixo Repository    (ARCH0003)
#   i == 9 -> Service com 4 parâmetros de construtor        (ARCH0004)

emit_domain() { # $1=g
  local g=$1
  cat > "$OUT/Domain/Entity$g.cs" <<CS
using System;
using System.Collections.Generic;

namespace $ROOT_NS.Domain
{
    public sealed class Entity$g
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public decimal Total { get; set; }
        public List<decimal> Lines { get; } = new List<decimal>();

        public decimal Subtotal()
        {
            decimal sum = 0m;
            foreach (var line in Lines)
            {
                sum += line;
            }
            return sum;
        }

        public bool IsActive(DateTime now) => Id % 2 == 0 && now.Year > 2000;
    }
}
CS
}

emit_persistence() { # $1=m $2=g
  local m=$1 g=$2
  cat > "$OUT/Persistence/Table${g}Context.cs" <<CS
using System.Collections.Generic;
using $ROOT_NS.Domain;

namespace $ROOT_NS.Infrastructure.Persistence.Mod$m
{
    public sealed class Table${g}Context
    {
        private readonly List<Entity$g> _rows = new List<Entity$g>();

        public IReadOnlyList<Entity$g> Rows => _rows;

        public void Add(Entity$g row) => _rows.Add(row);

        public Entity$g Find(int id)
        {
            foreach (var row in _rows)
            {
                if (row.Id == id)
                {
                    return row;
                }
            }
            return null;
        }

        public int SaveChanges() => _rows.Count;
    }
}
CS
}

emit_repository() { # $1=m $2=i $3=g
  local m=$1 i=$2 g=$3 name="Entity$3Repository"
  if (( m % 2 == 0 && i == 7 )); then name="Entity$3Store"; fi   # ARCH0003
  cat > "$OUT/Repositories/$name.cs" <<CS
using System.Collections.Generic;
using System.Linq;
using $ROOT_NS.Domain;
using $ROOT_NS.Infrastructure.Persistence.Mod$m;

namespace $ROOT_NS.Infrastructure.Repositories.Mod$m
{
    public sealed class $name
    {
        private readonly Table${g}Context _db;

        public $name(Table${g}Context db)
        {
            _db = db;
        }

        public Entity$g GetById(int id) => _db.Find(id);

        public IReadOnlyList<Entity$g> ListActive(System.DateTime now)
        {
            return _db.Rows.Where(r => r.IsActive(now)).OrderBy(r => r.Id).ToList();
        }

        public decimal TotalOf(int id)
        {
            var e = _db.Find(id);
            return e == null ? 0m : e.Subtotal() + e.Total;
        }

        public void Save(Entity$g entity)
        {
            foreach (var line in entity.Lines)
            {
                entity.Total += line;
            }
            _db.Add(entity);
            _db.SaveChanges();
        }
    }
}
CS
}

emit_service() { # $1=m $2=i $3=g
  local m=$1 i=$2 g=$3
  local next=$(( m * PER_MODULE + (i + 1) % PER_MODULE ))
  local viol_persist=0 four=0
  if (( m % 2 == 0 && i == 5 )); then viol_persist=1; fi
  if (( m % 2 == 0 && i == 9 )); then four=1; fi
  # Nome do repositório vizinho pode ser "Store" no caso da violação ARCH0003 (i == 7 em módulo par).
  local nextI=$(( (i + 1) % PER_MODULE )) nextRepo="Entity${next}Repository" ownRepo="Entity${g}Repository"
  if (( m % 2 == 0 && i == 7 )); then ownRepo="Entity${g}Store"; fi
  if (( m % 2 == 0 && nextI == 7 )); then nextRepo="Entity${next}Store"; fi

  local extra_param="" extra_assign="" extra_field=""
  if (( four )); then
    # 2 parâmetros base + 2 extras, todos de TIPOS DISTINTOS (o avaliador conta tipos distintos): 4 > max 3.
    local t=$(( m * PER_MODULE + (i + 2) % PER_MODULE )) f=$(( m * PER_MODULE + (i + 3) % PER_MODULE ))
    extra_param=", Entity${t}Repository third, Entity${f}Repository fourth"
    extra_assign="            _third = third;
            _fourth = fourth;"
    extra_field="        private readonly Entity${t}Repository _third;
        private readonly Entity${f}Repository _fourth;"
  fi
  local persist_using="" persist_field="" persist_body=""
  if (( viol_persist )); then
    persist_using="using $ROOT_NS.Infrastructure.Persistence.Mod$m;"
    persist_field="        private readonly Table${g}Context _direct = new Table${g}Context();"
    persist_body="            _direct.Add(entity);"
  fi

  cat > "$OUT/Services/Entity${g}Service.cs" <<CS
using System;
using System.Collections.Generic;
using System.Linq;
using $ROOT_NS.Domain;
using $ROOT_NS.Infrastructure.Repositories.Mod$m;
$persist_using

namespace $ROOT_NS.Application.Services.Mod$m
{
    public sealed class Entity${g}Service
    {
        private readonly $ownRepo _repo;
        private readonly $nextRepo _neighbour;
$extra_field
$persist_field

        public Entity${g}Service($ownRepo repo, $nextRepo neighbour$extra_param)
        {
            _repo = repo;
            _neighbour = neighbour;
$extra_assign
        }

        public Entity$g Place(int id, IReadOnlyList<decimal> lines)
        {
            var entity = new Entity$g { Id = id, Name = "e" + id };
            foreach (var line in lines)
            {
                entity.Lines.Add(line);
            }
            entity.Total = entity.Subtotal();
$persist_body
            _repo.Save(entity);
            return entity;
        }

        public decimal Report(int id)
        {
            var own = _repo.TotalOf(id);
            var active = _repo.ListActive(DateTime.UtcNow).Sum(e => e.Total);
            return own + active;
        }

        public Entity$g Get(int id) => _repo.GetById(id);
    }
}
CS
}

emit_controller() { # $1=m $2=i $3=g
  local m=$1 i=$2 g=$3
  local viol=0
  if (( m % 2 == 0 && i == 3 )); then viol=1; fi
  local repo_using="" repo_field="" repo_ctor="" repo_assign="" repo_body=""
  if (( viol )); then
    repo_using="using $ROOT_NS.Infrastructure.Repositories.Mod$m;"
    repo_field="        private readonly Entity${g}Repository _repo;"
    repo_ctor=", Entity${g}Repository repo"
    repo_assign="            _repo = repo;"
    repo_body="            var cached = _repo.GetById(id);"
  fi
  cat > "$OUT/Controllers/Entity${g}Controller.cs" <<CS
using System;
using System.Collections.Generic;
using $ROOT_NS.Application.Services.Mod$m;
using $ROOT_NS.Domain;
$repo_using

namespace $ROOT_NS.Api.Controllers.Mod$m
{
    public sealed class Entity${g}Controller
    {
        private readonly Entity${g}Service _service;
$repo_field

        public Entity${g}Controller(Entity${g}Service service$repo_ctor)
        {
            _service = service;
$repo_assign
        }

        public Entity$g Get(int id)
        {
$repo_body
            return _service.Get(id);
        }

        public Entity$g Post(int id, IReadOnlyList<decimal> lines) => _service.Place(id, lines);

        public decimal Report(int id)
        {
            var total = _service.Report(id);
            return total > 0m ? total : 0m;
        }
    }
}
CS
}

mkdir -p "$OUT/Domain" "$OUT/Persistence" "$OUT/Repositories" "$OUT/Services" "$OUT/Controllers"
for (( m = 0; m < MODULES; m++ )); do
  for (( i = 0; i < PER_MODULE; i++ )); do
    g=$(( m * PER_MODULE + i ))
    emit_domain "$g"
    emit_persistence "$m" "$g"
    emit_repository "$m" "$i" "$g"
    emit_service "$m" "$i" "$g"
    emit_controller "$m" "$i" "$g"
  done
done

files=$(find "$OUT" -name '*.cs' | wc -l)
lines=$(find "$OUT" -name '*.cs' -print0 | xargs -0 cat | wc -l)
echo "generate.sh: $files arquivos .cs, $lines linhas, $(( MODULES * PER_MODULE * 5 )) tipos em '$OUT'"
