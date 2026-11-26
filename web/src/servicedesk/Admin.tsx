import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Card, Err, Loading, Modal, useSubmit } from '../ui';
import { PRIORITY } from './List';
import { FieldsAdmin } from './Fields';

const DAYS: [number, string][] = [[1, 'Mon'], [2, 'Tue'], [3, 'Wed'], [4, 'Thu'], [5, 'Fri'], [6, 'Sat'], [7, 'Sun']];

/** Categories and service levels. */
export function Settings() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/servicedesk/settings');
  const [cats, setCats] = useState(''); const [sla, setSla] = useState<Rec | null>(null); const [saved, setSaved] = useState(false);
  const f = useSubmit(() => { setSaved(true); reload(); });
  useEffect(() => { if (data) { setCats((data.categories as string[]).join('\n')); setSla(structuredClone(data.sla)); } }, [data]);
  if (!sla) return <><h1>Service Desk settings</h1><Loading loading={loading} error={error} /></>;
  const hrs = (sla.hours ?? { enabled: false, start: '09:00', end: '18:00', days: [1, 2, 3, 4, 5], zone: 'UTC' }) as Rec;
  const setHours = (patch: Rec) => { setSla({ ...sla, hours: { ...hrs, ...patch } }); setSaved(false); };
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
    <Card title="Working hours">
      <p className="muted">When this is on, the times above count only working hours. A ticket opened on Friday evening is then due on Monday, not on Saturday.</p>
      <label className="check"><input type="checkbox" checked={!!hrs.enabled} onChange={(e) => setHours({ enabled: e.target.checked })} /> Count working hours only</label>
      {hrs.enabled && <div className="form" style={{ marginTop: 8 }}>
        <div className="row">
          <label><span>Start</span><input type="time" value={hrs.start} onChange={(e) => setHours({ start: e.target.value })} aria-label="Start of the working day" /></label>
          <label><span>End</span><input type="time" value={hrs.end} onChange={(e) => setHours({ end: e.target.value })} aria-label="End of the working day" /></label>
          <label><span>Time zone</span><input value={hrs.zone} onChange={(e) => setHours({ zone: e.target.value })} aria-label="Time zone" placeholder="Asia/Kolkata" /></label>
        </div>
        <div className="tools" role="group" aria-label="Working days">{DAYS.map(([n, label]) => <label key={n} className="check"><input type="checkbox" checked={(hrs.days as number[]).includes(n)} onChange={(e) => setHours({ days: e.target.checked ? [...(hrs.days as number[]), n].sort() : (hrs.days as number[]).filter((d) => d !== n) })} /> {label}</label>)}</div>
      </div>}
    </Card>
    <CannedAdmin />
    <FieldsAdmin />
    <Err text={f.err} />
    <div className="actions"><span className="muted">{saved ? 'Saved. Open tickets use the new targets.' : ''}</span>
      <button className="primary" disabled={f.busy} onClick={() => f.submit(() => api('/api/servicedesk/settings', { method: 'PUT', body: { categories: cats.split('\n').map((c) => c.trim()).filter(Boolean), sla } }))}>Save settings</button></div>
  </>;
}


/** Canned replies: ready answers the team picks in the reply box. {requester}, {agent} and {number} are filled in from the ticket. */
function CannedAdmin() {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/servicedesk/canned?all=1');
  const [edit, setEdit] = useState<Rec | null>(null);
  const f = useSubmit(() => { setEdit(null); reload(); });
  const rows = Array.isArray(data) ? data : [];
  return <Card title="Canned replies">
    <p className="muted">Ready answers for the reply box. Use {'{requester}'}, {'{agent}'} and {'{number}'} where the name or ticket number should go.</p>
    <Loading loading={loading && !data} error={error} />
    {rows.length === 0 && !loading ? <p className="muted">No canned replies yet.</p> : <table><thead><tr><th>Title</th><th>Text</th><th /></tr></thead><tbody>
      {rows.map((c) => <tr key={c.id}><td>{c.title}{!c.active && <> <span className="badge warn">Off</span></>}</td><td className="muted">{String(c.body).slice(0, 90)}{String(c.body).length > 90 ? '…' : ''}</td>
        <td className="row-actions"><button onClick={() => setEdit({ ...c })}>Edit</button><Act label="Delete" danger confirm={`Delete “${c.title}”?`} run={() => api(`/api/servicedesk/canned/${c.id}`, { method: 'DELETE' }).then(reload)} /></td></tr>)}
    </tbody></table>}
    <div className="actions" style={{ justifyContent: 'flex-start' }}><button onClick={() => setEdit({ title: '', body: '', active: true })}>Add a canned reply</button></div>
    {edit && <Modal title={edit.id ? 'Edit canned reply' : 'New canned reply'} onClose={() => setEdit(null)}>
      <div className="form">
        <label><span>Title</span><input value={edit.title} maxLength={80} onChange={(e) => setEdit({ ...edit, title: e.target.value })} aria-label="Title" /></label>
        <label><span>Text</span><textarea rows={7} value={edit.body} maxLength={5000} onChange={(e) => setEdit({ ...edit, body: e.target.value })} aria-label="Text" /></label>
        <label className="check"><input type="checkbox" checked={edit.active} onChange={(e) => setEdit({ ...edit, active: e.target.checked })} /> Offer it in the reply box</label>
        <Err text={f.err} />
        <div className="actions"><button onClick={() => setEdit(null)}>Cancel</button>
          <button className="primary" disabled={f.busy || !edit.title.trim() || !edit.body.trim()} onClick={() => f.submit(() => api(edit.id ? `/api/servicedesk/canned/${edit.id}` : '/api/servicedesk/canned', { method: edit.id ? 'PUT' : 'POST', body: { title: edit.title, body: edit.body, active: edit.active } }))}>Save</button></div>
      </div>
    </Modal>}
  </Card>;
}
