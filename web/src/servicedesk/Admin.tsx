import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Card, Err, Loading, useSubmit } from '../ui';
import { PRIORITY } from './List';
import { FieldsAdmin } from './Fields';

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
    <FieldsAdmin />
    <Err text={f.err} />
    <div className="actions"><span className="muted">{saved ? 'Saved. Open tickets use the new targets.' : ''}</span>
      <button className="primary" disabled={f.busy} onClick={() => f.submit(() => api('/api/servicedesk/settings', { method: 'PUT', body: { categories: cats.split('\n').map((c) => c.trim()).filter(Boolean), sla } }))}>Save settings</button></div>
  </>;
}

