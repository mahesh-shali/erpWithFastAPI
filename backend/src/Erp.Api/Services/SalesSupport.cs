using System.Text;
using Erp.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

internal static class Tx
{
    /// <summary>
    /// Runs <paramref name="work"/> in one transaction inside the retrying execution strategy (EnableRetryOnFailure
    /// refuses user transactions otherwise). The change tracker is cleared first so a retry starts clean.
    /// </summary>
    public static Task<T> InTransactionAsync<T>(this ErpDbContext db, Func<Task<T>> work, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        });

    private static readonly HashSet<string> LockableTables = ["customer_pos", "work_orders", "sales_invoices"];

    /// <summary>Row-locks one document of <typeparamref name="T"/> (FOR UPDATE) in the current organization.</summary>
    public static async Task<T> LockAsync<T>(this ErpDbContext db, string table, Guid id, string notFound, CancellationToken ct)
        where T : class
    {
        // The table name comes from our own code, never from input; the allow-list keeps it that way.
        if (!LockableTables.Contains(table)) throw new ArgumentException($"Table {table} can't be locked.", nameof(table));
        var sql = "SELECT * FROM " + table + " WHERE id = {0} AND organization_id = {1} FOR UPDATE";
        var rows = await db.Set<T>().FromSqlRaw(sql, id, db.CurrentOrgId)
            .IgnoreQueryFilters().AsTracking().ToListAsync(ct);
        return rows.SingleOrDefault() ?? throw new NotFoundException(notFound);
    }
}

/// <summary>Money arithmetic shared by P.O.s and invoices (2 decimals, half away from zero, like printed invoices).</summary>
public static class Money
{
    public static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Taxable amount, discount and tax of one line.</summary>
    public static (decimal Gross, decimal Discount, decimal Amount, decimal Tax) Line(int qty, decimal rate, decimal discountPercent, decimal taxRate)
    {
        var gross = R2(qty * rate);
        var amount = R2(gross * (100 - discountPercent) / 100);
        var tax = R2(amount * taxRate / 100);
        return (gross, gross - amount, amount, tax);
    }

    /// <summary>Grand total rounded to the rupee and the round-off that got it there.</summary>
    public static (decimal GrandTotal, decimal RoundOff) RoundToRupee(decimal exact)
    {
        var grand = Math.Round(exact, 0, MidpointRounding.AwayFromZero);
        return (grand, grand - exact);
    }
}

/// <summary>"Rupees One Lakh Twenty Thousand and Fifty Paise Only" (Indian numbering: thousand, lakh, crore).</summary>
public static class AmountInWords
{
    private static readonly string[] Ones =
    [
        "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve",
        "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];
    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    public static string Rupees(decimal amount)
    {
        if (amount < 0) return "Minus " + Rupees(-amount);
        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var rupees = (long)Math.Floor(amount);
        var paise = (int)((amount - rupees) * 100);
        var sb = new StringBuilder("Rupees ").Append(rupees == 0 ? "Zero" : Words(rupees));
        if (paise > 0) sb.Append(" and ").Append(Words(paise)).Append(" Paise");
        return sb.Append(" Only").ToString();
    }

    private static string Words(long n)
    {
        var parts = new List<string>();
        void Add(long value, string unit) { if (value > 0) parts.Add(Below1000((int)value) + (unit.Length > 0 ? " " + unit : "")); }
        if (n >= 10_000_000) { parts.Add(Words(n / 10_000_000) + " Crore"); n %= 10_000_000; }
        Add(n / 100_000, "Lakh"); n %= 100_000;
        Add(n / 1000, "Thousand"); n %= 1000;
        Add(n, "");
        return string.Join(" ", parts);
    }

    private static string Below1000(int n)
    {
        var parts = new List<string>();
        if (n >= 100) { parts.Add(Ones[n / 100] + " Hundred"); n %= 100; }
        if (n >= 20) { parts.Add(Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : "")); }
        else if (n > 0) parts.Add(Ones[n]);
        return string.Join(" ", parts);
    }
}
