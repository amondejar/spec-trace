using System;
using System.Collections.Generic;
using System.Linq;
using Orders.Specs;
using SpecTrace;

namespace Orders
{
    public enum OrderState { Pending, Shipped, Cancelled }

    public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice);

    public sealed class Order
    {
        public Order(Guid id, IReadOnlyList<OrderLine> lines)
        {
            Id = id;
            Lines = lines;
        }

        public Guid Id { get; }
        public IReadOnlyList<OrderLine> Lines { get; }
        public OrderState State { get; internal set; } = OrderState.Pending;
        public decimal Total => Lines.Sum(l => l.Quantity * l.UnitPrice);
    }

    public sealed class OrderService
    {
        [Implements(Requirements.ORD_001)]
        public Order Create(IReadOnlyList<OrderLine> cart)
        {
            if (cart.Count == 0) throw new ArgumentException("The cart is empty.", nameof(cart));
            return new Order(Guid.NewGuid(), cart.ToList());
        }

        [Implements(Requirements.ORD_002)]
        public void Cancel(Order order)
        {
            if (order.State != OrderState.Pending)
                throw new InvalidOperationException($"Cannot cancel an order in state {order.State}.");
            order.State = OrderState.Cancelled;
        }

        // ORD-003 (coupons) is not implemented yet: SpecTrace reports it as ST0003.
    }
}
