using System.Collections.Generic;
using Contoso.Sales.Application.Abstractions;
using Contoso.Sales.Domain;
using Contoso.Sales.Infrastructure.Repositories;

namespace Contoso.Sales.Application.Services
{
    public interface ICustomerService
    {
        Customer FindByEmail(string email);

        IReadOnlyList<Customer> ListActive();
    }

    /// <summary>
    /// CASO LIMPO. 2 parâmetros de construtor (abaixo de `max: 3`) e acesso a `Repository`,
    /// que é o caminho aprovado pela política.
    ///
    /// <para>Contém também um construtor de conveniência com MENOS parâmetros: o
    /// MaxDependenciesEvaluator conta o MAIOR construtor público, então a presença da sobrecarga
    /// não pode mudar o resultado. Se mudar, é defeito.</para>
    /// </summary>
    public sealed class CustomerService : ICustomerService
    {
        private readonly ICustomerRepository _customers;
        private readonly IAuditTrail _audit;

        public CustomerService(ICustomerRepository customers)
            : this(customers, null)
        {
        }

        public CustomerService(ICustomerRepository customers, IAuditTrail audit)
        {
            _customers = customers;
            _audit = audit;
        }

        public Customer FindByEmail(string email)
        {
            var customer = _customers.FindByEmail(email);
            if (customer != null && _audit != null)
            {
                _audit.Record("customer.read", customer.Id.ToString());
            }

            return customer;
        }

        public IReadOnlyList<Customer> ListActive() => _customers.ListActive();
    }
}
