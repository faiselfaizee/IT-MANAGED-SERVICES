import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Card, Empty, Err, Loading, useSubmit } from '../ui';
import { PRIORITY } from './List';

/** Categories and service levels. */
export function Settings() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/servicedesk/settings');
  const [cats, setCats] = useState(''); const [sla, setSla] = useState<Rec | null>(null); const [saved, setSaved] = useState(false);
  const f = useSubmit(() => { setSaved(true); reload(); });
  useEffect(() => { if (data) { setCats((data.categories as string[]).join('\n')); setSla(structuredClone(data.sla)); } }, [data]);
  if (!sla) return <><h1>Service Desk settings</h1><Loading loading={loading} error={error} /></>;
  const hours = (g: 'respond' | 'resolve', p: string, x: string) => setSla({ ...sla, [g]: { ...sla[g], [p]: Number(x) } });
  return <>
    <h1>Service Desk settings</h1>
    <Card title="Categories">
      <p className="muted">One per line. Tickets are sorted into these.</p>
      <textarea rows={7} value={cats} onChange={(e) => { setCats(e.target.value); setSaved(false); }} aria-label="Categories" />
    </Card>
    <Card title="Service levels">
      <p className="muted">Hours from opening a ticket. The clock stops while a ticket waits for the customer.</p>
      <table><thead><tr><th>Priority</th><th>First reply within (hours)</th><th>Resolved within (hours)</th></tr></thead><tbody>
        {Object.entries(PRIORITY).map(([k, n]) => <tr key={k}><td>{n}</td>
          <td><input type="number" min={0.25} step={0.25} value={sla.respond[k]} onChange={(e) => { hours('respond', k, e.target.value); setSaved(false); }} aria-label={`${n} first reply`} /></td>
          <td><input type="number" min={0.25} step={0.25} value={sla.resolve[k]} onChange={(e) => { hours('resolve', k, e.target.value); setSaved(false); }} aria-label={`${n} resolution`} /></td></tr>)}
      </tbody></table>
      <div className="form" style={{ marginTop: 12 }}>
        <label><span>Warn at (percent of the time used)</span><input type="number" min={10} max={99} value={sla.warnPct} onChange={(e) => { setSla({ ...sla, warnPct: Number(e.target.value) }); setSaved(false); }} /></label>
        <label><input type="checkbox" checked={sla.pause} onChange={(e) => { setSla({ ...sla, pause: e.target.checked }); setSaved(false); }} /> Stop the clock while waiting for the customer</label>
        <label><input type="checkbox" checked={sla.escalate} onChange={(e) => { setSla({ ...sla, escalate: e.target.checked }); setSaved(false); }} /> Record warnings and missed targets on the ticket</label>
      </div>
    </Card>
    <Err text={f.err} />
    <div className="actions"><span className="muted">{saved ? 'Saved. Open tickets use the new targets.' : ''}</span>
      <button className="primary" disabled={f.busy} onClick={() => f.submit(() => api('/api/servicedesk/settings', { method: 'PUT', body: { categories: cats.split('\n').map((c) => c.trim()).filter(Boolean), sla } }))}>Save settings</button></div>
  </>;
}

/** Which company each customer user belongs to, and what they may do. */
export function People() {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/servicedesk/people');
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [msg, setMsg] = useState('');
  const list = Array.isArray(data) ? data : []; const cos = Array.isArray(companies.data) ? companies.data : [];
  const save = async (p: Rec, change: Rec) => {
    setMsg('');
    try { await api(`/api/servicedesk/people/${p.id}`, { method: 'PUT', body: { companyId: p.companyId, mayRaise: p.mayRaise, mayModify: p.mayModify, ...change } }); reload(); } catch (e) { setMsg((e as Error).message); }
  };
  return <>
    <h1>Customer users</h1>
    <p className="muted">Add customer users on the Users page (role Customer or Customer administrator), then link each to their company here.</p>
    <Loading loading={loading && !data} error={error} /><Err text={msg} />
    {data && (list.length === 0 ? <Empty>No customer users yet.</Empty> : <table><thead><tr><th>Name</th><th>Company</th><th>May open tickets</th><th>May reply and close</th></tr></thead><tbody>
      {list.map((p) => <tr key={p.id}><td>{p.name}<div className="muted">{p.email}{p.active ? '' : ' · disabled'}</div></td>
        <td><select value={p.companyId ?? ''} aria-label={`Company of ${p.name}`} onChange={(e) => save(p, { companyId: e.target.value ? Number(e.target.value) : null })}><option value="">Not linked</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></td>
        <td><input type="checkbox" checked={p.mayRaise} aria-label={`${p.name} may open tickets`} onChange={(e) => save(p, { mayRaise: e.target.checked })} /></td>
        <td><input type="checkbox" checked={p.mayModify} aria-label={`${p.name} may reply`} onChange={(e) => save(p, { mayModify: e.target.checked })} /></td></tr>)}
    </tbody></table>)}
  </>;
}
