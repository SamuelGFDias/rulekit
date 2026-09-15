namespace Contoso.Sales.Application.Abstractions
{
    /// <summary>
    /// Abstrações transversais. Namespace deliberadamente FORA de todas as camadas declaradas —
    /// serve para provar que um tipo não classificado não faz regra nenhuma disparar, nem como
    /// origem nem como destino.
    /// </summary>
    public interface IAuditTrail
    {
        void Record(string action, string detail);
    }

    public interface IClock
    {
        System.DateTime UtcNow { get; }
    }

    public interface IEmailSender
    {
        void Send(string to, string subject, string body);
    }

    public interface ISmsSender
    {
        void Send(string phone, string body);
    }

    public interface ITemplateRenderer
    {
        string Render(string template, object model);
    }
}
