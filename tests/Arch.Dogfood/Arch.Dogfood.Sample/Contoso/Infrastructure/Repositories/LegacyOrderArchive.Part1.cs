using System.Collections.Generic;
using Contoso.Sales.Domain;

namespace Contoso.Sales.Infrastructure.Repositories
{
    /// <summary>
    /// VIOLAÇÃO PROPOSITAL de DOG-003, em duas PARTES (arquivos separados). Serve para verificar a
    /// decisão de design documentada em <c>NamingConventionEvaluator</c>: a convenção é propriedade
    /// do SÍMBOLO, não da árvore sintática, então um `partial class` mal nomeado deve gerar
    /// EXATAMENTE 1 diagnóstico (na primeira localização de declaração), e não um por arquivo.
    /// Se aparecerem 2 ARCH0003 para este tipo, é defeito.
    /// </summary>
    public sealed partial class LegacyOrderArchive
    {
        private readonly List<Order> _archived = new List<Order>();

        public void Archive(Order order) => _archived.Add(order);
    }
}
