namespace Contoso.Sales.Api.Controllers
{
    /// <summary>
    /// CASO LIMPO trivial: Controller sem dependência alguma. Existe para provar que a simples
    /// PERTINÊNCIA a uma camada com regras não gera diagnóstico — só o comportamento proibido gera.
    /// </summary>
    public sealed class HealthController
    {
        public string Get() => "ok";
    }
}
