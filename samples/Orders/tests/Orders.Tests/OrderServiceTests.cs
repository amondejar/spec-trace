using System;
using Orders.Tests.Specs;
using SpecTrace;
using Xunit;

namespace Orders.Tests
{
    public class OrderServiceTests
    {
        private readonly OrderService _service = new();

        [Fact]
        [Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_1)]
        public void Create_with_two_lines_creates_pending_order()
        {
            var order = _service.Create(new[] { new OrderLine("A", 1, 10m), new OrderLine("B", 2, 5m) });

            Assert.Equal(2, order.Lines.Count);
            Assert.Equal(OrderState.Pending, order.State);
        }

        [Fact]
        [Verifies(Requirements.ORD_001, AcceptanceCriteria.ORD_001.AC_2)]
        public void Create_with_empty_cart_is_rejected()
        {
            Assert.Throws<ArgumentException>(() => _service.Create(Array.Empty<OrderLine>()));
        }

        [Fact]
        [Verifies(Requirements.ORD_002, AcceptanceCriteria.ORD_002.AC_1)]
        public void Cancel_pending_order_sets_cancelled()
        {
            var order = _service.Create(new[] { new OrderLine("A", 1, 10m) });

            _service.Cancel(order);

            Assert.Equal(OrderState.Cancelled, order.State);
        }

        // ORD-002 AC-2 (cancelar un pedido enviado) no tiene test: SpecTrace lo marca con ST0005.
    }
}
