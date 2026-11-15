import { useState, type ReactNode } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, Loading, Modal, fmtDay, useSubmit } from '../ui';

const daysTo = (d?: string | null) => (d ? Math.ceil((new Date(d).getTime() - Date.now()) / 86400000) : null);
function Ends({ d }: { d?: string | null }) {
  const n = daysTo(d); if (n === null) return <span className="muted">–</span>;
  return <>{fmtDay(d)} {n < 0 ? <Badge tone="bad">ended</Badge> : n <= 30 ? <Badge tone="warn">{n} days</Badge> : null}</>;
}

/** Vendors: your shared suppliers and each customer's own, with their registrations and what was bought from them. */
export default function Vendors({ id }: { id?: string }) {
  if (id) return <VendorPage id={id} />;
  return <VendorList />;
}

function VendorList() {
  const companies = useLoad<Rec[]>('/api/assets/companies'); const cos = Array.isArray(companies.data) ? companies.data : [];
  const [co, setCo] = useState(''); const [q, setQ] = useState('');
  const list = useLoad<Rec>(`/api/assets/vendors${co && co !== 'shared' ? `?company=${co}` : ''}`, [co]);
  const items = ((list.data?.items ?? []) as Rec[]).filter((v) => (co !== 'shared' || v.companyId == null) && (!q || `${v.name} ${v.gst} ${v.pan} ${v.city} ${v.contactName}`.toLowerCase().includes(q.toLowerCase())));
  const [edit, setEdit] = useState<Rec | null>(null);
  const canAdd = !!list.data?.canManage;
  return <>
    <h1>Vendors</h1>
    <p className="muted">The suppliers assets and AMC contracts are bought from. Shared vendors serve every customer; a customer can also have its own.</p>
    <div className="tools">
      <input type="search" placeholder="Search name, GSTIN, PAN, city…" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search vendors" />
      {cos.length > 1 && <select value={co} onChange={(e) => setCo(e.target.value)} aria-label="Customer"><option value="">Every vendor</option><option value="shared">Shared vendors only</option>{cos.map((c) => <option key={c.id} value={c.id}>For {c.name}</option>)}</select>}
      {canAdd && <button className="primary" onClick={() => setEdit({ companyId: list.data?.manageAll ? null : ((list.data?.manageCompanies ?? []) as number[])[0] ?? null, remind: true, active: true, otherIds: [] })}>Add vendor</button>}
    </div>
    <Loading loading={list.loading && !list.data} error={list.error} />
    {list.data && (items.length === 0 ? <Empty>{q ? 'No vendor matches.' : 'No vendors yet.'}</Empty> : <div className="table-wrap"><table>
      <thead><tr><th>Vendor</th><th>For</th><th>GSTIN / VAT</th><th>Contact</th><th className="num">Assets</th><th className="num">AMC</th><th>Next warranty end</th></tr></thead>
      <tbody>{items.map((v) => <tr key={v.id} className={v.active === false ? 'muted' : ''}>
        <td><a href={`#/assets/vendors/${v.id}`}>{v.name}</a>{v.code && <span className="mono muted"> {v.code}</span>}{v.active === false && <> <Badge>inactive</Badge></>}<div className="muted small">{[v.city, v.state, v.country].filter(Boolean).join(', ')}</div></td>
        <td>{v.companyId == null ? <span className="muted">Every customer</span> : v.company}</td>
        <td className="mono">{v.gst || v.vat || <span className="muted">–</span>}</td>
        <td>{v.contactName}<div className="muted small">{v.contactEmail} {v.contactPhone}</div></td>
        <td className="num"><a href={`#/assets/list?vendor=${v.id}`}>{v.assets}</a></td><td className="num">{v.contracts}</td><td><Ends d={v.nextWarrantyEnd} /></td></tr>)}</tbody></table></div>)}
    {edit && <VendorForm init={edit} cos={cos} manageAll={!!list.data?.manageAll} manageCompanies={(list.data?.manageCompanies ?? []) as number[]} onClose={(id) => { setEdit(null); if (id) location.hash = `#/assets/vendors/${id}`; }} />}
  </>;
}

function VendorPage({ id }: { id: string }) {
  const d = useLoad<Rec>(`/api/assets/vendors/${id}`); const companies = useLoad<Rec[]>('/api/assets/companies'); const list = useLoad<Rec>('/api/assets/vendors');
  const [edit, setEdit] = useState(false);
  const v = d.data?.vendor as Rec | undefined; const assets = (d.data?.assets ?? []) as Rec[]; const amc = (d.data?.amc ?? []) as Rec[];
  if (!v) return <Loading loading error={d.error} />;
  const others = (v.otherIds ?? []) as Rec[];
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/assets/vendors">Vendors</a><span aria-hidden="true">›</span><span aria-current="page">{v.name}</span></nav>
    <div className="dash-head"><h1>{v.name}{v.active === false && <> <Badge>inactive</Badge></>}</h1>
      <div className="tools">{v.canEdit && <button className="primary" onClick={() => setEdit(true)}>Edit</button>}
        {v.canEdit && <Act label="Delete" danger confirm={`Delete ${v.name}?`} disabled={Number(v.assets) + Number(v.contracts) > 0} run={async () => { await api(`/api/assets/vendors/${id}`, { method: 'DELETE' }); location.hash = '#/assets/vendors'; }} />}</div></div>
    <div className="grid">
      <Card title="Details"><Dl rows={[['For', v.companyId == null ? 'Every customer (shared)' : String(v.company)], ['Code', v.code], ['Address', [v.address, v.city, v.state, v.postalCode, v.country].filter(Boolean).join(', ')], ['Website', v.website ? <a href={/^https?:/.test(String(v.website)) ? String(v.website) : 'https://' + v.website} target="_blank" rel="noreferrer">{v.website}</a> : ''], ['Payment terms', v.paymentTerms], ['Notes', v.notes]]} /></Card>
      <Card title="Contact and reminders"><Dl rows={[['Contact', v.contactName], ['Email', v.contactEmail], ['Phone', v.contactPhone], ['Renewal reminders to', v.remind ? (v.renewalEmail || v.contactEmail || <span className="muted">no address yet</span>) : <span className="muted">not mailed</span>]]} /></Card>
      <Card title="Registrations"><Dl rows={[['GSTIN', v.gst ? <span className="mono">{v.gst}</span> : ''], ['PAN', v.pan ? <span className="mono">{v.pan}</span> : ''], ['TAN', v.tan ? <span className="mono">{v.tan}</span> : ''], ['VAT / TRN', v.vat ? <span className="mono">{v.vat}</span> : ''], ['CIN', v.cin ? <span className="mono">{v.cin}</span> : ''],
        ...others.map((o) => [String(o.label), <span key={o.label} className="mono">{o.value}</span>] as [string, ReactNode])]} /></Card>
    </div>
    <Card title={`Assets bought from ${v.name} (${assets.length})`}>
      {assets.length === 0 ? <Empty>No assets yet. Choose this vendor on an asset's Edit form.</Empty> : <div className="table-wrap"><table>
        <thead><tr><th>Asset</th><th>Customer</th><th>Site</th><th>Bought</th><th>PO / invoice</th><th className="num">Cost</th><th>Warranty</th></tr></thead>
        <tbody>{assets.map((a) => <tr key={a.id}><td><a href={`#/assets/${a.id}`}>{a.name}</a> <span className="mono muted">{a.tag}</span></td><td>{a.company}</td><td>{a.site}</td><td>{fmtDay(a.purchaseDate)}</td>
          <td>{[a.poNumber, a.invoiceNumber].filter(Boolean).join(' / ')}</td><td className="num">{a.purchaseCost != null ? Number(a.purchaseCost).toLocaleString() : ''}</td>
          <td><Ends d={a.warrantyEnd} />{a.warrantyMonths ? <div className="muted small">{a.warrantyMonths} months</div> : null}</td></tr>)}</tbody></table></div>}
    </Card>
    <Card title={`AMC contracts (${amc.length})`}>
      {amc.length === 0 ? <Empty>No AMC contracts with this vendor. Add one under AMC.</Empty> : <table>
        <thead><tr><th>Contract</th><th>Customer</th><th>Period</th><th className="num">Assets</th><th className="num">Cost</th></tr></thead>
        <tbody>{amc.map((m) => <tr key={m.id}><td>{m.title}{m.contractNo && <span className="mono muted"> {m.contractNo}</span>}{m.previousId && <div className="muted small">renewal</div>}</td><td>{m.company}</td><td>{fmtDay(m.start)} → <Ends d={m.end} /></td><td className="num">{m.assets}</td><td className="num">{m.cost != null ? Number(m.cost).toLocaleString() : ''}</td></tr>)}</tbody></table>}
    </Card>
    {edit && <VendorForm init={v} cos={Array.isArray(companies.data) ? companies.data : []} manageAll={!!list.data?.manageAll} manageCompanies={(list.data?.manageCompanies ?? []) as number[]} onClose={(ch) => { setEdit(false); if (ch) d.reload(); }} />}
  </>;
}

function VendorForm({ init, cos, manageAll, manageCompanies, onClose }: { init: Rec; cos: Rec[]; manageAll: boolean; manageCompanies: number[]; onClose: (id?: number) => void }) {
  const [v, setV] = useState<Rec>({ name: '', code: '', address: '', city: '', state: '', country: '', postalCode: '', contactName: '', contactEmail: '', contactPhone: '', renewalEmail: '', website: '',
    gst: '', pan: '', tan: '', vat: '', cin: '', paymentTerms: '', notes: '', ...init, otherIds: [...((init.otherIds ?? []) as Rec[])] });
  const f = useSubmit(() => undefined); const [err, setErr] = useState('');
  const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const others = v.otherIds as Rec[];
  const forOptions = cos.filter((c) => manageAll || manageCompanies.includes(c.id));
  // a GSTIN holds the PAN (characters 3 to 12): fill it in when it is empty
  const onGst = (g: string) => { const up = g.toUpperCase().replace(/\s/g, ''); setV({ ...v, gst: up, ...(up.length >= 12 && !v.pan ? { pan: up.slice(2, 12) } : {}) }); };
  const save = async () => {
    setErr('');
    const body: Rec = { ...v }; delete body.id; for (const k of ['company', 'assets', 'contracts', 'nextWarrantyEnd', 'canEdit']) delete body[k];
    try {
      if (init.id) { await api(`/api/assets/vendors/${init.id}`, { method: 'PUT', body }); onClose(Number(init.id)); }
      else { const r = await api<Rec>('/api/assets/vendors', { body }); onClose(Number(r.id)); }
    } catch (e) { setErr((e as Error).message); }
  };
  return <Modal title={init.id ? `Edit ${init.name}` : 'Add vendor'} onClose={() => onClose()}><div className="form vendor-form">
    <div className="row"><Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} autoFocus /></Field><Field label="Code"><input value={v.code} onChange={(e) => set('code', e.target.value)} /></Field></div>
    {!init.id && <Field label="For"><select value={v.companyId ?? ''} onChange={(e) => set('companyId', e.target.value ? Number(e.target.value) : null)}>
      {manageAll && <option value="">Every customer (a supplier you buy from)</option>}{forOptions.map((c) => <option key={c.id} value={c.id}>{c.name} only</option>)}</select></Field>}
    <fieldset><legend>Address</legend>
      <Field label="Street"><textarea rows={2} value={v.address} onChange={(e) => set('address', e.target.value)} /></Field>
      <div className="row"><Field label="City"><input value={v.city} onChange={(e) => set('city', e.target.value)} /></Field><Field label="State"><input value={v.state} onChange={(e) => set('state', e.target.value)} /></Field>
        <Field label="Postal code"><input value={v.postalCode} onChange={(e) => set('postalCode', e.target.value)} /></Field><Field label="Country"><input value={v.country} onChange={(e) => set('country', e.target.value)} /></Field></div>
    </fieldset>
    <fieldset><legend>Government registrations</legend>
      <div className="row"><Field label="GSTIN"><input className="mono" value={v.gst} onChange={(e) => onGst(e.target.value)} placeholder="29ABCDE1234F1Z5" maxLength={15} /></Field>
        <Field label="PAN"><input className="mono" value={v.pan} onChange={(e) => set('pan', e.target.value.toUpperCase())} placeholder="ABCDE1234F" maxLength={10} /></Field>
        <Field label="TAN"><input className="mono" value={v.tan} onChange={(e) => set('tan', e.target.value.toUpperCase())} placeholder="BLRA12345B" maxLength={10} /></Field></div>
      <div className="row"><Field label="VAT / TRN"><input className="mono" value={v.vat} onChange={(e) => set('vat', e.target.value)} /></Field><Field label="CIN"><input className="mono" value={v.cin} onChange={(e) => set('cin', e.target.value.toUpperCase())} maxLength={25} /></Field></div>
      {others.map((o, i) => <div key={i} className="row"><Field label="Registration"><input value={o.label} onChange={(e) => set('otherIds', others.map((x, j) => j === i ? { ...x, label: e.target.value } : x))} placeholder="MSME (Udyam), IEC, EIN…" /></Field>
        <Field label="Number"><input className="mono" value={o.value} onChange={(e) => set('otherIds', others.map((x, j) => j === i ? { ...x, value: e.target.value } : x))} /></Field>
        <button type="button" className="small" onClick={() => set('otherIds', others.filter((_, j) => j !== i))}>Remove</button></div>)}
      <button type="button" className="small" onClick={() => set('otherIds', [...others, { label: '', value: '' }])}>Add another registration</button>
    </fieldset>
    <fieldset><legend>Contact</legend>
      <div className="row"><Field label="Contact person"><input value={v.contactName} onChange={(e) => set('contactName', e.target.value)} /></Field><Field label="Email"><input value={v.contactEmail} onChange={(e) => set('contactEmail', e.target.value)} /></Field><Field label="Phone"><input value={v.contactPhone} onChange={(e) => set('contactPhone', e.target.value)} /></Field></div>
      <div className="row"><Field label="Website"><input value={v.website} onChange={(e) => set('website', e.target.value)} /></Field><Field label="Payment terms"><input value={v.paymentTerms} onChange={(e) => set('paymentTerms', e.target.value)} placeholder="30 days from invoice" /></Field></div>
    </fieldset>
    <fieldset><legend>Renewal reminders</legend>
      <label className="check"><input type="checkbox" checked={v.remind !== false} onChange={(e) => set('remind', e.target.checked)} /> Mail this vendor one month and 15 days before a warranty or AMC from them ends</label>
      <Field label="Send them to (empty: the contact email)"><input value={v.renewalEmail} onChange={(e) => set('renewalEmail', e.target.value)} placeholder="renewals@vendor.com" /></Field>
    </fieldset>
    {init.id && <label className="check"><input type="checkbox" checked={v.active !== false} onChange={(e) => set('active', e.target.checked)} /> Active (an inactive vendor keeps its history but is not offered for new assets)</label>}
    <Field label="Notes"><textarea rows={2} value={v.notes} onChange={(e) => set('notes', e.target.value)} /></Field>
    <Err text={err || f.err} /><div className="actions"><button onClick={() => onClose()}>Cancel</button><button className="primary" disabled={!String(v.name).trim()} onClick={() => void save()}>Save</button></div>
  </div></Modal>;
}
