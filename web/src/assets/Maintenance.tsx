import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Badge, Card, Empty, Err, Field, Loading, Modal, ago } from '../ui';

/** Follows a remote action until the agent reports back, then hands over its result (status, text and the structured data it printed). */
async function follow(assetId: string, jid: string, onStep: (s: string) => void): Promise<Rec> {
  for (let i = 0; i < 360; i++) {
    const r = await api<Rec>(`/api/assets/${assetId}/remote/${jid}`);
    if (['done', 'failed', 'timeout', 'cancelled'].includes(String(r.status))) return r;
    onStep(r.status === 'running' ? 'The computer is working on it…' : 'Waiting for the agent…');
    await new Promise((ok) => setTimeout(ok, i < 10 ? 1500 : 4000));
  }
  throw new Error('No answer from the agent after about 24 minutes. Check Remote activity later.');
}

/** Asks for the reason a change needs; it is kept in the history and the audit log. */
function ReasonForm({ title, text, label, onClose, onGo }: { title: string; text?: string; label: string; onClose: () => void; onGo: (reason: string) => void }) {
  const [reason, setReason] = useState('');
  return <Modal title={title} onClose={onClose}><form className="form" onSubmit={(e) => { e.preventDefault(); if (reason.trim()) onGo(reason.trim()); }}>
    {text && <p className="muted">{text}</p>}
    <Field label="Reason"><input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} autoFocus /></Field>
    <p className="muted small">The reason is kept in the asset history and the audit log.</p>
    <div className="actions"><button type="button" onClick={onClose}>Cancel</button><button className="primary" type="submit" disabled={!reason.trim()}>{label}</button></div>
  </form></Modal>;
}

const SEV: Record<string, 'bad' | 'warn' | undefined> = { Critical: 'bad', Important: 'warn' };

/** Updates: look for them, then install all or the ticked ones, with a restart if the computer asks for it. */
export function PatchPanel({ assetId }: { assetId: string }) {
  const { data, error, loading, reload } = useLoad<Rec>(`/api/assets/${assetId}/patches`, [assetId]);
  const [step, setStep] = useState(''); const [msg, setMsg] = useState<{ text: string; bad?: boolean } | null>(null);
  const [pick, setPick] = useState<string[]>([]); const [ask, setAsk] = useState<null | 'all' | 'picked'>(null); const [restart, setRestart] = useState(false);
  const check = data?.check as Rec | null | undefined; const items = (Array.isArray(check?.items) ? check!.items : []) as Rec[];
  const win = data?.os === 'windows';
  const id = (u: Rec) => String(win ? u.kb : u.title);

  const run = async (path: string, body: Rec | undefined, done: string) => {
    setStep('Sending…'); setMsg(null);
    try {
      const { id: jid } = await api<Rec>(`/api/assets/${assetId}/patches/${path}`, { method: 'POST', body });
      const r = await follow(assetId, String(jid), setStep);
      setMsg({ text: String(r.output || (r.status === 'done' ? done : 'The action did not finish.')), bad: r.status !== 'done' });
      setPick([]); reload();
    } catch (e) { setMsg({ text: (e as Error).message, bad: true }); }
    finally { setStep(''); }
  };

  if (loading && !data) return <Loading loading error="" />;
  if (error) return <Err text={error} />;
  if (!data?.hasAgent) return <Empty>Updates are read and installed through the agent. Install the agent on this computer first.</Empty>;
  const can = !!data.canControl; const busy = step !== '';
  return <Card title="Updates">
    <div className="tools">
      {can && <button type="button" className="primary" disabled={busy} onClick={() => void run('check', undefined, 'Checked.')}>Check for updates</button>}
      {can && items.length > 0 && <button type="button" disabled={busy} onClick={() => setAsk('all')}>Install all</button>}
      {can && pick.length > 0 && <button type="button" disabled={busy} onClick={() => setAsk('picked')}>Install {pick.length} selected</button>}
      <span className="muted">{data.checkedAt ? `Checked ${ago(String(data.checkedAt))}.` : 'Not checked yet.'}{data.lastPatch ? ` Last update installed on ${String(data.lastPatch)}.` : ''}</span>
    </div>
    {step && <p role="status" className="muted">{step}</p>}
    {msg && <p role="status" className={msg.bad ? 'err' : 'ok-note'} style={{ whiteSpace: 'pre-wrap' }}>{msg.text}</p>}
    {check && <p>{Number(check.pending) === 0 ? <Badge tone="ok">Up to date</Badge> : <Badge tone="warn">{String(check.pending)} waiting</Badge>} {check.rebootRequired ? <Badge tone="warn">Restart needed</Badge> : null}</p>}
    {items.length > 0 && <div className="table-wrap"><table>
      <thead><tr>{can && <th><span className="sr-only">Select</span></th>}<th>Update</th><th>{win ? 'KB' : 'Version'}</th><th>Importance</th>{win && <th>Size</th>}</tr></thead>
      <tbody>{items.map((u, i) => { const key = id(u); const sev = String(u.severity ?? ''); return <tr key={i}>
        {can && <td><input type="checkbox" aria-label={`Select ${u.title}`} disabled={key === ''} checked={pick.includes(key)} onChange={(e) => setPick((p) => e.target.checked ? [...p, key] : p.filter((x) => x !== key))} /></td>}
        <td>{String(u.title)}{u.reboot ? <span className="muted small"> · restarts</span> : null}</td><td className="mono small">{String(win ? u.kb : u.version) || '–'}</td>
        <td>{sev ? <Badge tone={SEV[sev]}>{sev}</Badge> : <span className="muted">–</span>}</td>{win && <td>{Number(u.sizeMb) ? `${u.sizeMb} MB` : '–'}</td>}
      </tr>; })}</tbody></table></div>}
    {check && items.length === 0 && Number(check.pending) === 0 && <Empty>Nothing is waiting on this computer.</Empty>}
    {ask && <ReasonForm title={ask === 'all' ? 'Install all waiting updates' : `Install ${pick.length} update(s)`} label="Install"
      text="The computer downloads and installs the updates now. This can take a long time and some updates need a restart." onClose={() => setAsk(null)}
      onGo={(reason) => { const which = ask; setAsk(null); void run('install', { reason, restart, items: which === 'picked' ? pick : [] }, 'The updates are installed.'); }} />}
    {can && <label className="check" style={{ display: 'block', marginTop: '.6rem' }}><input type="checkbox" checked={restart} onChange={(e) => setRestart(e.target.checked)} /> Restart the computer after installing, if it needs one</label>}
  </Card>;
}

/** The busiest processes right now, for when an alert says the processor or memory is full. */
export function ProcessPanel({ assetId }: { assetId: string }) {
  const [step, setStep] = useState(''); const [err, setErr] = useState(''); const [data, setData] = useState<Rec | null>(null);
  const read = async () => {
    setStep('Sending…'); setErr('');
    try {
      const { id } = await api<Rec>(`/api/assets/${assetId}/processes`, { method: 'POST' });
      const r = await follow(assetId, String(id), setStep);
      if (r.status !== 'done' || !r.data) setErr(String(r.output || 'The computer could not read its processes.')); else setData(r.data as Rec);
    } catch (e) { setErr((e as Error).message); } finally { setStep(''); }
  };
  const Table = ({ rows, what }: { rows: Rec[]; what: 'cpu' | 'mem' }) => <div className="table-wrap"><table>
    <thead><tr><th>Program</th><th>{what === 'cpu' ? 'Processor' : 'Memory'}</th></tr></thead>
    <tbody>{rows.map((p, i) => <tr key={i}><td>{String(p.name)} <span className="muted small mono">{String(p.id)}</span></td><td>{what === 'cpu' ? `${p.cpu}%` : `${p.memMb} MB`}</td></tr>)}</tbody></table></div>;
  return <Card title="Busiest processes">
    <div className="tools"><button type="button" disabled={step !== ''} onClick={() => void read()}>Read now</button>
      <span className="muted">Takes a few seconds: the computer measures its processor use over three seconds.</span></div>
    {step && <p role="status" className="muted">{step}</p>}<Err text={err} />
    {data && <div className="row">
      <div><h3>By processor{data.cores ? <span className="muted small"> ({String(data.cores)} cores, share of the whole computer)</span> : null}</h3><Table rows={(data.cpu ?? []) as Rec[]} what="cpu" /></div>
      <div><h3>By memory</h3><Table rows={(data.mem ?? []) as Rec[]} what="mem" /></div></div>}
  </Card>;
}

/** Windows print server: queues, ports, drivers, clear a queue or every stuck job, restart the spooler. */
export function PrintPanel({ assetId, can }: { assetId: string; can: boolean }) {
  const [step, setStep] = useState(''); const [msg, setMsg] = useState<{ text: string; bad?: boolean } | null>(null); const [data, setData] = useState<Rec | null>(null);
  const [ask, setAsk] = useState<null | { action: string; printer?: string; title: string; text: string }>(null);
  const run = async (body: Rec) => {
    setStep('Sending…'); setMsg(null);
    try {
      const { id } = await api<Rec>(`/api/assets/${assetId}/printserver`, { method: 'POST', body });
      const r = await follow(assetId, String(id), setStep);
      if (body.action === 'list') { if (r.status === 'done' && r.data) setData(r.data as Rec); else setMsg({ text: String(r.output || 'The computer could not read its print queues.'), bad: true }); }
      else { setMsg({ text: String(r.output || (r.status === 'done' ? 'Done.' : 'The action did not finish.')), bad: r.status !== 'done' }); if (r.status === 'done') void run({ action: 'list' }); }
    } catch (e) { setMsg({ text: (e as Error).message, bad: true }); } finally { setStep(''); }
  };
  const queues = (data?.queues ?? []) as Rec[]; const ports = (data?.ports ?? []) as Rec[]; const drivers = (data?.drivers ?? []) as Rec[];
  return <Card title="Print server">
    <div className="tools">
      <button type="button" disabled={step !== ''} onClick={() => void run({ action: 'list' })}>Read print queues</button>
      {can && <button type="button" disabled={step !== ''} onClick={() => setAsk({ action: 'clear-all', title: 'Clear all stuck print jobs', text: 'Stops the print spooler, deletes every waiting job on this computer and starts it again.' })}>Clear all stuck jobs</button>}
      {can && <button type="button" disabled={step !== ''} onClick={() => setAsk({ action: 'restart-spooler', title: 'Restart the print spooler', text: 'Jobs being printed right now may have to be sent again.' })}>Restart spooler</button>}
      {data && <span className="muted">Print spooler: <b>{String(data.spooler)}</b></span>}
    </div>
    {step && <p role="status" className="muted">{step}</p>}
    {msg && <p role="status" className={msg.bad ? 'err' : 'ok-note'} style={{ whiteSpace: 'pre-wrap' }}>{msg.text}</p>}
    {data && (queues.length === 0 ? <Empty>This computer has no print queues.</Empty> : <div className="table-wrap"><table>
      <thead><tr><th>Queue</th><th>Driver</th><th>Port</th><th>Status</th><th>Jobs</th>{can && <th><span className="sr-only">Actions</span></th>}</tr></thead>
      <tbody>{queues.map((q, i) => <tr key={i}><td><b>{String(q.name)}</b>{q.shared ? <div className="muted small">Shared as {String(q.shareName || q.name)}</div> : null}</td>
        <td className="small">{String(q.driver)}</td><td className="small mono">{String(q.port)}</td>
        <td><Badge tone={String(q.status) === 'Normal' ? 'ok' : 'warn'}>{String(q.status)}</Badge></td><td>{String(q.jobs)}</td>
        {can && <td>{Number(q.jobs) > 0 && <button type="button" disabled={step !== ''} onClick={() => setAsk({ action: 'clear', printer: String(q.name), title: `Clear the queue of ${q.name}`, text: `Removes ${q.jobs} waiting job(s).` })}>Clear queue</button>}</td>}</tr>)}</tbody></table></div>)}
    {data && ports.length > 0 && <details><summary>{ports.length} port(s)</summary><div className="table-wrap"><table><thead><tr><th>Port</th><th>Address</th><th>TCP port</th></tr></thead>
      <tbody>{ports.map((p, i) => <tr key={i}><td className="mono small">{String(p.name)}</td><td>{String(p.host || '–')}</td><td>{p.port ? String(p.port) : '–'}</td></tr>)}</tbody></table></div></details>}
    {data && drivers.length > 0 && <details><summary>{drivers.length} driver(s)</summary><ul>{drivers.map((d, i) => <li key={i}>{String(d.name)}{d.manufacturer ? <span className="muted small"> · {String(d.manufacturer)}</span> : null}</li>)}</ul></details>}
    {ask && <ReasonForm title={ask.title} text={ask.text} label="Go ahead" onClose={() => setAsk(null)} onGo={(reason) => { const a = ask; setAsk(null); void run({ action: a.action, printer: a.printer, reason }); }} />}
  </Card>;
}

/** The Maintenance tab of a computer with the agent. */
export default function Maintenance({ id, os, canModify }: { id: string; os: string; canModify: boolean }) {
  return <>
    <PatchPanel assetId={id} />
    <ProcessPanel assetId={id} />
    {os === 'windows' && <PrintPanel assetId={id} can={canModify} />}
  </>;
}
