import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Empty, Err, Field, Loading, Modal, fmtDay, useSubmit } from '../ui';

/** AMC (annual maintenance contracts) and rentals share one screen; AMC also has coverage, visits and SLA. */
export default function Contracts({ kind }: { kind: 'amc' | 'rentals' }) {
  const amc = kind === 'amc'; const path = `/api/assets/${kind}`;
  const { data, error, loading, reload } = useLoad<Rec[]>(path); const [edit, setEdit] = useState<Rec | null>(null); const list = Array.isArray(data) ? data : [];
  return <>
    <h1>{amc ? 'AMC contracts' : 'Rentals'}</h1>
    <div className="tools"><button className="primary" onClick={() => setEdit({ title: '', companyId: '', start: '', end: '', notes: '', coverage: '', visits: 0, slaPct: 0, assetIds: [] })}>{amc ? 'Add AMC' : 'Add rental'}</button></div>
    <Loading loading={loading && !data} error={error} />
    {data && (list.length === 0 ? <Empty>Nothing here yet.</Empty> : <table><thead><tr><th>Title</th><th>Customer</th><th>Period</th>{amc && <><th className="num">Visits</th><th className="num">SLA</th></>}<th className="num">Assets</th><th /></tr></thead><tbody>
      {list.map((c) => <tr key={c.id}><td>{c.title}<div className="muted">{c.coverage}</div></td><td>{c.company}</td><td>{fmtDay(c.start)} → {fmtDay(c.end)} {c.active === false && <Badge tone="bad">ended</Badge>}</td>{amc && <><td className="num">{c.visits}</td><td className="num">{c.slaPct ? c.slaPct + '%' : ''}</td></>}<td className="num">{(c.assetIds ?? []).length}</td>
        <td><button onClick={() => setEdit(c)}>Edit</button> <Act label="Delete" danger confirm={`Delete ${c.title}?`} run={async () => { await api(`${path}/${c.id}`, { method: 'DELETE' }); reload(); }} /></td></tr>)}</tbody></table>)}
    {edit && <Form amc={amc} path={path} init={edit} onClose={(ch) => { setEdit(null); if (ch) reload(); }} />}
  </>;
}
function Form({ amc, path, init, onClose }: { amc: boolean; path: string; init: Rec; onClose: (changed: boolean) => void }) {
  const [v, setV] = useState<Rec>({ ...init, companyId: init.companyId ?? '' }); const f = useSubmit(() => onClose(true)); const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const companies = useLoad<Rec[]>('/api/assets/companies'); const [q, setQ] = useState(''); const assets = useLoad<Rec>(`/api/assets?search=${encodeURIComponent(q)}&take=100`);
  const ids = new Set<number>(v.assetIds ?? []);
  const body = { title: v.title, companyId: v.companyId === '' ? null : Number(v.companyId), start: v.start || null, end: v.end || null, notes: v.notes, assetIds: [...ids], ...(amc ? { coverage: v.coverage, visits: Number(v.visits) || 0, slaPct: Number(v.slaPct) || 0 } : {}) };
  return <Modal title={init.id ? 'Edit' : 'Add'} onClose={() => onClose(false)}><div className="form">
    <Field label="Title"><input value={v.title} onChange={(e) => set('title', e.target.value)} autoFocus /></Field>
    <Field label="Customer"><select value={v.companyId} onChange={(e) => set('companyId', e.target.value)}><option value="">(none)</option>{(Array.isArray(companies.data) ? companies.data : []).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
    <div className="row"><Field label="Starts"><input type="date" value={fmtDay(v.start) === '–' ? '' : fmtDay(v.start)} onChange={(e) => set('start', e.target.value)} /></Field><Field label="Ends"><input type="date" value={fmtDay(v.end) === '–' ? '' : fmtDay(v.end)} onChange={(e) => set('end', e.target.value)} /></Field></div>
    {amc && <><Field label="Coverage"><textarea rows={2} value={v.coverage} onChange={(e) => set('coverage', e.target.value)} /></Field>
      <div className="row"><Field label="Visits per year"><input type="number" value={v.visits} onChange={(e) => set('visits', e.target.value)} /></Field><Field label="SLA %"><input type="number" value={v.slaPct} onChange={(e) => set('slaPct', e.target.value)} /></Field></div></>}
    <Field label="Notes"><textarea rows={2} value={v.notes} onChange={(e) => set('notes', e.target.value)} /></Field>
    <Field label={`Assets covered (${ids.size} selected)`}><input placeholder="Search to find assets" value={q} onChange={(e) => setQ(e.target.value)} /></Field>
    <div style={{ maxHeight: 160, overflow: 'auto', border: '1px solid var(--line)', borderRadius: 6, padding: 6 }}>
      {((assets.data?.items ?? []) as Rec[]).map((a) => <label key={a.id} style={{ display: 'block' }}><input type="checkbox" checked={ids.has(a.id)} onChange={() => { const n = new Set(ids); if (n.has(a.id)) n.delete(a.id); else n.add(a.id); set('assetIds', [...n]); }} /> {a.name} <span className="muted mono">{a.tag}</span></label>)}</div>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !String(v.title).trim()} onClick={() => f.submit(() => api(init.id ? `${path}/${init.id}` : path, { method: init.id ? 'PUT' : 'POST', body }))}>Save</button></div></div></Modal>;
}
