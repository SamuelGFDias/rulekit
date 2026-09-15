using System;
using System.Collections.Generic;
using Contoso.Sales.Domain;
using Contoso.Sales.Infrastructure.Persistence;

namespace Contoso.Sales.Infrastructure.Repositories
{
    /// <summary>
    /// Camada `Repository`. CASO LIMPO em todas as 4 regras:
    /// nome termina em "Repository" (DOG-003 satisfeita) e o acesso a `Persistence` é legítimo —
    /// DOG-002 proíbe Service → Persistence, e o repositório é exatamente o intermediário que a
    /// política manda usar (`through: [Repository]`).
    /// </summary>
    public interface IOrderRepository
    {
        Order GetById(Guid id);

        IReadOnlyList<Order> ListByCustomer(Guid customerId);

        void Save(Order order);
    }

    /// <inheritdoc cref="IOrderRepository"/>
    public sealed class OrderRepository : IOrderRepository
    {
        private readonly SalesDbContext _db;

        public OrderRepository(SalesDbContext db)
        {
            _db = db;
        }

        public Order GetById(Guid id)
        {
            // Repository -> Persistence: permitido. Nenhum diagnóstico esperado nestas linhas.
            foreach (var order in _db.Orders)
            {
                if (order.Id == id)
                {
                    return order;
                }
            }

            return null;
        }

        public IReadOnlyList<Order> ListByCustomer(Guid customerId)
        {
            var result = new List<Order>();
            foreach (var order in _db.Orders)
            {
                if (order.CustomerId == customerId)
                {
                    result.Add(order);
                }
            }

            return result;
        }

        public void Save(Order order)
        {
            _db.Add(order);
            _db.SaveChanges();
        }
    }
}
