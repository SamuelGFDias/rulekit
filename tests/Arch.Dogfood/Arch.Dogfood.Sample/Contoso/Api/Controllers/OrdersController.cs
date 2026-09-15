using System;
using System.Collections.Generic;
using Contoso.Sales.Application.Services;
using Contoso.Sales.Domain;

namespace Contoso.Sales.Api.Controllers
{
    /// <summary>
    /// CASO LIMPO. É o desenho que a política aprova: o Controller conhece apenas a camada
    /// `Service`, nunca `Repository` nem `Persistence`. Qualquer diagnóstico neste arquivo é
    /// falso positivo.
    /// </summary>
    public sealed class OrdersController
    {
        private readonly IOrderService _orders;
        private readonly ICustomerService _customers;

        public OrdersController(IOrderService orders, ICustomerService customers)
        {
            _orders = orders;
            _customers = customers;
        }

        public Order Get(Guid id) => _orders.Get(id);

        public Order Post(Guid customerId, IReadOnlyList<OrderItem> items)
        {
            var customer = _customers.FindByEmail("comprador@contoso.example");
            var order = _orders.Place(customer?.Id ?? customerId, items);
            return order;
        }

        public IReadOnlyList<Customer> ActiveCustomers() => _customers.ListActive();
    }
}
