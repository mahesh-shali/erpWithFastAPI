namespace Erp.Api.Services;

/// <summary>
/// Starting point for a new print format of each printable page, using its variables. For Customer P.O. and Work Order
/// the starter is also the built-in layout (rendered on the server); invoices have their own built-in React layout.
/// </summary>
public static class PrintTemplateStarter
{
    public static string For(string pageKey) => pageKey switch
    {
        Domain.PageKeys.SalesInvoices => SalesInvoice,
        Domain.PageKeys.CustomerPos => CustomerPo,
        Domain.PageKeys.WorkOrders => WorkOrder,
        _ => throw new DomainException($"Print formats exist only for these pages: {string.Join(", ", Domain.PrintablePages.All)}."),
    };

    public const string SalesInvoice = """
<style>
  .invoice { font-family: Arial, Helvetica, sans-serif; font-size: 12px; color: #111; }
  .head { display: flex; justify-content: space-between; border-bottom: 2px solid #111; padding-bottom: 8px; }
  .head h1 { margin: 0 0 4px; font-size: 20px; }
  .title { text-align: right; }
  .title h2 { margin: 0 0 6px; font-size: 16px; text-transform: uppercase; letter-spacing: 1px; }
  .parties { display: flex; gap: 24px; padding: 8px 0; border-bottom: 1px solid #ccc; }
  .parties > div { flex: 1; }
  .label { color: #555; font-size: 10px; text-transform: uppercase; letter-spacing: .5px; margin: 0 0 4px; }
  table { width: 100%; border-collapse: collapse; margin-top: 10px; }
  th { background: #f2f2f2; text-align: left; }
  th, td { border: 1px solid #ccc; padding: 4px 6px; }
  .num { text-align: right; }
  .foot { display: flex; justify-content: space-between; gap: 24px; margin-top: 10px; }
  .totals td { border: 0; padding: 2px 6px; }
  .grand td { font-size: 14px; font-weight: bold; border-top: 1px solid #111; }
  .sign { display: flex; justify-content: space-between; margin-top: 40px; }
  .watermark { color: #c62828; font-weight: bold; text-align: center; font-size: 18px; }
</style>
<div class="invoice">
  {{ if invoice.is_draft }}<p class="watermark">DRAFT</p>{{ end }}
  {{ if invoice.is_cancelled }}<p class="watermark">CANCELLED</p>{{ end }}
  <div class="head">
    <div>
      <h1>{{ company.name }}</h1>
      <div>{{ company.address_html }}</div>
      <div>{{ company.state }} {{ if company.phone != "" }}· {{ company.phone }}{{ end }} {{ if company.email != "" }}· {{ company.email }}{{ end }}</div>
      {{ if company.gstin != "" }}<div><strong>GSTIN:</strong> {{ company.gstin }}</div>{{ end }}
    </div>
    <div class="title">
      <h2>Tax Invoice</h2>
      <div>Invoice no. <strong>{{ invoice.number }}</strong></div>
      <div>Date {{ invoice.date }} · Due {{ invoice.due_date }}</div>
      {{ if invoice.customer_po_ref != "" }}<div>Your P.O. {{ invoice.customer_po_ref }}</div>{{ end }}
    </div>
  </div>
  <div class="parties">
    <div>
      <p class="label">Bill to</p>
      <strong>{{ customer.name }}</strong>
      <div>{{ customer.address_html }}</div>
      {{ if customer.gstin != "" }}<div>GSTIN: {{ customer.gstin }}</div>{{ end }}
    </div>
    <div>
      <p class="label">Ship to</p>
      <div>{{ customer.ship_to_html }}</div>
      {{ if invoice.place_of_supply != "" }}<div>Place of supply: {{ invoice.place_of_supply }}</div>{{ end }}
    </div>
  </div>
  <table>
    <thead>
      <tr><th>#</th><th>Item</th><th>HSN</th><th class="num">Qty</th><th class="num">Rate</th><th class="num">Disc. %</th>
        <th class="num">Taxable</th><th class="num">GST %</th><th class="num">GST</th><th class="num">Amount</th></tr>
    </thead>
    <tbody>
      {{ for line in lines }}
      <tr>
        <td>{{ line.no }}</td><td>{{ line.name }}<br><small>{{ line.sku }}</small></td><td>{{ line.hsn }}</td>
        <td class="num">{{ line.quantity }} {{ line.unit }}</td><td class="num">{{ line.rate | fmt.money }}</td>
        <td class="num">{{ line.discount_percent }}</td><td class="num">{{ line.amount | fmt.money }}</td>
        <td class="num">{{ line.tax_rate }}</td><td class="num">{{ line.tax_amount | fmt.money }}</td><td class="num">{{ line.total | fmt.money }}</td>
      </tr>
      {{ end }}
    </tbody>
  </table>
  <div class="foot">
    <table style="width: auto">
      <thead>
        <tr><th class="num">GST %</th><th class="num">Taxable</th>
          {{ if invoice.is_inter_state }}<th class="num">IGST</th>{{ else }}<th class="num">CGST</th><th class="num">SGST</th>{{ end }}</tr>
      </thead>
      <tbody>
        {{ for t in tax_summary }}
        <tr><td class="num">{{ t.rate }}</td><td class="num">{{ t.taxable | fmt.money }}</td>
          {{ if invoice.is_inter_state }}<td class="num">{{ t.igst | fmt.money }}</td>
          {{ else }}<td class="num">{{ t.cgst | fmt.money }}</td><td class="num">{{ t.sgst | fmt.money }}</td>{{ end }}</tr>
        {{ end }}
      </tbody>
    </table>
    <table class="totals" style="width: auto">
      <tr><td>Sub-total</td><td class="num">{{ invoice.sub_total | fmt.money }}</td></tr>
      {{ if invoice.discount_total > 0 }}<tr><td>Discount</td><td class="num">-{{ invoice.discount_total | fmt.money }}</td></tr>{{ end }}
      {{ if invoice.is_inter_state }}<tr><td>IGST</td><td class="num">{{ invoice.igst_total | fmt.money }}</td></tr>
      {{ else }}<tr><td>CGST</td><td class="num">{{ invoice.cgst_total | fmt.money }}</td></tr>
      <tr><td>SGST</td><td class="num">{{ invoice.sgst_total | fmt.money }}</td></tr>{{ end }}
      {{ if invoice.round_off != 0 }}<tr><td>Round off</td><td class="num">{{ invoice.round_off | fmt.money }}</td></tr>{{ end }}
      <tr class="grand"><td>Total</td><td class="num">{{ invoice.grand_total | fmt.money }}</td></tr>
    </table>
  </div>
  <p><strong>Amount in words:</strong> {{ invoice.amount_in_words }}</p>
  {{ if invoice.remarks != "" }}<p><strong>Remarks:</strong> {{ invoice.remarks }}</p>{{ end }}
  <div class="sign">
    <small>This is a computer-generated invoice.</small>
    <div>For {{ company.name }}<br><br><br>Authorised signatory</div>
  </div>
</div>
""";

    public const string CustomerPo = """
<style>
  .doc { font-family: Arial, Helvetica, sans-serif; font-size: 12px; color: #111; }
  .head { display: flex; justify-content: space-between; border-bottom: 2px solid #111; padding-bottom: 8px; }
  .head h1 { margin: 0 0 4px; font-size: 20px; }
  .title { text-align: right; }
  .title h2 { margin: 0 0 6px; font-size: 16px; text-transform: uppercase; letter-spacing: 1px; }
  .label { color: #555; font-size: 10px; text-transform: uppercase; letter-spacing: .5px; margin: 8px 0 4px; }
  table { width: 100%; border-collapse: collapse; margin-top: 10px; }
  th { background: #f2f2f2; text-align: left; }
  th, td { border: 1px solid #ccc; padding: 4px 6px; }
  .num { text-align: right; }
  .totals { width: auto; margin-left: auto; }
  .totals td { border: 0; padding: 2px 6px; }
  .grand td { font-size: 14px; font-weight: bold; border-top: 1px solid #111; }
  .sign { display: flex; justify-content: space-between; margin-top: 40px; }
  .watermark { color: #c62828; font-weight: bold; text-align: center; font-size: 18px; }
</style>
<div class="doc">
  {{ if po.is_draft }}<p class="watermark">DRAFT</p>{{ end }}
  {{ if po.is_cancelled }}<p class="watermark">CANCELLED</p>{{ end }}
  <div class="head">
    <div>
      <h1>{{ company.name }}</h1>
      <div>{{ company.address_html }}</div>
      {{ if company.gstin != "" }}<div><strong>GSTIN:</strong> {{ company.gstin }}</div>{{ end }}
    </div>
    <div class="title">
      <h2>Order Acknowledgement</h2>
      <div>Our ref. <strong>{{ po.number }}</strong></div>
      <div>Your P.O. <strong>{{ po.customer_po_no }}</strong> dated {{ po.date }}</div>
      {{ if po.delivery_date != "" }}<div>Delivery by {{ po.delivery_date }}</div>{{ end }}
    </div>
  </div>
  <p class="label">Customer</p>
  <strong>{{ customer.name }}</strong>
  <div>{{ customer.address_html }}</div>
  {{ if customer.gstin != "" }}<div>GSTIN: {{ customer.gstin }}</div>{{ end }}
  <table>
    <thead>
      <tr><th>#</th><th>Item</th><th>HSN</th><th class="num">Qty</th><th class="num">Rate</th><th class="num">Amount</th>
        <th class="num">GST %</th><th class="num">GST</th><th class="num">Total</th></tr>
    </thead>
    <tbody>
      {{ for line in lines }}
      <tr>
        <td>{{ line.no }}</td><td>{{ line.name }}<br><small>{{ line.sku }}</small></td><td>{{ line.hsn }}</td>
        <td class="num">{{ line.quantity }} {{ line.unit }}</td><td class="num">{{ line.rate | fmt.money }}</td>
        <td class="num">{{ line.amount | fmt.money }}</td><td class="num">{{ line.tax_rate }}</td>
        <td class="num">{{ line.tax_amount | fmt.money }}</td><td class="num">{{ line.total | fmt.money }}</td>
      </tr>
      {{ end }}
    </tbody>
  </table>
  <table class="totals">
    <tr><td>Sub-total</td><td class="num">{{ po.sub_total | fmt.money }}</td></tr>
    <tr><td>GST</td><td class="num">{{ po.tax_total | fmt.money }}</td></tr>
    <tr class="grand"><td>Total</td><td class="num">{{ po.grand_total | fmt.money }}</td></tr>
  </table>
  <p><strong>Amount in words:</strong> {{ po.amount_in_words }}</p>
  {{ if po.remarks != "" }}<p><strong>Remarks:</strong> {{ po.remarks }}</p>{{ end }}
  <div class="sign">
    <small>We confirm the order above.</small>
    <div>For {{ company.name }}<br><br><br>Authorised signatory</div>
  </div>
</div>
""";

    public const string WorkOrder = """
<style>
  .doc { font-family: Arial, Helvetica, sans-serif; font-size: 12px; color: #111; }
  .head { display: flex; justify-content: space-between; border-bottom: 2px solid #111; padding-bottom: 8px; }
  .head h1 { margin: 0 0 4px; font-size: 20px; }
  .title { text-align: right; }
  .title h2 { margin: 0 0 6px; font-size: 16px; text-transform: uppercase; letter-spacing: 1px; }
  .facts { display: grid; grid-template-columns: 1fr 1fr; gap: 4px 24px; padding: 8px 0; border-bottom: 1px solid #ccc; }
  .label { color: #555; }
  table { width: 100%; border-collapse: collapse; margin-top: 10px; }
  th { background: #f2f2f2; text-align: left; }
  th, td { border: 1px solid #ccc; padding: 4px 6px; }
  .num { text-align: right; }
  .totals { width: auto; margin-left: auto; }
  .totals td { border: 0; padding: 2px 6px; }
  .sign { display: flex; justify-content: space-between; margin-top: 40px; }
  .watermark { color: #c62828; font-weight: bold; text-align: center; font-size: 18px; }
</style>
<div class="doc">
  {{ if work_order.is_cancelled }}<p class="watermark">CANCELLED</p>{{ end }}
  <div class="head">
    <div>
      <h1>{{ company.name }}</h1>
      <div>{{ company.address_html }}</div>
    </div>
    <div class="title">
      <h2>Work Order</h2>
      <div>No. <strong>{{ work_order.number }}</strong></div>
      <div>Date {{ work_order.date }} · {{ work_order.status }}</div>
    </div>
  </div>
  <div class="facts">
    <div><span class="label">Produce:</span> <strong>{{ work_order.item_name }}</strong> ({{ work_order.item_sku }})</div>
    <div><span class="label">Quantity:</span> <strong>{{ work_order.quantity }} {{ work_order.unit }}</strong></div>
    <div><span class="label">For:</span> {{ if work_order.customer_po_number != "" }}{{ work_order.customer_po_number }} · {{ work_order.customer_name }}{{ else }}Stock{{ end }}</div>
    <div><span class="label">Receive into:</span> {{ work_order.store }}</div>
    <div><span class="label">Materials from:</span> {{ work_order.material_store }}</div>
  </div>
  <table>
    <thead>
      <tr><th>#</th><th>Material</th><th class="num">Qty</th><th class="num">Rate</th><th class="num">Amount</th><th>Issued by</th></tr>
    </thead>
    <tbody>
      {{ for m in materials }}
      <tr>
        <td>{{ m.no }}</td><td>{{ m.name }}<br><small>{{ m.sku }}</small></td>
        <td class="num">{{ m.quantity }} {{ m.unit }}</td><td class="num">{{ m.rate | fmt.money }}</td>
        <td class="num">{{ m.amount | fmt.money }}</td><td></td>
      </tr>
      {{ end }}
    </tbody>
  </table>
  <table class="totals">
    <tr><td>Materials</td><td class="num">{{ work_order.material_cost | fmt.money }}</td></tr>
    <tr><td>Other cost</td><td class="num">{{ work_order.other_cost | fmt.money }}</td></tr>
    <tr><td>Total cost</td><td class="num">{{ work_order.total_cost | fmt.money }}</td></tr>
    <tr><td>Cost per unit</td><td class="num">{{ work_order.unit_cost | fmt.money }}</td></tr>
  </table>
  {{ if work_order.remarks != "" }}<p><strong>Instructions:</strong> {{ work_order.remarks }}</p>{{ end }}
  <div class="sign">
    <div>Produced by<br><br><br>________________</div>
    <div>Checked by<br><br><br>________________</div>
    <div>Approved by<br><br><br>________________</div>
  </div>
</div>
""";
}
