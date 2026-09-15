using System.Collections.Generic;
using Contoso.Sales.Application.Services;
using Contoso.Sales.Domain;

namespace Contoso.Sales.Api.Controllers
{
    /// <summary>
    /// CASO LIMPO, com um detalhe de propósito: uma função LOCAL e uma lambda chamando o Service.
    /// O <c>ForbiddenCallEvaluator</c> sobe a cadeia de <c>ContainingSymbol</c> para achar o tipo
    /// que executa a operação — dentro de função local o símbolo reportado é a própria função, não
    /// o tipo. Aqui isso não deve mudar nada (o alvo é `Service`, permitido); o valor do caso é
    /// garantir que o mecanismo não passa a acusar (ou a ignorar) violações por causa do aninhamento.
    /// </summary>
    public sealed class CustomersController
    {
        private readonly ICustomerService _customers;

        public CustomersController(ICustomerService customers)
        {
            _customers = customers;
        }

        public IReadOnlyList<Customer> GetActive()
        {
            IReadOnlyList<Customer> Fetch()
            {
                return _customers.ListActive();
            }

            return Fetch();
        }

        public IReadOnlyList<string> Emails()
        {
            var result = new List<string>();

            // Lambda: não cria símbolo de contenção próprio, a operação pertence a este método.
            System.Func<Customer, string> describe = customer =>
                customer.Email + " / " + _customers.FindByEmail(customer.Email)?.Name;

            foreach (var customer in _customers.ListActive())
            {
                result.Add(describe(customer));
            }

            return result;
        }
    }
}
