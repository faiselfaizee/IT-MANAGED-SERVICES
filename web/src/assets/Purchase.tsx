import { useMemo } from 'react';
import { useLoad, type Rec } from '../http';
import { Field } from '../ui';

export type Place = Rec & { id: number; companyId: number; parentId: number | null; level: 'country' | 'state' | 'site'; name: string; code: string };

/** "Bengaluru HQ (BLR01), Karnataka, India" */
export function placePath(all: Place[], id: number | null | undefined): string {
  const parts: string[] = []; let p = all.find((x) => x.id === id);
  while (p) { parts.push(p.level === 'site' && p.code ? `${p.name} (${p.code})` : p.name); const parent = p.parentId; p = all.find((x) => x.id === parent); }
  return parts.join(', ');
}

/** Adds a number of months to a date and takes off a day: a 36-month warranty bought on 15 Jan 2024 ends on 14 Jan 2027. */
export function warrantyEnd(purchase: string, months: number): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(purchase) || !Number.isFinite(months) || months <= 0) return '';
  const [y, m, d] = purchase.split('-').map(Number);
  const t = new Date(Date.UTC(y, m - 1 + months, 1));
  const last = new Date(Date.UTC(t.getUTCFullYear(), t.getUTCMonth() + 1, 0)).getUTCDate();
  t.setUTCDate(Math.min(d, last)); t.setUTCDate(t.getUTCDate() - 1);
  return t.toISOString().slice(0, 10);
}

/** Country, then state, then site: only this customer's. */
export function SitePicker({ companyId, value, onChange }: { companyId: number | null | undefined; value: number | null; onChange: (id: number | null) => void }) {
  const all = useLoad<Rec>(companyId ? `/api/assets/places?company=${companyId}` : null, [companyId]);
  const places = ((all.data?.items ?? []) as Place[]).filter((p) => p.active !== false || p.id === value);
  const sites = places.filter((p) => p.level === 'site');
  if (!companyId) return <Field label="Site"><select disabled><option>Choose the customer first</option></select></Field>;
  if (all.data && sites.length === 0) return <Field label="Site"><select disabled><option>No sites yet: add them under Assets › Locations</option></select></Field>;
  return <Field label="Site"><select value={value ?? ''} onChange={(e) => onChange(e.target.value ? Number(e.target.value) : null)}>
    <option value="">(no site)</option>
    {places.filter((p) => p.level === 'country').map((c) => <optgroup key={c.id} label={c.name}>
      {sites.filter((s) => s.parentId === c.id || places.some((st) => st.id === s.parentId && st.parentId === c.id))
        .map((s) => <option key={s.id} value={s.id}>{placePath(places, s.id).split(', ').slice(0, -1).join(', ')}</option>)}
    </optgroup>)}
  </select></Field>;
}

/** The vendors that fit this customer: its own and the shared ones. */
export function VendorPicker({ companyId, value, onChange, label = 'Bought from (vendor)' }: { companyId: number | null | undefined; value: number | null; onChange: (id: number | null) => void; label?: string }) {
  const all = useLoad<Rec>(`/api/assets/vendors${companyId ? `?company=${companyId}` : ''}`, [companyId]);
  const list = ((all.data?.items ?? []) as Rec[]).filter((v) => (v.companyId == null || v.companyId === companyId) && (v.active !== false || v.id === value));
  return <Field label={label}><select value={value ?? ''} onChange={(e) => onChange(e.target.value ? Number(e.target.value) : null)}>
    <option value="">(not recorded)</option>
    {list.map((v) => <option key={v.id} value={v.id}>{v.name}{v.companyId == null ? '' : ' (this customer)'}</option>)}
  </select></Field>;
}

const PERIODS = [12, 24, 36, 48, 60];

/** Site, vendor, order and warranty fields of an asset, shared by the add and edit forms. */
export function PurchaseFields({ v, set, companyId }: { v: Rec; set: (patch: Rec) => void; companyId: number | null | undefined }) {
  const months = Number(v.warrantyMonths) || 0;
  const computed = useMemo(() => warrantyEnd(String(v.purchaseDate ?? '').slice(0, 10), months), [v.purchaseDate, months]);
  return <>
    <div className="row"><SitePicker companyId={companyId} value={v.siteId ?? null} onChange={(id) => set({ siteId: id })} />
      <Field label="Room or area"><input value={v.location ?? ''} onChange={(e) => set({ location: e.target.value })} placeholder="2nd floor, server room" /></Field></div>
    <div className="row"><VendorPicker companyId={companyId} value={v.vendorId ?? null} onChange={(id) => set({ vendorId: id })} />
      <Field label="Purchase order"><input value={v.poNumber ?? ''} onChange={(e) => set({ poNumber: e.target.value })} /></Field>
      <Field label="Invoice"><input value={v.invoiceNumber ?? ''} onChange={(e) => set({ invoiceNumber: e.target.value })} /></Field></div>
    <div className="row">
      <Field label="Ownership"><select value={v.ownershipKind ?? ''} onChange={(e) => set({ ownershipKind: e.target.value })}>
        <option value="">(not recorded)</option><option value="owned">Owned</option><option value="rented">Rented</option><option value="leased">Leased</option></select></Field>
      <Field label="Hosted at" hint="Where it runs: head office, a data centre, AWS, Azure…"><input value={v.hostedAt ?? ''} onChange={(e) => set({ hostedAt: e.target.value })} maxLength={120} /></Field>
    </div>
    <div className="row">
      <Field label="Purchase date"><input type="date" value={String(v.purchaseDate ?? '').slice(0, 10)} onChange={(e) => { const end = warrantyEnd(e.target.value, months); set({ purchaseDate: e.target.value, ...(end ? { warrantyEnd: end } : {}) }); }} /></Field>
      <Field label="Warranty period"><select value={PERIODS.includes(months) || !months ? String(months || '') : 'other'} onChange={(e) => {
        const m = e.target.value === 'other' ? months || 1 : Number(e.target.value) || 0; const end = warrantyEnd(String(v.purchaseDate ?? '').slice(0, 10), m);
        set({ warrantyMonths: m || null, ...(end ? { warrantyEnd: end } : {}) });
      }}><option value="">(not recorded)</option>{PERIODS.map((p) => <option key={p} value={p}>{p / 12} year{p > 12 ? 's' : ''}</option>)}<option value="other">Other (months)</option></select></Field>
      {!!months && !PERIODS.includes(months) && <Field label="Months"><input type="number" min={1} max={240} value={months} onChange={(e) => { const m = Number(e.target.value) || 0; const end = warrantyEnd(String(v.purchaseDate ?? '').slice(0, 10), m); set({ warrantyMonths: m || null, ...(end ? { warrantyEnd: end } : {}) }); }} /></Field>}
      <Field label="Warranty ends"><input type="date" value={String(v.warrantyEnd ?? '').slice(0, 10)} onChange={(e) => set({ warrantyEnd: e.target.value })} /></Field>
    </div>
    {computed && v.warrantyEnd && computed !== String(v.warrantyEnd).slice(0, 10) && <p className="muted small">The period gives {computed}; the end date you typed is kept.</p>}
  </>;
}

/** The asset fields PurchaseFields edits, ready for the API. */
export function purchaseBody(v: Rec) {
  return { ownershipKind: v.ownershipKind ?? '', hostedAt: String(v.hostedAt ?? '').trim(), siteId: v.siteId ?? null, vendorId: v.vendorId ?? null, poNumber: v.poNumber ?? '', invoiceNumber: v.invoiceNumber ?? '', location: v.location ?? '',
    purchaseDate: v.purchaseDate ? String(v.purchaseDate).slice(0, 10) : null, warrantyEnd: v.warrantyEnd ? String(v.warrantyEnd).slice(0, 10) : null, warrantyMonths: v.warrantyMonths ? Number(v.warrantyMonths) : null };
}
