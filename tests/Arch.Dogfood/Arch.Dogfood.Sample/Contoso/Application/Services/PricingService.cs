using Contoso.Sales.Domain;
using Contoso.Sales.Infrastructure.Persistence;

namespace Contoso.Sales.Application.Services
{
    /// <summary>
    /// VIOLAÇÃO PROPOSITAL de DOG-002 (must-route-through, slot ARCH0002): a política manda o
    /// Service alcançar `Persistence` PASSANDO POR `Repository`; este serviço fala com o
    /// <see cref="SalesDbContext"/> diretamente.
    ///
    /// <para>Esperados 2 diagnósticos ARCH0002 (um por acesso — leitura de propriedade e
    /// invocação de método); o parâmetro do construtor e a atribuição ao campo NÃO geram
    /// diagnóstico, porque nenhum dos dois é uma das 4 OperationKind cobertas (ADR-001 §5).</para>
    /// </summary>
    public sealed class PricingService
    {
        private readonly SalesDbContext _db;

        public PricingService(SalesDbContext db)
        {
            _db = db;
        }

        public decimal RecalculateAll()
        {
            decimal total = 0m;

            // VIOLAÇÃO 1 — leitura de propriedade de Persistence a partir de Service.
            foreach (Order order in _db.Orders)
            {
                order.Total = order.ComputeSubtotal();
                total += order.Total;
            }

            // VIOLAÇÃO 2 — invocação de método de Persistence a partir de Service.
            _db.SaveChanges();

            return total;
        }
    }
}
