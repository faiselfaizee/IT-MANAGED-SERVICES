import { useState } from 'react';
import { api, download, go, openPage, saveFile, useLoad, type Rec } from '../http';
import { usePerms } from '../perms';
import { Act, Badge, Card, Empty, Err, Field, Loading, Modal, fmtDay, useSubmit } from '../ui';

const STATUSES = ['stock', 'in_use', 'repair', 'retired', 'lost'];
const label = (s: string) => s.replace('_', ' ');

export default function List() {
  const perms = usePerms('assets'); const [search, setSearch] = useState(''); const [status, setStatus] = useState(''); const [agent, setAgent] = useState(location.hash.match(/agent=(\w+)/)?.[1] ?? ''); const [skip, setSkip] = useState(0); const take = 50;
  const [sel, setSel] = useState<Set<number>>(new Set()); const [add, setAdd] = useState(false); const [print, setPrint] = useState(false);
  const q = `/api/assets?search=${encodeURIComponent(search)}&status=${status}&agent=${agent}&skip=${skip}&take=${take}`;
  const { data, error, loading, reload } = useLoad<Rec>(q);
  const sum = useLoad<Rec>('/api/assets/summary');
  const items = (data?.items ?? []) as Rec[]; const total = (data?.total ?? 0) as number;
  const toggle = (id: number) => setSel((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  return <>
    <h1>Assets</h1>
    {sum.data && <div className="grid" style={{ marginBottom: 14 }}>
      <Card title="Total"><b style={{ fontSize: 24 }}>{sum.data.total}</b> <span className="muted">{STATUSES.map((s) => `${sum.data?.byStatus?.[s] ?? 0} ${label(s)}`).join(' · ')}</span></Card>
      <Card title="Warranty"><Badge tone="warn">{sum.data.warrantyEndsIn30Days} end in 30 days</Badge> <Badge>{sum.data.warrantyEndsIn90Days} in 90 days</Badge> <Badge tone="bad">{sum.data.warrantyExpired} expired</Badge></Card>
    </div>}
    <div className="tools">
      <input placeholder="Search name, tag, serial, model, person…" value={search} onChange={(e) => { setSearch(e.target.value); setSkip(0); }} style={{ minWidth: 260 }} />
      <select value={status} onChange={(e) => { setStatus(e.target.value); setSkip(0); }} aria-label="Status"><option value="">Any status</option>{STATUSES.map((s) => <option key={s} value={s}>{label(s)}</option>)}</select>
      <select value={agent} onChange={(e) => { setAgent(e.target.value); setSkip(0); }} aria-label="Agent"><option value="">Any agent state</option><option value="online">Agent online</option><option value="offline">Agent offline</option><option value="update">Agent update due</option><option value="none">No agent</option></select>
      {perms.add && <button className="primary" onClick={() => setAdd(true)}>Add asset</button>}
      <button disabled={sel.size === 0} onClick={() => setPrint(true)}>QR labels ({sel.size})</button>
      <button onClick={() => download('/api/assets/export.csv', 'assets.csv')}>Export list (CSV)</button>
      <Act label="Export all details (CSV)" run={() => saveFile('/api/assets/export-full.csv', 'assets-full-details.csv')} />
    </div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No assets match.</Empty> : <table><thead><tr><th><input type="checkbox" aria-label="Select all" checked={items.every((i) => sel.has(i.id))} onChange={(e) => setSel(e.target.checked ? new Set([...sel, ...items.map((i) => i.id)]) : new Set([...sel].filter((x) => !items.some((i) => i.id === x))))} /></th>
      <th>Tag</th><th>Name</th><th>Type</th><th>Agent</th><th>Model</th><th>Assigned to</th><th>Location</th><th>Warranty</th></tr></thead><tbody>
      {items.map((a) => <tr key={a.id}><td><input type="checkbox" aria-label={'Select ' + a.name} checked={sel.has(a.id)} onChange={() => toggle(a.id)} /></td>
        <td className="mono">{a.tag}</td><td><a href={`#/assets/${a.id}`}>{a.name}</a></td><td>{a.type ?? ''}</td><td><AgentCell a={(a.agent ?? { state: 'none' }) as Rec} /></td>
        <td>{[a.manufacturer, a.model].filter(Boolean).join(' ')}</td><td>{a.assignedTo}</td><td>{a.location}</td><td>{fmtDay(a.warrantyEnd)}</td></tr>)}</tbody></table>)}
    <div className="tools" style={{ marginTop: 10 }}><button disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - take))}>Previous</button><span className="muted">{total ? `${skip + 1}–${Math.min(total, skip + take)} of ${total}` : ''}</span><button disabled={skip + take >= total} onClick={() => setSkip(skip + take)}>Next</button></div>
    {add && <AddAsset onClose={(id) => { setAdd(false); if (id) go(`assets/${id}`); else reload(); }} />}
    {print && <PrintDialog ids={[...sel]} onClose={() => setPrint(false)} />}
  </>;
}

/** Online, offline or no agent, with the version and a flag when an update is due. */
function AgentCell({ a }: { a: Rec }) {
  if (a.state === 'none') return <span className="muted">No agent</span>;
  const on = a.state === 'online';
  return <span className="agent-cell"><Badge tone={on ? 'ok' : 'bad'}>{on ? 'Online' : 'Offline'}</Badge>{' '}
    <span className="muted mono" title={a.lastSeen ? `Last seen ${new Date(String(a.lastSeen)).toLocaleString()}` : 'Never seen'}>{String(a.version || '')}</span>
    {a.updateDue && <>{' '}<Badge tone="warn">update due</Badge></>}</span>;
}

function AddAsset({ onClose }: { onClose: (id?: number) => void }) {
  const [v, setV] = useState<Rec>({ name: '', status: 'in_use', manufacturer: '', model: '', serial: '', location: '', assignedTo: '' }); const types = useLoad<Rec[]>('/api/assets/types');
  const f = useSubmit(() => undefined); const [err, setErr] = useState('');
  const set = (k: string, x: string) => setV({ ...v, [k]: x });
  return <Modal title="Add asset" onClose={() => onClose()}><div className="form">
    <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} autoFocus /></Field>
    <div className="row"><Field label="Type"><select value={v.typeId ?? ''} onChange={(e) => setV({ ...v, typeId: e.target.value ? Number(e.target.value) : undefined })}><option value="">(none)</option>{(Array.isArray(types.data) ? types.data : []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}</select></Field>
      <Field label="Status"><select value={v.status} onChange={(e) => set('status', e.target.value)}>{STATUSES.map((s) => <option key={s} value={s}>{label(s)}</option>)}</select></Field></div>
    <div className="row"><Field label="Manufacturer"><input value={v.manufacturer} onChange={(e) => set('manufacturer', e.target.value)} /></Field><Field label="Model"><input value={v.model} onChange={(e) => set('model', e.target.value)} /></Field></div>
    <div className="row"><Field label="Serial number"><input value={v.serial} onChange={(e) => set('serial', e.target.value)} /></Field><Field label="Location"><input value={v.location} onChange={(e) => set('location', e.target.value)} /></Field></div>
    <Field label="Assigned to"><input value={v.assignedTo} onChange={(e) => set('assignedTo', e.target.value)} /></Field>
    <Err text={err || f.err} /><div className="actions"><button onClick={() => onClose()}>Cancel</button>
      <button className="primary" disabled={!v.name.trim()} onClick={async () => { setErr(''); try { const r = await api<Rec>('/api/assets', { body: v }); onClose(r.id); } catch (e) { setErr((e as Error).message); } }}>Create</button></div></div></Modal>;
}

export function PrintDialog({ ids, onClose }: { ids: number[]; onClose: () => void }) {
  const [layout, setLayout] = useState('sheet'); const [content, setContent] = useState('details'); const [size, setSize] = useState('35'); const [text, setText] = useState(true);
  return <Modal title={`Print QR labels (${ids.length})`} onClose={onClose}><div className="form">
    <div className="row"><Field label="Layout"><select value={layout} onChange={(e) => setLayout(e.target.value)}><option value="sheet">A4 sheet, 10 × 10</option><option value="label">One label each</option></select></Field>
      <Field label="Content"><select value={content} onChange={(e) => setContent(e.target.value)}><option value="details">Details (compact)</option><option value="full">Full details</option><option value="link">Link to the asset</option></select></Field></div>
    {layout === 'label' && <Field label="Label size"><select value={size} onChange={(e) => setSize(e.target.value)}><option value="25">25 mm</option><option value="35">35 mm</option><option value="50">50 mm</option></select></Field>}
    <label><input type="checkbox" checked={text} onChange={(e) => setText(e.target.checked)} /> Print the tag under each code</label>
    <div className="actions"><button onClick={onClose}>Cancel</button><button className="primary" onClick={() => { void openPage(`/api/assets/qr/print?ids=${ids.join(',')}&layout=${layout}&content=${content}&size=${size}&text=${text ? 1 : 0}`); onClose(); }}>Open print page</button></div></div></Modal>;
}
