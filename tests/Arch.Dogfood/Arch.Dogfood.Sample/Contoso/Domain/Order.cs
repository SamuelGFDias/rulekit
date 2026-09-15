using System;
using System.Collections.Generic;

namespace Contoso.Sales.Domain
{
    /// <summary>
    /// Entidades de domínio. Este namespace NÃO pertence a nenhuma camada declarada em
    /// arch-rules.yaml — de propósito: tipos de domínio circulam livremente entre Controller,
    /// Service e Repository, e nenhuma regra deve disparar por causa deles. Se um diagnóstico
    /// apontar para cá, é falso positivo.
    /// </summary>
    public sealed class Order
    {
        public Guid Id { get; set; }

        public Guid CustomerId { get; set; }

        public OrderStatus Status { get; set; }

        public decimal Total { get; set; }

        public DateTime PlacedAt { get; set; }

        public List<OrderItem> Items { get; set; } = new List<OrderItem>();

        /// <summary>Método de domínio chamado por Services — caso limpo esperado.</summary>
        public decimal ComputeSubtotal()
        {
            decimal subtotal = 0m;
            foreach (var item in Items)
            {
                subtotal += item.UnitPrice * item.Quantity;
            }

            return subtotal;
        }
    }

    public sealed class OrderItem
    {
        public Guid Id { get; set; }

        public string Sku { get; set; }

        public int Quantity { get; set; }

        public decimal UnitPrice { get; set; }
    }

    public sealed class Customer
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public string Email { get; set; }

        public bool IsActive { get; set; }
    }

    public enum OrderStatus
    {
        Draft = 0,
        Placed = 1,
        Shipped = 2,
        Cancelled = 3,
    }
}
