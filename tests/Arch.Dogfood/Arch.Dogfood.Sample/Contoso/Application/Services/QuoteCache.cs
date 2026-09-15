using System.Collections.Generic;

namespace Contoso.Sales.Application.Services
{
    /// <summary>
    /// CASO LIMPO com genérico. Está na camada `Service` (namespace), então DOG-004 o avalia — e
    /// deve passar: construtor sem parâmetros.
    ///
    /// <para>O uso construído (<c>QuoteCache&lt;decimal&gt;</c>, em <see cref="QuoteCacheUsage"/>)
    /// chamando um membro da própria definição genérica exercita a normalização por
    /// <c>OriginalDefinition</c> no LayerResolver e o controle "origem == destino" do
    /// ForbiddenCallEvaluator. Nenhum diagnóstico esperado.</para>
    /// </summary>
    public sealed class QuoteCache<TValue>
    {
        private readonly Dictionary<string, TValue> _entries = new Dictionary<string, TValue>();

        public void Set(string key, TValue value) => _entries[key] = value;

        public bool TryGet(string key, out TValue value) => _entries.TryGetValue(key, out value);

        public void Seed(string key, TValue value)
        {
            // Instância construída chamando membro da mesma definição genérica.
            Set(key, value);
        }
    }

    /// <summary>Consumidor do cache genérico — também camada `Service`, também limpo.</summary>
    public sealed class QuoteCacheUsage
    {
        private readonly QuoteCache<decimal> _cache = new QuoteCache<decimal>();

        public decimal GetOrDefault(string sku)
        {
            decimal value;
            return _cache.TryGet(sku, out value) ? value : 0m;
        }
    }
}
