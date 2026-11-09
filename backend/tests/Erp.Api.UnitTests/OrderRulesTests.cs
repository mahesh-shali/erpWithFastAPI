using Erp.Api.Domain;
using Erp.Api.Services;
using FluentAssertions;

namespace Erp.Api.UnitTests;

public class OrderRulesTests
{
    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Paid, true)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Paid, OrderStatus.Shipped, true)]
    [InlineData(OrderStatus.Pending, OrderStatus.Shipped, false)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Paid, false)]
    [InlineData(OrderStatus.Paid, "Bogus", false)]
    public void Status_transitions_follow_state_machine(string from, string to, bool expected) =>
        OrderService.CanTransition(from, to).Should().Be(expected);

    [Fact]
    public void Total_is_sum_of_qty_times_price() =>
        OrderService.CalculateTotal([(2, 10.50m), (3, 1.25m)]).Should().Be(24.75m);

    [Fact]
    public void Total_of_empty_order_is_zero() =>
        OrderService.CalculateTotal([]).Should().Be(0m);

    [Fact]
    public void ToDto_maps_items()
    {
        var o = new Order
        {
            Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), TotalAmount = 5,
            Items = [new OrderItem { ProductId = Guid.NewGuid(), Quantity = 1, UnitPrice = 5 }]
        };
        var dto = OrderService.ToDto(o);
        dto.Items.Should().ContainSingle().Which.UnitPrice.Should().Be(5);
    }
}
