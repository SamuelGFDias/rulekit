namespace Contoso.Sales.Infrastructure.Persistence
{
    /// <summary>
    /// Segundo tipo da camada `Persistence`. Existe para provar que a camada é definida por
    /// NAMESPACE (e não por um único tipo bem conhecido): a regra DOG-002 deve valer para ele
    /// tanto quanto para <see cref="SalesDbContext"/>.
    /// </summary>
    public sealed class ConnectionFactory
    {
        private readonly string _connectionString;

        public ConnectionFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public string Describe() => "conexão para " + _connectionString;
    }
}
