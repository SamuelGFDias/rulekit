using System;
using System.Collections.Generic;
using Contoso.Sales.Application.Abstractions;
using Contoso.Sales.Domain;
using Contoso.Sales.Infrastructure.Repositories;

namespace Contoso.Sales.Application.Services
{
    public interface IOrderService
    {
        Order Place(Guid customerId, IReadOnlyList<OrderItem> items);

        Order Get(Guid id);
    }

    /// <summary>
    /// CASO LIMPO, e o mais importante do exercício: este é o fluxo que a política INCENTIVA.
    /// <list type="bullet">
    ///   <item>Service → Repository: permitido (DOG-001 só proíbe Controller → Repository).</item>
    ///   <item>Service → Domain: permitido (Domain não está em camada nenhuma).</item>
    ///   <item>3 parâmetros de construtor com `max: 3` — limite INCLUSIVO, então NÃO viola DOG-004.
    ///   É o caso de borda que pega um off-by-one no avaliador.</item>
    /// </list>
    /// Qualquer diagnóstico neste arquivo é falso positivo.
    /// </summary>
    public sealed class OrderService : IOrderService
    {
        private readonly IOrderRepository _orders;
        private readonly ICustomerRepository _customers;
        private readonly IAuditTrail _audit;

        public OrderService(IOrderRepository orders, ICustomerRepository customers, IAuditTrail audit)
        {
            _orders = orders;
            _customers = customers;
            _audit = audit;
        }

        public Order Place(Guid customerId, IReadOnlyList<OrderItem> items)
        {
            var order = new Order
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                Status = OrderStatus.Placed,
                PlacedAt = DateTime.UtcNow,
            };

            foreach (var item in items)
            {
                order.Items.Add(item);
            }

            // Chamada a método privado do PRÓPRIO tipo: controle de falso positivo nº 1 do
            // ForbiddenCallEvaluator (origem == destino nunca cruza camada).
            ApplyTotals(order);

            _orders.Save(order);
            _audit.Record("order.placed", order.Id.ToString());
            return order;
        }

        public Order Get(Guid id) => _orders.GetById(id);

        public IReadOnlyList<Order> ListForCustomerEmail(string email)
        {
            var customer = _customers.FindByEmail(email);
            return customer == null
                ? new List<Order>()
                : _orders.ListByCustomer(customer.Id);
        }

        private static void ApplyTotals(Order order)
        {
            order.Total = order.ComputeSubtotal();
        }
    }
}
