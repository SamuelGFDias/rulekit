using Contoso.Sales.Infrastructure.Repositories;

namespace Contoso.Sales.Api.Controllers
{
    /// <summary>
    /// CASO LIMPO DELIBERADAMENTE HOSTIL: este Controller MENCIONA tipos da camada `Repository`
    /// sem acessá-los, e nenhuma das duas menções pode disparar DOG-001.
    /// <list type="bullet">
    ///   <item><c>nameof(IReportRepository.TotalCount)</c> produz um IPropertyReferenceOperation de
    ///   verdade, mas é constante de compilação — é o controle de falso positivo nº 2 do
    ///   ForbiddenCallEvaluator (<c>IsInsideNameOf</c>).</item>
    ///   <item><c>typeof(IOrderRepository)</c> não é nenhuma das 4 OperationKind cobertas; o
    ///   <c>.Name</c> logo em seguida é propriedade de <c>System.Type</c>, que não está em camada
    ///   nenhuma.</item>
    /// </list>
    /// Um diagnóstico aqui é falso positivo.
    /// </summary>
    public sealed class MetricsController
    {
        public string DescribeContract()
        {
            return nameof(IReportRepository.TotalCount) + " em " + typeof(IOrderRepository).Name;
        }
    }
}
