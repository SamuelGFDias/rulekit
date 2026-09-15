using System.Collections.Generic;
using Contoso.Sales.Domain;

namespace Contoso.Sales.Infrastructure.Repositories
{
    /// <summary>Segunda parte de <see cref="LegacyOrderArchive"/> — ver comentário na Part1.</summary>
    public sealed partial class LegacyOrderArchive
    {
        public IReadOnlyList<Order> All() => _archived;

        public int Count => _archived.Count;
    }
}
