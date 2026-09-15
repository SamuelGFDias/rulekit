using Contoso.Sales.Application.Abstractions;
using Contoso.Sales.Domain;

namespace Contoso.Sales.Application.Services
{
    /// <summary>
    /// VIOLAÇÃO PROPOSITAL de DOG-004 (max-dependencies, slot ARCH0004): 4 tipos distintos no
    /// construtor público, com `max: 3` para a camada `Service`.
    /// Esperado: 1 diagnóstico ARCH0004 na declaração da classe.
    ///
    /// <para>Nenhuma das dependências está numa camada declarada, o que isola o teste: o que
    /// dispara é a CONTAGEM, não o destino.</para>
    /// </summary>
    public sealed class NotificationService
    {
        private readonly IEmailSender _email;
        private readonly ISmsSender _sms;
        private readonly ITemplateRenderer _renderer;
        private readonly IClock _clock;

        public NotificationService(
            IEmailSender email,
            ISmsSender sms,
            ITemplateRenderer renderer,
            IClock clock)
        {
            _email = email;
            _sms = sms;
            _renderer = renderer;
            _clock = clock;
        }

        public void NotifyPlaced(Order order, string email, string phone)
        {
            var body = _renderer.Render("order-placed", order);
            _email.Send(email, "Pedido recebido em " + _clock.UtcNow, body);
            _sms.Send(phone, "Pedido " + order.Id + " recebido.");
        }
    }
}
