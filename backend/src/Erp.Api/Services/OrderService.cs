using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

public interface IOrderService
{
    Task<OrderDto> CreateAsync(CreateOrderRequest req, CancellationToken ct);
    Task<OrderDto> UpdateStatusAsync(Guid id, string status, CancellationToken ct);
}

public sealed class OrderService(ErpDbContext db, IStockPosting posting) : IOrderService
{
    // Allowed state transitions
    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [OrderStatus.Pending] = [OrderStatus.Paid, OrderStatus.Cancelled],
        [OrderStatus.Paid] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [],
        [OrderStatus.Cancelled] = [],
    };

    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var next) && next.Contains(to);

    public static decimal CalculateTotal(IEnumerable<(int Qty, decimal Price)> lines) =>
        lines.Sum(l => l.Qty * l.Price);

    public async Task<OrderDto> CreateAsync(CreateOrderRequest req, CancellationToken ct)
    {
        if (req.Items.Count == 0) throw new DomainException("Order must have at least one item.");
        if (req.Items.GroupBy(i => i.ProductId).Any(g => g.Count() > 1))
            throw new DomainException("Duplicate products in order.");

        if (!await db.Customers.AnyAsync(c => c.Id == req.CustomerId, ct))
            throw new NotFoundException("Customer not found.");

        var ids = req.Items.Select(i => i.ProductId).ToList();
        var products = await db.Products.Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        if (products.Count != ids.Count) throw new NotFoundException("One or more products not found.");

        // The connection uses a retrying execution strategy (EnableRetryOnFailure), which refuses
        // user-initiated transactions unless the whole unit of work runs inside the strategy,
        // so a transient failure retries everything (transaction included) from the start.
        // Orders ship from the default store: stock goes out through the stock ledger at the moving-average
        // rate, which also lowers products.stock_quantity.
        var store = await posting.EnsureDefaultStoreAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear(); // a retry must not re-add entities from the failed attempt
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var orderId = Guid.NewGuid();

            // PostAsync locks the balance rows in one global order (product, store), so concurrent orders for
            // [A, B] and [B, A] can't deadlock, and refuses to take any store below zero (no oversell).
            await posting.PostAsync(req.Items.Select(i => new StockMovement(i.ProductId, store, -i.Quantity)).ToList(),
                new Voucher(VoucherType.SalesOrder, orderId, "SO-" + orderId.ToString("N")[..8].ToUpperInvariant(),
                    DateOnly.FromDateTime(DateTime.UtcNow)), ct);

            var order = new Order
            {
                Id = orderId,
                CustomerId = req.CustomerId,
                Items = req.Items.Select(i => new OrderItem
                {
                    Id = Guid.NewGuid(),
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    UnitPrice = products[i.ProductId].UnitPrice
                }).ToList()
            };
            order.TotalAmount = CalculateTotal(order.Items.Select(i => (i.Quantity, i.UnitPrice)));
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToDto(order);
        });
    }

    public async Task<OrderDto> UpdateStatusAsync(Guid id, string status, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id, ct)
                    ?? throw new NotFoundException("Order not found.");
        if (!CanTransition(order.Status, status))
            throw new DomainException($"Cannot change status from {order.Status} to {status}.");
        order.Status = status;
        await db.SaveChangesAsync(ct);
        return ToDto(order);
    }

    public static OrderDto ToDto(Order o) => new(o.Id, o.CustomerId, o.Status, o.TotalAmount, o.OrderedAt,
        o.Items.Select(i => new OrderItemDto(i.ProductId, i.Quantity, i.UnitPrice)).ToList());
}
