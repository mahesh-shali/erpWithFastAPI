export const inr = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 2 });
export const rate = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 4 });
export const num = new Intl.NumberFormat('en-IN');
const dateFmt = new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', year: 'numeric' });

/** "2026-11-16" -> "16 Nov 2026" (no timezone shift). */
export const fmtDate = (iso: string) => {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number);
  return dateFmt.format(new Date(y, m - 1, d));
};
export const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};
export const signed = (n: number) => (n > 0 ? `+${num.format(n)}` : num.format(n));
export const errorText = (e: unknown, fallback = 'Something went wrong') => (e instanceof Error ? e.message : fallback);
