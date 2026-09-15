using System.Collections.Generic;
using Contoso.Sales.Infrastructure.Persistence;
using Contoso.Sales.Infrastructure.Repositories;

namespace Contoso.Sales.Api.Controllers
{
    /// <summary>
    /// VIOLAÇÕES PROPOSITAIS de DOG-001 (forbidden-call Controller → Repository, slot ARCH0001).
    /// Quatro acessos diretos, um por OperationKind coberta pela regra — é o cenário clássico do
    /// "controller que virou repositório com rota HTTP".
    /// </summary>
    public sealed class ReportsController
    {
        private readonly IReportRepository _reports;

        public ReportsController(IReportRepository reports)
        {
            // Injeção não gera operação coberta: o parâmetro e a atribuição de campo não são
            // Invocation/ObjectCreation/PropertyReference/FieldReference sobre o tipo alvo.
            _reports = reports;
        }

        public IReadOnlyList<string> Daily()
        {
            // VIOLAÇÃO 1 — Invocation.
            var rows = _reports.ListDaily();

            // VIOLAÇÃO 2 — PropertyReference.
            var total = _reports.TotalCount;

            var output = new List<string>(rows) { "total=" + total };
            return output;
        }

        public void Rebuild()
        {
            // `new SalesDbContext()` alcança a camada Persistence a partir de Controller — coberto
            // pela regra DOG-005, que está `enabled: false` no YAML. NÃO deve gerar diagnóstico.
            using (var db = new SalesDbContext())
            {
                // VIOLAÇÃO 3 — ObjectCreation de um tipo da camada Repository.
                var repository = new ReportRepository(db);

                // VIOLAÇÃO 4 — Invocation sobre a instância criada.
                repository.Rebuild();
            }
        }
    }
}
