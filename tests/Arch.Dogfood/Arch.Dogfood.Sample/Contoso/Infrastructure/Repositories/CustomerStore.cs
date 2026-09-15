using System;
using System.Collections.Generic;
using Contoso.Sales.Domain;
using Contoso.Sales.Infrastructure.Persistence;

namespace Contoso.Sales.Infrastructure.Repositories
{
    /// <summary>
    /// A interface está BEM nomeada (termina em "Repository") — caso limpo para DOG-003.
    /// </summary>
    public interface ICustomerRepository
    {
        Customer FindByEmail(string email);

        IReadOnlyList<Customer> ListActive();
    }

    /// <summary>
    /// VIOLAÇÃO PROPOSITAL de DOG-003 (naming-convention, slot ARCH0003): o tipo está na camada
    /// `Repository` (pelo namespace) mas o nome termina em "Store", não em "Repository".
    /// Esperado: 1 diagnóstico ARCH0003 na declaração da classe.
    /// </summary>
    public sealed class CustomerStore : ICustomerRepository
    {
        private readonly SalesDbContext _db;

        public CustomerStore(SalesDbContext db)
        {
            _db = db;
        }

        public Customer FindByEmail(string email)
        {
            foreach (var customer in _db.Customers)
            {
                if (string.Equals(customer.Email, email, StringComparison.OrdinalIgnoreCase))
                {
                    return customer;
                }
            }

            return null;
        }

        public IReadOnlyList<Customer> ListActive()
        {
            var result = new List<Customer>();
            foreach (var customer in _db.Customers)
            {
                if (customer.IsActive)
                {
                    result.Add(customer);
                }
            }

            return result;
        }
    }
}
