import { useEffect, useRef, useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Copy, Empty, Err, Field, Loading, Modal, ago, fmtDate, useSubmit } from '../ui';
import { LiveLog } from './LiveLog';

const RISK: Record<string, 'ok' | 'warn' | 'bad'> = { safe: 'ok', caution: 'warn', disruptive: 'bad' };
const DONE = ['done', 'failed', 'timeout', 'cancelled'];

/** Why remote work is not possible right now, or '' when it is. */
function remoteBlock(d: Rec): string {
  if (!d.agent.id) return 'This device has no agent. Install the agent to run commands, open a shell or take remote control.';
  if (!d.remote.companyAllows) return 'Remote access is switched off for this customer (Companies).';
  if (!d.remote.assetAllows) return 'Remote access is switched off for this device (Overview).';
  if (!d.agent.online) return 'The agent is offline. Wait until it reports in.';
  return '';
}
const Blocked = ({ d }: { d: Rec }) => { const b = remoteBlock(d); return b ? <p className="note">{b}</p> : null; };

// ---------------------------------------------------------------- service checks
const emptyCheck = { name: '', type: 'tcp', host: '', port: 443, url: '', expect: '', status: '', insecure: false, dns: '', timeoutS: 10, enabled: true };

export function Checks({ id }: { id: string }) {
  const { data, error, loading, reload } = useLoad<Rec>(`/api/assets/${id}/checks`);
  const [edit, setEdit] = useState<Rec | null>(null);
  return <>
    <div className="tools"><button className="primary" onClick={() => setEdit({ ...emptyCheck })}>Add check</button>
      {data && !data.collector && <span className="muted">Checks run from a site collector. Choose one under Collectors and set this device to be monitored.</span>}</div>
    <Loading loading={loading && !data} error={error} />
    {data && (data.items.length === 0 ? <Empty>No service checks yet.</Empty> : <table><thead><tr><th>Name</th><th>Kind</th><th>Target</th><th>Result</th><th>When</th><th /></tr></thead><tbody>
      {(data.items as Rec[]).map((c) => <tr key={c.id}><td>{c.name}{!c.enabled && <Badge>off</Badge>}</td><td>{c.type}</td>
        <td className="mono">{c.type === 'http' ? c.url : c.type === 'dns' ? c.host : (c.host || 'device') + ':' + c.port}</td>
        <td>{c.lastOk === null ? <span className="muted">not run yet</span> : <><Badge tone={c.lastOk ? 'ok' : 'bad'}>{c.lastOk ? 'OK' : 'Failed'}</Badge> {c.lastMs ? Math.round(c.lastMs) + ' ms' : ''}{c.lastDays !== null ? ` · certificate ${Math.round(c.lastDays)} days` : ''}<div className="muted">{c.lastDetail}</div></>}</td>
        <td>{c.lastAt ? ago(c.lastAt) : ''}</td>
        <td><button onClick={() => setEdit(c)}>Edit</button> <Act label="Delete" danger confirm={`Delete the check "${c.name}"?`} run={async () => { await api(`/api/assets/checks/${c.id}`, { method: 'DELETE' }); reload(); }} /></td></tr>)}</tbody></table>)}
    {edit && <CheckForm id={id} init={edit} onClose={(ch) => { setEdit(null); if (ch) reload(); }} />}
  </>;
}
function CheckForm({ id, init, onClose }: { id: string; init: Rec; onClose: (changed: boolean) => void }) {
  const [c, setC] = useState<Rec>(init); const f = useSubmit(() => onClose(true));
  const set = (k: string, v: unknown) => setC({ ...c, [k]: v });
  return <Modal title={init.id ? 'Edit check' : 'New service check'} onClose={() => onClose(false)}><div className="form">
    <div className="row"><Field label="Name"><input value={c.name} onChange={(e) => set('name', e.target.value)} placeholder="Web site, RDP…" /></Field>
      <Field label="Kind"><select value={c.type} onChange={(e) => set('type', e.target.value)}><option value="tcp">Port open (TCP)</option><option value="http">Web page (HTTP)</option><option value="ssl">Certificate expiry</option><option value="dns">DNS answer</option></select></Field></div>
    {(c.type === 'tcp' || c.type === 'ssl') && <div className="row"><Field label="Port"><input type="number" value={c.port} onChange={(e) => set('port', Number(e.target.value))} /></Field><Field label="Host (empty = this device)"><input value={c.host} onChange={(e) => set('host', e.target.value)} /></Field></div>}
    {c.type === 'http' && <><Field label="Web address"><input value={c.url} onChange={(e) => set('url', e.target.value)} placeholder="https://…" /></Field>
      <div className="row"><Field label="Expected status (optional)"><input value={c.status} onChange={(e) => set('status', e.target.value)} placeholder="200" /></Field><Field label="Page must contain (optional)"><input value={c.expect} onChange={(e) => set('expect', e.target.value)} /></Field></div>
      <label><input type="checkbox" checked={c.insecure} onChange={(e) => set('insecure', e.target.checked)} /> Accept an untrusted certificate</label></>}
    {c.type === 'dns' && <div className="row"><Field label="Name to look up"><input value={c.host} onChange={(e) => set('host', e.target.value)} /></Field><Field label="DNS server (optional)"><input value={c.dns} onChange={(e) => set('dns', e.target.value)} /></Field></div>}
    <div className="row"><Field label="Timeout (seconds)"><input type="number" value={c.timeoutS} onChange={(e) => set('timeoutS', Number(e.target.value))} /></Field><label><input type="checkbox" checked={c.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Enabled</label></div>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy} onClick={() => f.submit(() => api(init.id ? `/api/assets/${id}/checks/${init.id}` : `/api/assets/${id}/checks`, { method: init.id ? 'PUT' : 'POST', body: c }))}>Save</button></div></div></Modal>;
}

// ---------------------------------------------------------------- tickets (real Service Desk tickets; old links when it is not licensed)
export function Tickets({ id, d }: { id: string; d?: Rec }) {
  const sd = useLoad<Rec>(`/api/servicedesk/tickets?assetId=${id}&take=100`); const [open, setOpen] = useState(false);
  if (sd.error && !sd.data) return <LegacyTickets id={id} />;
  const items = (sd.data?.items ?? []) as Rec[]; const active = items.filter((t) => !['resolved', 'closed'].includes(t.status)).length;
  return <>
    <div className="tools"><button className="primary" onClick={() => setOpen(true)}>Raise ticket</button>{sd.data && <span className="muted">{active} open of {sd.data.total}</span>}</div>
    <Loading loading={sd.loading && !sd.data} error="" />
    {sd.data && (items.length === 0 ? <Empty>No tickets for this asset.</Empty> : <table><thead><tr><th>Ticket</th><th>Subject</th><th>Priority</th><th>Status</th><th>Raised</th></tr></thead><tbody>
      {items.map((t) => <tr key={t.id}><td className="mono"><a href={`#/servicedesk/${t.id}`}>{t.number}</a></td><td>{t.subject}<div className="muted">{t.assignee ? `Assigned to ${t.assignee}` : 'Not assigned'}</div></td><td>{t.priority}</td>
        <td>{String(t.status).replace('_', ' ')}</td><td>{fmtDate(t.createdAt)} · {t.requester}</td></tr>)}</tbody></table>)}
    {open && <TicketForm id={id} d={d} onClose={(ch) => { setOpen(false); if (ch) sd.reload(); }} />}
  </>;
}
function TicketForm({ id, d, onClose }: { id: string; d?: Rec; onClose: (changed: boolean) => void }) {
  const [subject, setSubject] = useState(''); const [pri, setPri] = useState('medium'); const [desc, setDesc] = useState(''); const f = useSubmit(() => onClose(true));
  const companyId = d?.companyId ?? null;
  return <Modal title="Raise a ticket for this asset" onClose={() => onClose(false)}><div className="form">
    <Field label="Subject"><input value={subject} onChange={(e) => setSubject(e.target.value)} autoFocus /></Field>
    <Field label="Priority"><select value={pri} onChange={(e) => setPri(e.target.value)}>{['low', 'medium', 'high', 'critical'].map((p) => <option key={p}>{p}</option>)}</select></Field>
    <Field label="Details"><textarea rows={3} value={desc} onChange={(e) => setDesc(e.target.value)} /></Field>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !subject.trim()} onClick={() => f.submit(() => api('/api/servicedesk/tickets', { body: { subject, priority: pri, description: desc, assetId: Number(id), ...(companyId ? { companyId } : {}) } }))}>Raise</button></div></div></Modal>;
}

// ---------------------------------------------------------------- old links (kept while the Service Desk is not licensed)
function LegacyTickets({ id }: { id: string }) {
  const { data, error, loading, reload } = useLoad<Rec>(`/api/assets/${id}/tickets`); const [open, setOpen] = useState(false);
  return <>
    <div className="tools"><button className="primary" onClick={() => setOpen(true)}>Raise ticket</button>{data && <span className="muted">{data.openCount} open</span>}</div>
    <p className="note">The Service Desk is not part of this licence. Tickets raised here are kept as links on the asset.</p>
    <Loading loading={loading && !data} error={error} />
    {data && (data.items.length === 0 ? <Empty>No tickets for this asset.</Empty> : <table><thead><tr><th>Ticket</th><th>Title</th><th>Priority</th><th>Status</th><th>Raised</th></tr></thead><tbody>
      {(data.items as Rec[]).map((t) => <tr key={t.id}><td className="mono">{t.ref}</td><td>{t.title}<div className="muted">{t.note}</div></td><td>{t.priority}</td>
        <td><select value={t.status} onChange={async (e) => { await api(`/api/assets/tickets/${t.id}`, { method: 'PATCH', body: { status: e.target.value } }); reload(); }}>{['open', 'in_progress', 'resolved', 'closed'].map((s) => <option key={s} value={s}>{s.replace('_', ' ')}</option>)}</select></td>
        <td>{fmtDate(t.createdAt)} · {t.createdBy}</td></tr>)}</tbody></table>)}
    {open && <LegacyForm id={id} onClose={(ch) => { setOpen(false); if (ch) reload(); }} />}
  </>;
}
function LegacyForm({ id, onClose }: { id: string; onClose: (changed: boolean) => void }) {
  const [title, setTitle] = useState(''); const [pri, setPri] = useState('normal'); const [note, setNote] = useState(''); const f = useSubmit(() => onClose(true));
  return <Modal title="Raise a ticket" onClose={() => onClose(false)}><div className="form">
    <Field label="Title"><input value={title} onChange={(e) => setTitle(e.target.value)} autoFocus /></Field>
    <Field label="Priority"><select value={pri} onChange={(e) => setPri(e.target.value)}>{['low', 'normal', 'high', 'urgent'].map((p) => <option key={p}>{p}</option>)}</select></Field>
    <Field label="Details"><textarea rows={3} value={note} onChange={(e) => setNote(e.target.value)} /></Field>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !title.trim()} onClick={() => f.submit(() => api(`/api/assets/${id}/tickets`, { body: { title, priority: pri, note } }))}>Raise</button></div></div></Modal>;
}

// ---------------------------------------------------------------- troubleshooting commands
export function Commands({ id, d }: { id: string; d: Rec }) {
  const os = d.system.osFamily === 'linux' ? 'linux' : 'windows';
  const [q, setQ] = useState(''); const [cat, setCat] = useState(''); const [pick, setPick] = useState<Rec | null>(null); const [job, setJob] = useState<string | null>(null);
  const { data, error, loading } = useLoad<Rec>(`/api/assets/commands?q=${encodeURIComponent(q)}`);
  const jobs = useLoad<Rec[] | Rec>(`/api/assets/${id}/jobs`);
  const items = ((data?.items ?? []) as Rec[]).filter((c) => c.enabled && (c.os === 'any' || c.os === os) && (!cat || c.category === cat));
  const cats = [...new Set(((data?.items ?? []) as Rec[]).filter((c) => c.os === 'any' || c.os === os).map((c) => c.category as string))].sort();
  return <>
    <Blocked d={d} />
    <div className="tools"><input placeholder="Search commands" value={q} onChange={(e) => setQ(e.target.value)} /><select value={cat} onChange={(e) => setCat(e.target.value)} aria-label="Category"><option value="">All categories</option>{cats.map((c) => <option key={c}>{c}</option>)}</select><span className="muted">{items.length} commands for {os}</span></div>
    <Loading loading={loading && !data} error={error} />
    <table><thead><tr><th>Command</th><th>Category</th><th>Risk</th><th /></tr></thead><tbody>
      {items.map((c) => <tr key={c.id}><td><b>{c.title}</b><div className="muted">{c.description}</div></td><td>{c.category}</td><td><Badge tone={RISK[c.risk]}>{c.risk}</Badge></td><td><button disabled={!!remoteBlock(d)} onClick={() => setPick(c)}>Run…</button></td></tr>)}</tbody></table>
    <h2>Recent runs</h2>
    {Array.isArray(jobs.data) && (jobs.data.length === 0 ? <Empty>Nothing has been run on this device.</Empty> : <table><thead><tr><th>When</th><th>Command</th><th>By</th><th>Result</th><th /></tr></thead><tbody>
      {jobs.data.map((j: Rec) => <tr key={j.id}><td>{fmtDate(j.created)}</td><td>{j.title}</td><td>{j.by}</td><td><Badge tone={j.status === 'done' && !j.exit ? 'ok' : DONE.includes(j.status) ? 'bad' : undefined}>{j.status}</Badge></td><td><button onClick={() => setJob(j.id)}>Output</button></td></tr>)}</tbody></table>)}
    {pick && <RunCommand id={id} cmd={pick} onClose={(jid) => { setPick(null); if (jid) { setJob(jid); jobs.reload(); } }} />}
    {job && <JobView jid={job} onClose={() => { setJob(null); jobs.reload(); }} />}
  </>;
}
function RunCommand({ id, cmd, onClose }: { id: string; cmd: Rec; onClose: (jid?: string) => void }) {
  const ps: Rec[] = cmd.params ?? []; const [vals, setVals] = useState<Record<string, string>>(Object.fromEntries(ps.map((p) => [p.name, p.default ?? ''])));
  const [ok, setOk] = useState(cmd.risk !== 'disruptive'); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  return <Modal title={cmd.title} onClose={() => onClose()}><div className="form">
    <p>{cmd.description} <Badge tone={RISK[cmd.risk]}>{cmd.risk}</Badge></p>
    {ps.map((p) => <Field key={p.name} label={p.label || p.name}><input value={vals[p.name]} onChange={(e) => setVals({ ...vals, [p.name]: e.target.value })} /></Field>)}
    {cmd.risk === 'disruptive' && <label><input type="checkbox" checked={ok} onChange={(e) => setOk(e.target.checked)} /> I understand this can disrupt the user or the service</label>}
    <Err text={err} /><div className="actions"><button onClick={() => onClose()}>Cancel</button>
      <button className="primary" disabled={busy || !ok} onClick={async () => { setBusy(true); setErr(''); try { const r = await api<Rec>(`/api/assets/${id}/run`, { body: { commandId: cmd.id, params: vals, confirm: ok } }); onClose(r.id); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } }}>Run on device</button></div></div></Modal>;
}
export function JobView({ jid, onClose }: { jid: string; onClose: () => void }) {
  const { data, reload } = useLoad<Rec>(`/api/assets/jobs/${jid}`);
  const running = !data || !DONE.includes(data.status); useInterval(reload, running ? 2000 : null);
  return <Modal title={data ? data.title : 'Running…'} onClose={onClose}>
    {data && <p><Badge tone={data.status === 'done' && !data.exit ? 'ok' : DONE.includes(data.status) ? 'bad' : undefined}>{data.status}</Badge> {data.exit !== null && data.exit !== undefined ? `exit code ${data.exit}` : ''} <span className="muted">{data.shell}</span></p>}
    <pre className="term">{data?.output || (running ? 'Waiting for the agent…' : '(no output)')}</pre>
    <div className="actions">{running && data && <Act label="Cancel run" danger run={async () => { await api(`/api/assets/jobs/${jid}/cancel`, { body: {} }); reload(); }} />}<button onClick={onClose}>Close</button></div></Modal>;
}

// ---------------------------------------------------------------- live elevated shell
export function Shell({ id, d }: { id: string; d: Rec }) {
  const win = d.system.osFamily !== 'linux'; const shells = win ? ['cmd', 'powershell'] : ['bash'];
  const [shell, setShell] = useState(shells[0]); const [sid, setSid] = useState(''); const [out, setOut] = useState(''); const [closed, setClosed] = useState(false); const [line, setLine] = useState(''); const [err, setErr] = useState('');
  const seq = useRef(0); const box = useRef<HTMLPreElement>(null); const hist = useRef<string[]>([]); const hi = useRef(-1);
  useEffect(() => {
    if (!sid) return; let stop = false;
    (async () => { while (!stop) {
      try { const r = await api<Rec>(`/api/assets/shell/${sid}/output?since=${seq.current}&wait=20`); if (stop) break;
        if (r.chunks.length) { seq.current = r.seq; setOut((o) => (o + (r.chunks as Rec[]).map((c) => c.data).join('')).slice(-200000)); }
        if (r.closed) { setClosed(true); break; } } catch (e) { if (!stop) { setErr((e as Error).message); await new Promise((x) => setTimeout(x, 3000)); } } } })();
    return () => { stop = true; };
  }, [sid]);
  useEffect(() => { if (box.current) box.current.scrollTop = box.current.scrollHeight; }, [out]);
  const start = async () => { setErr(''); try { const r = await api<Rec>(`/api/assets/${id}/shell`, { body: { shell } }); seq.current = 0; setOut(''); setClosed(false); setSid(r.sid); } catch (e) { setErr((e as Error).message); } };
  const send = async () => { if (!sid || closed) return; const t = line; setLine(''); if (t.trim()) { hist.current.push(t); hi.current = hist.current.length; }
    try { await api(`/api/assets/shell/${sid}/input`, { body: { data: t + '\n' } }); } catch (e) { setErr((e as Error).message); } };
  return <>
    <Blocked d={d} />
    <p className="note">An elevated shell on the device. Everything you type is recorded in the audit log, and the shell closes after 30 minutes without input.</p>
    {!sid || closed ? <div className="tools"><select value={shell} onChange={(e) => setShell(e.target.value)} aria-label="Shell">{shells.map((s) => <option key={s}>{s}</option>)}</select><button className="primary" disabled={!!remoteBlock(d)} onClick={start}>{closed ? 'Open a new shell' : 'Open shell'}</button>{closed && <span className="muted">The shell was closed.</span>}</div> : null}
    {sid && <><pre className="term" ref={box} aria-label="Shell output">{out || 'Waiting for the shell to start…'}</pre>
      <div className="tools"><input style={{ flex: 1 }} className="mono" value={line} disabled={closed} placeholder="Type a command and press Enter" aria-label="Command line"
        onChange={(e) => setLine(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') void send(); else if (e.key === 'ArrowUp' && hist.current.length) { hi.current = Math.max(0, hi.current - 1); setLine(hist.current[hi.current] ?? ''); } else if (e.key === 'ArrowDown') { hi.current = Math.min(hist.current.length, hi.current + 1); setLine(hist.current[hi.current] ?? ''); } }} />
        <button disabled={closed} onClick={send}>Send</button><Act label="Close shell" danger disabled={closed} run={async () => { await api(`/api/assets/shell/${sid}/close`, { body: {} }); setClosed(true); }} /></div></>}
    <Err text={err} />
  </>;
}

// ---------------------------------------------------------------- PowerShell / script editor
export function Ise({ id, d }: { id: string; d: Rec }) {
  const win = d.system.osFamily !== 'linux'; const [shell, setShell] = useState(win ? 'powershell' : 'bash'); const [body, setBody] = useState(win ? 'Get-ComputerInfo | Select-Object CsName, OsName, OsVersion\n' : 'uname -a\n');
  const [timeout, setTimeoutS] = useState(300); const [job, setJob] = useState<string | null>(null); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  return <>
    <Blocked d={d} />
    <div className="tools"><select value={shell} onChange={(e) => setShell(e.target.value)} aria-label="Shell">{(win ? ['powershell', 'cmd'] : ['bash']).map((s) => <option key={s}>{s}</option>)}</select>
      <label>Time limit <input type="number" style={{ width: 80 }} value={timeout} onChange={(e) => setTimeoutS(Number(e.target.value))} /> s</label>
      <button className="primary" disabled={busy || !body.trim() || !!remoteBlock(d)} onClick={async () => { setBusy(true); setErr(''); try { const r = await api<Rec>(`/api/assets/${id}/run`, { body: { body, shell, timeout, script: true } }); setJob(r.id); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } }}>Run script</button></div>
    <textarea rows={16} value={body} onChange={(e) => setBody(e.target.value)} spellCheck={false} aria-label="Script" />
    <Err text={err} />{job && <JobView jid={job} onClose={() => setJob(null)} />}
  </>;
}

// ---------------------------------------------------------------- RustDesk remote control
export function RemoteControl({ id, d }: { id: string; d: Rec }) {
  const { data, error, reload } = useLoad<Rec>(`/api/assets/${id}/remote-control`); const log = useLoad<Rec>(`/api/assets/${id}/remote-control/log`);
  const [link, setLink] = useState<Rec | null>(null);
  const live = data && (data.state === 'pending'); useInterval(() => { reload(); log.reload(); }, live ? 1500 : null);
  if (!data) return <Loading loading error={error} />;
  const block = remoteBlock(d);
  return <>
    <Blocked d={d} />
    {!data.enabled && <p className="note">Remote control is not switched on for this installation. An administrator sets the RustDesk servers under Settings.</p>}
    {data.hasAgent && !data.windows && <p className="note">Remote control is available for Windows computers only for now.</p>}
    <Card title="RustDesk">
      <p className="muted" style={{ margin: 0 }}>Agent version {data.agentVersion || 'unknown'}</p>
      <p>State: <Badge tone={data.state === 'ready' ? 'ok' : data.state === 'failed' || data.state === 'stalled' ? 'bad' : undefined}>{data.state || 'not set up'}</Badge> {data.id && <span className="mono">ID {data.id}</span>} {data.mode && <span className="muted">({data.mode})</span>}</p>
      {data.msg && <p className="muted">{data.msg}</p>}
      <div className="tools">
        <Act label="Set up unattended access" disabled={!data.enabled || !data.windows || !!block} run={async () => { await api(`/api/assets/${id}/remote-control/setup`, { body: { mode: 'unattended' } }); reload(); log.reload(); }} />
        <Act label="Set up attended (user approves)" disabled={!data.enabled || !data.windows || !!block} run={async () => { await api(`/api/assets/${id}/remote-control/setup`, { body: { mode: 'attended' } }); reload(); log.reload(); }} />
        <Act label="Connect" primary disabled={data.state !== 'ready' || !!block} run={async () => { setLink(await api<Rec>(`/api/assets/${id}/remote-control/connect`, { body: { via: 'web' } })); }} />
        <Act label="Remove" danger disabled={!data.state || !data.hasAgent} confirm="Remove remote control from this computer?" run={async () => { await api(`/api/assets/${id}/remote-control/remove`, { body: {} }); reload(); }} />
      </div>
      {link && <div className="note"><p>Open this link on your computer (RustDesk must be installed there):</p><a href={link.link}>Open RustDesk session</a>{link.password && <p>Password: <Copy text={link.password} /></p>}<p className="muted">The session is recorded in the audit log.</p></div>}
    </Card>
    <Card title="Set-up progress">{log.data && <LiveLog items={log.data.items as Rec[]} running={data.state === 'pending'} empty="No activity yet. Press Set up to start; every step appears here as the agent does it." />}
      {data.state === 'pending' && !((log.data?.items ?? []) as Rec[]).some((x) => String(x.text).includes('picked up')) && <p className="note">The agent has not answered yet. It runs version {data.agentVersion || 'unknown'}; RustDesk set-up needs 2.9.0 or newer. An older agent ignores the request, so nothing starts on the computer. Check the agent version under Agents &amp; collectors and wait for it to update.</p>}
      {data.state === 'pending' && <p className="muted">Waiting for the agent on the computer. It starts within a few seconds when it is online; a download of about 25 MB follows.</p>}</Card>
  </>;
}
