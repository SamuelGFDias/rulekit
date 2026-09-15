using System.Collections.Generic;
using Contoso.Sales.Infrastructure.Persistence;

namespace Contoso.Sales.Infrastructure.Repositories
{
    /// <summary>
    /// Alvo das violações propositais de DOG-001 (forbidden-call Controller → Repository) —
    /// ver <c>Contoso/Api/Controllers/ReportsController.cs</c>. Este arquivo em si não deve
    /// gerar diagnóstico nenhum.
    /// </summary>
    public interface IReportRepository
    {
        IReadOnlyList<string> ListDaily();

        int TotalCount { get; }
    }

    /// <inheritdoc cref="IReportRepository"/>
    public sealed class ReportRepository : IReportRepository
    {
        private readonly SalesDbContext _db;

        public ReportRepository(SalesDbContext db)
        {
            _db = db;
        }

        public int TotalCount => _db.Orders.Count;

        public IReadOnlyList<string> ListDaily()
        {
            var lines = new List<string>();
            foreach (var order in _db.Orders)
            {
                lines.Add(order.Id + ";" + order.Total);
            }

            return lines;
        }

        public void Rebuild() => _db.SaveChanges();
    }
}
