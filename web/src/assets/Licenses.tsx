import { useState } from 'react';
import { api, download, go, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, Loading, Modal, fmtDay, useSubmit } from '../ui';
import { VendorPicker } from './Purchase';

const KIND: Record<string, string> = { subscription: 'Subscription', perpetual: 'Perpetual', volume: 'Volume licence', oem: 'OEM', trial: 'Trial', 'open-source': 'Open source' };
const MODEL: Record<string, string> = { user: 'per user', device: 'per device', core: 'per core', site: 'site licence', concurrent: 'concurrent users' };
const BILL: Record<string, string> = { monthly: 'Monthly', yearly: 'Yearly', 'multi-year': 'Several years', 'one-time': 'One-time' };
const STATUS: Record<string, [string, 'ok' | 'warn' | 'bad' | undefined]> = {
  active: ['Active', 'ok'], expiring: ['Ends within 30 days', 'warn'], expired: ['Expired', 'bad'], renewed: ['Renewed', undefined], perpetual: ['Perpetual', 'ok'],
};
// common products, so the form fills itself
const PRESETS: Rec[] = [
  { product: 'Microsoft 365 Business Basic', publisher: 'Microsoft', kind: 'subscription', model: 'user', billing: 'yearly', match: '' },
  { product: 'Microsoft 365 Business Standard', publisher: 'Microsoft', kind: 'subscription', model: 'user', billing: 'yearly', match: 'Microsoft 365' },
  { product: 'Microsoft 365 Business Premium', publisher: 'Microsoft', kind: 'subscription', model: 'user', billing: 'yearly', match: 'Microsoft 365' },
  { product: 'Microsoft 365 Apps for business', publisher: 'Microsoft', kind: 'subscription', model: 'user', billing: 'yearly', match: 'Microsoft 365' },
  { product: 'Adobe Acrobat Pro', publisher: 'Adobe', kind: 'subscription', model: 'user', billing: 'yearly', match: 'Adobe Acrobat' },
  { product: 'Adobe Creative Cloud All Apps', publisher: 'Adobe', kind: 'subscription', model: 'user', billing: 'yearly', match: 'Adobe Creative Cloud' },
  { product: 'AutoCAD', publisher: 'Autodesk', kind: 'subscription', model: 'user', billing: 'yearly', match: 'AutoCAD' },
  { product: 'AutoCAD LT', publisher: 'Autodesk', kind: 'subscription', model: 'user', billing: 'yearly', match: 'AutoCAD LT' },
  { product: 'Windows Server Standard', publisher: 'Microsoft', kind: 'perpetual', model: 'core', billing: 'one-time', match: '' },
  { product: 'Windows Server CAL', publisher: 'Microsoft', kind: 'perpetual', model: 'user', billing: 'one-time', match: '' },
  { product: 'Antivirus / endpoint protection', publisher: '', kind: 'subscription', model: 'device', billing: 'yearly', match: '' },
];

function Seats({ l }: { l: Rec }) {
  const over = l.model !== 'site' && Math.max(Number(l.assigned), Number(l.installed)) > Number(l.seats);
  return <span className={over ? 'err' : ''} title={`${l.assigned} assigned, ${l.installed} installed`}>{String(l.model === 'site' ? '∞' : l.seats)} · {String(l.assigned)} assigned{l.match ? ` · ${l.installed} installed` : ''}{over ? ' · over' : ''}</span>;
}

function Form({ l, onClose, onSaved }: { l: Rec | null; onClose: () => void; onSaved: (id: number) => void }) {
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [f, setF] = useState<Rec>(l ? { ...l, licenseKey: undefined } : { companyId: 0, product: '', publisher: '', edition: '', kind: 'subscription', model: 'user', seats: 1, billing: 'yearly', currency: 'INR', autoRenew: false, match: '' });
  const set = (k: string, v: unknown) => setF({ ...f, [k]: v });
  const [key, setKey] = useState<string | null>(null);
  const s = useSubmit(() => undefined);
  const save = () => s.submit(async () => {
    const body: Rec = { ...f, cost: f.cost === '' || f.cost == null ? null : Number(f.cost), seats: Number(f.seats) };
    delete body.licenseKey; if (key !== null) body.licenseKey = key;
    const r = l ? await api<Rec>(`/api/assets/licenses/${l.id}`, { method: 'PUT', body }) : await api<Rec>('/api/assets/licenses', { method: 'POST', body });
    onSaved(Number(r.id)); onClose();
  });
  return <Modal title={l ? 'Edit software licence' : 'Add a software licence or subscription'} onClose={onClose}><div className="form">
    {!l && <Field label="Start from a common product"><select value="" onChange={(e) => { const p = PRESETS[Number(e.target.value)]; if (p) setF({ ...f, ...p }); }}><option value="">Choose…</option>{PRESETS.map((p, i) => <option key={p.product} value={i}>{p.product}</option>)}</select></Field>}
    <div className="grid2">
      <Field label="Customer"><select value={f.companyId} disabled={!!l} onChange={(e) => set('companyId', Number(e.target.value))}><option value={0}>Choose…</option>{((companies.data ?? []) as Rec[]).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
      <Field label="Product"><input value={f.product} onChange={(e) => set('product', e.target.value)} placeholder="Microsoft 365 Business Standard" /></Field>
      <Field label="Publisher"><input value={f.publisher} onChange={(e) => set('publisher', e.target.value)} /></Field>
      <Field label="Edition or plan"><input value={f.edition} onChange={(e) => set('edition', e.target.value)} /></Field>
      <Field label="Kind"><select value={f.kind} onChange={(e) => set('kind', e.target.value)}>{Object.entries(KIND).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></Field>
      <Field label="Licensed"><select value={f.model} onChange={(e) => set('model', e.target.value)}>{Object.entries(MODEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></Field>
      <Field label="Seats / quantity"><input type="number" min={0} value={f.seats} onChange={(e) => set('seats', e.target.value)} /></Field>
      <VendorPicker companyId={f.companyId || null} value={f.vendorId ?? null} onChange={(v) => set('vendorId', v)} label="Bought from (vendor)" />
      <Field label="Purchase date"><input type="date" value={f.purchaseDate ?? ''} onChange={(e) => set('purchaseDate', e.target.value)} /></Field>
      <Field label="Starts"><input type="date" value={f.startDate ?? ''} onChange={(e) => set('startDate', e.target.value)} /></Field>
      <Field label={f.kind === 'subscription' ? 'Ends / renews on' : 'Ends (empty = never)'}><input type="date" value={f.endDate ?? ''} onChange={(e) => set('endDate', e.target.value)} /></Field>
      <Field label="Billing"><select value={f.billing} onChange={(e) => set('billing', e.target.value)}>{Object.entries(BILL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></Field>
      <Field label="Cost per period"><input type="number" min={0} step="0.01" value={f.cost ?? ''} onChange={(e) => set('cost', e.target.value)} /></Field>
      <Field label="Currency"><input value={f.currency} maxLength={3} onChange={(e) => set('currency', e.target.value.toUpperCase())} /></Field>
      <Field label="PO number"><input value={f.poNumber ?? ''} onChange={(e) => set('poNumber', e.target.value)} /></Field>
      <Field label="Invoice number"><input value={f.invoiceNumber ?? ''} onChange={(e) => set('invoiceNumber', e.target.value)} /></Field>
      <Field label="Subscription or contract ID"><input value={f.contractNo ?? ''} onChange={(e) => set('contractNo', e.target.value)} /></Field>
      <Field label="Admin portal"><input value={f.portalUrl ?? ''} onChange={(e) => set('portalUrl', e.target.value)} placeholder="https://admin.microsoft.com" /></Field>
      <div className="wide"><Field label="Count installs of programs named like (from the software inventory)"><input value={f.match ?? ''} onChange={(e) => set('match', e.target.value)} placeholder="AutoCAD" /></Field></div>
      <div className="wide"><Field label={l?.hasKey ? 'Licence key (stored sealed; leave empty to keep)' : 'Licence key (stored sealed)'}><input value={key ?? ''} autoComplete="off" onChange={(e) => setKey(e.target.value)} /></Field></div>
      <label className="check wide"><input type="checkbox" checked={!!f.autoRenew} onChange={(e) => set('autoRenew', e.target.checked)} /> Renews automatically</label>
      <div className="wide"><Field label="Notes"><textarea rows={3} value={f.notes ?? ''} onChange={(e) => set('notes', e.target.value)} /></Field></div>
    </div>
    <Err text={s.err} /><div className="actions"><button className="primary" disabled={s.busy || !f.companyId || !f.product} onClick={() => void save()}>Save</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

function Renew({ l, onClose, onDone }: { l: Rec; onClose: () => void; onDone: (id: number) => void }) {
  const end = l.endDate ? new Date(l.endDate) : new Date(); const start = new Date(end.getTime() + 86400000);
  const next = new Date(start); if (l.billing === 'monthly') next.setMonth(next.getMonth() + 1); else next.setFullYear(next.getFullYear() + 1); next.setDate(next.getDate() - 1);
  const [s, setS] = useState(start.toISOString().slice(0, 10)); const [e, setE] = useState(next.toISOString().slice(0, 10));
  const [seats, setSeats] = useState(String(l.seats)); const [cost, setCost] = useState(l.cost == null ? '' : String(l.cost)); const [po, setPo] = useState(''); const [inv, setInv] = useState('');
  const f = useSubmit(() => undefined);
  return <Modal title={`Renew ${l.product}`} onClose={onClose}><div className="form">
    <p className="muted">The next period becomes a new record linked to this one, with the same seats given out. This one stops reminding.</p>
    <div className="grid2"><Field label="Starts"><input type="date" value={s} onChange={(x) => setS(x.target.value)} /></Field><Field label="Ends"><input type="date" value={e} onChange={(x) => setE(x.target.value)} /></Field>
      <Field label="Seats"><input type="number" min={0} value={seats} onChange={(x) => setSeats(x.target.value)} /></Field><Field label={'Cost' + (l.currency ? ` (${l.currency})` : '')}><input type="number" min={0} value={cost} onChange={(x) => setCost(x.target.value)} /></Field>
      <Field label="PO number"><input value={po} onChange={(x) => setPo(x.target.value)} /></Field><Field label="Invoice number"><input value={inv} onChange={(x) => setInv(x.target.value)} /></Field></div>
    <Err text={f.err} /><div className="actions"><button className="primary" disabled={f.busy} onClick={() => void f.submit(async () => { const r = await api<Rec>(`/api/assets/licenses/${l.id}/renew`, { method: 'POST', body: { startDate: s, endDate: e, seats: Number(seats), cost: cost === '' ? undefined : Number(cost), poNumber: po, invoiceNumber: inv } }); onDone(Number(r.id)); })}>Renew</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

function GiveSeat({ l, onClose, onDone }: { l: Rec; onClose: () => void; onDone: () => void }) {
  const [mode, setMode] = useState<'user' | 'asset'>(l.model === 'device' ? 'asset' : 'user'); const [user, setUser] = useState(''); const [q, setQ] = useState(''); const [asset, setAsset] = useState<number | null>(null); const [note, setNote] = useState('');
  const list = useLoad<Rec>(mode === 'asset' ? `/api/assets?search=${encodeURIComponent(q)}&company=${l.companyId}&take=50` : null, [q, mode]);
  const f = useSubmit(() => { onDone(); onClose(); });
  return <Modal title="Give a seat" onClose={onClose}><div className="form">
    <div className="tabs" role="tablist">{(['user', 'asset'] as const).map((k) => <button key={k} role="tab" aria-selected={mode === k} className={mode === k ? 'on' : ''} onClick={() => setMode(k)}>{k === 'user' ? 'To a person' : 'To a computer'}</button>)}</div>
    {mode === 'user' ? <Field label="Person (name or email)"><input value={user} onChange={(e) => setUser(e.target.value)} /></Field> : <>
      <Field label="Find the computer"><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name, tag or user" /></Field>
      <select size={6} aria-label="Computer" value={asset ?? ''} onChange={(e) => setAsset(Number(e.target.value))} style={{ width: '100%' }}>
        {((list.data?.items ?? []) as Rec[]).map((a) => <option key={a.id} value={a.id}>{a.tag} · {a.name}{a.assignedTo ? ` (${a.assignedTo})` : ''}</option>)}</select></>}
    <Field label="Note"><input value={note} onChange={(e) => setNote(e.target.value)} /></Field>
    <Err text={f.err} /><div className="actions"><button className="primary" disabled={f.busy || (mode === 'user' ? !user.trim() : !asset)} onClick={() => void f.submit(() => api(`/api/assets/licenses/${l.id}/seats`, { method: 'POST', body: mode === 'user' ? { user, note } : { assetId: asset, note } }))}>Give</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

export function LicenseDetail({ id }: { id: string }) {
  const d = useLoad<Rec>(`/api/assets/licenses/${id}`, [id]);
  const inst = useLoad<Rec[]>(`/api/assets/licenses/${id}/installed`, [id]);
  const [ask, setAsk] = useState<string | null>(null); const [key, setKey] = useState<string | null>(null);
  if (!d.data) return <Loading loading={d.loading} error={d.error} />;
  const l = d.data.license as Rec; const seats = (d.data.seats ?? []) as Rec[]; const canEdit = !!d.data.canEdit; const st = STATUS[l.status] ?? [l.status, undefined];
  return <>
    <p><a href="#/assets/licenses">← Software licences</a></p>
    <div className="dash-head"><h1>{l.product}{l.edition ? ` ${l.edition}` : ''}</h1><p><Badge tone={st[1]}>{st[0]}</Badge> {l.company}</p></div>
    {canEdit && <div className="toolbar"><button onClick={() => setAsk('edit')}>Edit</button>
      {l.endDate && !d.data.renewedBy && <button className="primary" onClick={() => setAsk('renew')}>Renew</button>}
      {d.data.renewedBy && <a href={`#/assets/licenses/${d.data.renewedBy}`}>Go to the renewal →</a>}
      <button onClick={() => setAsk('seat')}>Give a seat</button>
      {l.hasKey && <Act label={key === null ? 'Show licence key' : 'Hide licence key'} run={async () => { if (key !== null) { setKey(null); return; } const r = await api<Rec>(`/api/assets/licenses/${id}/key`); setKey(String(r.key)); }} />}
      <Act label="Delete" danger confirm={`Delete ${l.product}?`} run={async () => { await api(`/api/assets/licenses/${id}`, { method: 'DELETE' }); go('assets/licenses'); }} /></div>}
    {key !== null && <p>Licence key: <code className="copy" onClick={() => void navigator.clipboard?.writeText(key)} title="Click to copy">{key}</code> <span className="muted small">(this view is recorded in the audit log)</span></p>}
    <div className="grid two">
      <Card title="Licence"><Dl rows={[['Publisher', l.publisher], ['Kind', KIND[l.kind] ?? l.kind], ['Licensed', MODEL[l.model] ?? l.model], ['Seats', <Seats key="s" l={l} />], ['Vendor', l.vendorId ? <a key="v" href={`#/assets/vendors/${l.vendorId}`}>{l.vendor}</a> : '–'],
        ['Subscription / contract', l.contractNo || '–'], ['Admin portal', l.portalUrl ? <a key="p" href={l.portalUrl} target="_blank" rel="noreferrer">{l.portalUrl}</a> : '–']]} /></Card>
      <Card title="Purchase and renewal"><Dl rows={[['Purchased', fmtDay(l.purchaseDate)], ['Starts', fmtDay(l.startDate)], ['Ends', l.endDate ? fmtDay(l.endDate) : 'Never'], ['Billing', BILL[l.billing] ?? l.billing],
        ['Cost', l.cost == null ? '–' : `${Number(l.cost).toLocaleString()} ${l.currency}`], ['Auto-renew', l.autoRenew ? 'Yes' : 'No'], ['PO / invoice', [l.poNumber, l.invoiceNumber].filter(Boolean).join(' / ') || '–'],
        ['Earlier periods', ((d.data.history ?? []) as Rec[]).length ? ((d.data.history ?? []) as Rec[]).map((h) => <a key={h.id} href={`#/assets/licenses/${h.id}`} style={{ marginRight: 8 }}>{fmtDay(h.startDate)}–{fmtDay(h.endDate)}</a>) : '–']]} /></Card>
    </div>
    {l.notes && <Card title="Notes"><p style={{ whiteSpace: 'pre-wrap' }}>{l.notes}</p></Card>}
    <Card title={`Seats given (${seats.length} of ${l.model === 'site' ? 'unlimited' : l.seats})`}>
      {seats.length === 0 ? <Empty>No seats given yet.</Empty> : <table><thead><tr><th>To</th><th>Note</th><th>Since</th><th /></tr></thead><tbody>
        {seats.map((x) => <tr key={x.id}><td>{x.assetId ? <a href={`#/assets/${x.assetId}`}>{x.tag} · {x.asset}</a> : x.user}</td><td>{x.note}</td><td>{fmtDay(x.assignedAt)}</td>
          <td>{canEdit && <Act label="Take back" confirm="Take this seat back?" run={async () => { await api(`/api/assets/licenses/seats/${x.id}`, { method: 'DELETE' }); d.reload(); }} />}</td></tr>)}</tbody></table>}
    </Card>
    {l.match && <Card title={`Installed on (${(inst.data ?? []).length}) — programs named like “${l.match}”`}>
      {(inst.data ?? []).length === 0 ? <Empty>No computer of this customer reports it.</Empty> : <table><thead><tr><th>Computer</th><th>Program</th><th>Version</th><th>User</th></tr></thead><tbody>
        {((inst.data ?? []) as Rec[]).map((a) => <tr key={a.id}><td><a href={`#/assets/${a.id}/apps`}>{a.tag} · {a.hostname || a.name}</a></td><td>{a.program}</td><td>{a.version}</td><td>{a.user}</td></tr>)}</tbody></table>}
    </Card>}
    {ask === 'edit' && <Form l={l} onClose={() => setAsk(null)} onSaved={() => d.reload()} />}
    {ask === 'renew' && <Renew l={l} onClose={() => setAsk(null)} onDone={(nid) => { setAsk(null); go(`assets/licenses/${nid}`); }} />}
    {ask === 'seat' && <GiveSeat l={l} onClose={() => setAsk(null)} onDone={() => d.reload()} />}
  </>;
}

export default function Licenses() {
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [company, setCompany] = useState(''); const [status, setStatus] = useState(''); const [q, setQ] = useState(''); const [adding, setAdding] = useState(false);
  const url = `/api/assets/licenses?${company ? 'company=' + company + '&' : ''}${status ? 'status=' + status + '&' : ''}q=${encodeURIComponent(q)}`;
  const list = useLoad<Rec>(url, [url]);
  const rows = (list.data?.items ?? []) as Rec[];
  const counts = { active: 0, expiring: 0, expired: 0 } as Record<string, number>; rows.forEach((r) => { if (counts[r.status] !== undefined) counts[r.status]++; });
  return <>
    <div className="dash-head"><h1>Software licences and subscriptions</h1><p className="muted">What customers bought: Microsoft 365, Adobe, AutoCAD and the rest, with vendor, dates, seats and renewals.</p></div>
    <div className="tools">
      <input placeholder="Search product, publisher, vendor" value={q} onChange={(e) => setQ(e.target.value)} style={{ minWidth: 240 }} />
      <select aria-label="Customer" value={company} onChange={(e) => setCompany(e.target.value)}><option value="">All customers</option>{((companies.data ?? []) as Rec[]).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
      <select aria-label="Status" value={status} onChange={(e) => setStatus(e.target.value)}><option value="">Any status</option>{Object.entries(STATUS).map(([k, [l]]) => <option key={k} value={k}>{l}</option>)}</select>
      <button onClick={() => download(`/api/assets/licenses/export.csv${company ? '?company=' + company : ''}`, 'software-licences.csv')}>Export CSV</button>
      {list.data?.canEdit && <button className="primary" onClick={() => setAdding(true)}>Add a licence</button>}
    </div>
    {!status && rows.length > 0 && <p className="tools"><Badge tone="ok">{counts.active} active</Badge><Badge tone="warn">{counts.expiring} ending within 30 days</Badge><Badge tone="bad">{counts.expired} expired</Badge></p>}
    <Loading loading={list.loading && !list.data} error={list.error} />
    {list.data && (rows.length === 0 ? <Empty>No software licences recorded yet.</Empty> : <table><thead><tr><th>Product</th><th>Customer</th><th>Seats</th><th>Vendor</th><th>Ends</th><th>Status</th></tr></thead><tbody>
      {rows.map((l) => { const st = STATUS[l.status] ?? [l.status, undefined]; return <tr key={l.id}><td><a href={`#/assets/licenses/${l.id}`}><strong>{l.product}</strong>{l.edition ? ` ${l.edition}` : ''}</a><div className="muted small">{KIND[l.kind]} · {MODEL[l.model]}</div></td>
        <td>{l.company}</td><td className="small"><Seats l={l} /></td><td>{l.vendor || '–'}</td><td>{l.endDate ? fmtDay(l.endDate) : 'Never'}</td><td><Badge tone={st[1]}>{st[0]}</Badge></td></tr>; })}</tbody></table>)}
    {adding && <Form l={null} onClose={() => setAdding(false)} onSaved={(id) => go(`assets/licenses/${id}`)} />}
  </>;
}
