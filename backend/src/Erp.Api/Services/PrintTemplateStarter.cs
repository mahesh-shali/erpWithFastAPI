namespace Erp.Api.Services;

/// <summary>Starting point for a new template: the built-in invoice layout written as a template, using every variable.</summary>
public static class PrintTemplateStarter
{
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
}
