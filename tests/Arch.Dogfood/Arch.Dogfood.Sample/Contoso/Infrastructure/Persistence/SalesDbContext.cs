using System;
using System.Collections.Generic;
using Contoso.Sales.Domain;

namespace Contoso.Sales.Infrastructure.Persistence
{
    /// <summary>
    /// Camada `Persistence` do arch-rules.yaml. Substituto sintético de um DbContext do EF Core —
    /// sem a dependência real, que não acrescentaria nada ao exercício de dogfooding (o analyzer
    /// trabalha sobre símbolos, não sobre o comportamento em runtime).
    /// </summary>
    public sealed class SalesDbContext : IDisposable
    {
        private readonly List<Order> _orders = new List<Order>();
        private readonly List<Customer> _customers = new List<Customer>();

        public IReadOnlyList<Order> Orders => _orders;

        public IReadOnlyList<Customer> Customers => _customers;

        public void Add(Order order) => _orders.Add(order);

        public void Add(Customer customer) => _customers.Add(customer);

        public int SaveChanges() => _orders.Count + _customers.Count;

        public void Dispose()
        {
            _orders.Clear();
            _customers.Clear();
        }
    }
}
