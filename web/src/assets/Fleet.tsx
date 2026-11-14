import { useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Copy, Empty, Err, Field, Modal, ago, fmtDate, useSubmit } from '../ui';
import { MODES } from './Hardware';
import { CountUp } from '../Rings';

/** Agents (installed on devices) and site collectors (inside a customer's network). */
export default function Fleet() {
  return <><h1>Agents &amp; collectors</h1><Agents /><Collectors /></>;
}

function Agents() {
  const tokens = useLoad<Rec[]>('/api/assets/enrolments'); const dash = useLoad<Rec>('/api/assets/dashboard'); const bin = useLoad<Rec>('/api/assets/recycle'); const [add, setAdd] = useState(false); const [show, setShow] = useState<Rec | null>(null);
  const tl = Array.isArray(tokens.data) ? tokens.data : []; const m = (dash.data?.all ?? {}) as Rec; const n = (v: unknown) => Number(v ?? 0);
  return <>
    <h2>Agents</h2>
    <div className="tools"><button className="primary" onClick={() => setAdd(true)}>New enrolment token</button><span className="muted">Run the install command on a computer to put an agent on it. It reports hardware and software and allows remote work.</span></div>
    {dash.data && <div className="agent-counts">
      {([['Live', n(m.agentOnline), 'online', 'ok'], ['Live, update due', n(m.agentUpdate), 'update', 'warn'], ['Not reporting', n(m.agentOffline), 'offline', 'bad'], ['No agent', n(m.agentNone), 'none', 'none']] as const).map(([l, v, f, tone]) =>
        <a key={f} className="co-tile agent-count" href={`#/assets/list?agent=${f}`}><span className="muted"><i className={`dot tone-${tone}`} aria-hidden="true" /> {l}</span><b><CountUp value={v} /></b><span className="open">Show these assets</span></a>)}
    </div>}
    <p className="muted">Each agent is shown with its asset: open a count to see the computers, then choose them to update, move to the recycle bin or uninstall their agents together.</p>
    <h2>Enrolment tokens</h2>
    {tl.length === 0 ? <Empty>No tokens.</Empty> : <table><thead><tr><th>Label</th><th>Customer</th><th>Token</th><th className="num">Used</th><th>Expires</th><th /></tr></thead><tbody>
      {tl.map((t) => <tr key={t.id}><td>{t.label}{t.monitorOnly && <> <Badge>monitor only</Badge></>}</td><td>{t.company || '(own)'}</td><td className="mono">{t.prefix}…</td><td className="num">{t.uses}</td><td>{t.revoked ? <Badge tone="bad">revoked</Badge> : t.expires ? fmtDate(t.expires) : 'never'}</td>
        <td>{!t.revoked && <><button onClick={async () => setShow(await api(`/api/assets/enrolments/${t.id}/scripts`))}>Install commands</button> <Act label="Revoke" danger run={async () => { await api(`/api/assets/enrolments/${t.id}`, { method: 'DELETE' }); tokens.reload(); }} /> </>}<Act label="Delete" confirm={`Move the token "${t.label || t.prefix}" to the recycle bin? It stops working at once; you can restore it for 30 days.`} run={async () => { await api(`/api/assets/enrolments/${t.id}/bin`, { method: 'POST', body: {} }); tokens.reload(); bin.reload(); }} /></td></tr>)}</tbody></table>}
    <RecycleBin bin={bin} onRestore={() => { dash.reload(); tokens.reload(); }} />
    {add && <TokenForm path="/api/assets/enrolments" title="New enrolment token" monitor onClose={(r) => { setAdd(false); if (r) { setShow(r); tokens.reload(); } }} />}
    {show && <Modal title="Install commands" onClose={() => setShow(null)}><Commands c={show.commands} /><div className="actions"><button onClick={() => setShow(null)}>Close</button></div></Modal>}
  </>;
}
function Commands({ c }: { c: Rec }) {
  return <div><p>Windows (elevated PowerShell):</p><Copy text={c.windows} /><p>Linux (as root):</p><Copy text={c.linux} />{c.upgradeWindows && <><p className="muted">Upgrade an existing install:</p><Copy text={c.upgradeWindows} /><Copy text={c.upgradeLinux} /></>}</div>;
}
function TokenForm({ path, title, monitor, onClose }: { path: string; title: string; monitor?: boolean; onClose: (r?: Rec) => void }) {
  const companies = useLoad<Rec[]>('/api/assets/companies'); const [co, setCo] = useState(''); const [label, setLabel] = useState(''); const [days, setDays] = useState(14); const [mo, setMo] = useState(false);
  const f = useSubmit(() => undefined); const [err, setErr] = useState('');
  return <Modal title={title} onClose={() => onClose()}><div className="form">
    <Field label="Customer"><select value={co} onChange={(e) => setCo(e.target.value)}><option value="">(own organisation)</option>{(Array.isArray(companies.data) ? companies.data : []).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
    <div className="row"><Field label="Label"><input value={label} onChange={(e) => setLabel(e.target.value)} /></Field><Field label="Valid for (days, 0 = no end)"><input type="number" value={days} onChange={(e) => setDays(Number(e.target.value))} /></Field></div>
    {monitor && <label><input type="checkbox" checked={mo} onChange={(e) => setMo(e.target.checked)} /> Monitor only (no remote commands, shell or remote control)</label>}
    <Err text={err || f.err} /><div className="actions"><button onClick={() => onClose()}>Cancel</button>
      <button className="primary" onClick={async () => { setErr(''); try { onClose(await api(path, { body: { companyId: co ? Number(co) : undefined, label, days, monitorOnly: monitor ? mo : undefined } })); } catch (e) { setErr((e as Error).message); } }}>Create</button></div></div></Modal>;
}

function Collectors() {
  const { data, reload } = useLoad<Rec>('/api/assets/collectors'); const [add, setAdd] = useState(false); const [show, setShow] = useState<Rec | null>(null); const [open, setOpen] = useState<Rec | null>(null);
  const list = (data?.items ?? []) as Rec[];
  return <>
    <h2>Site collectors</h2>
    <div className="tools"><button className="primary" onClick={() => setAdd(true)}>New collector token</button><span className="muted">A collector is a small service inside a customer's network. It finds devices, checks that they are up, runs service checks and reads details for devices that cannot have an agent.</span></div>
    {list.length === 0 ? <Empty>No collectors are installed yet.</Empty> : <table><thead><tr><th>Collector</th><th>Customer</th><th>Status</th><th className="num">Devices</th><th className="num">Found</th><th /></tr></thead><tbody>
      {list.map((c) => <tr key={c.id}><td>{c.name}<div className="muted">{c.hostname} {c.version}</div></td><td>{c.company || '(own)'}</td><td><Badge tone={c.online ? 'ok' : 'bad'}>{c.online ? 'online' : 'offline'}</Badge> {ago(c.lastSeen)}</td><td className="num">{c.devices}</td><td className="num">{c.found}</td><td><button onClick={() => setOpen(c)}>Manage</button></td></tr>)}</tbody></table>}
    {add && <TokenForm path="/api/assets/collectors/tokens" title="New collector token" onClose={(r) => { setAdd(false); if (r) setShow(r); }} />}
    {show && <Modal title="Collector install commands" onClose={() => setShow(null)}><Commands c={show.commands} /><div className="actions"><button onClick={() => setShow(null)}>Close</button></div></Modal>}
    {open && <Manage c={open} onClose={() => { setOpen(null); reload(); }} />}
  </>;
}

function Manage({ c, onClose }: { c: Rec; onClose: () => void }) {
  const [cidrs, setCidrs] = useState<string>(c.scanCidrs ?? ''); const [mode, setMode] = useState<string>(c.monitorDefault ?? 'ping'); const [name, setName] = useState<string>(c.name); const [auto, setAuto] = useState<boolean>(!!c.autoInstall); const [err, setErr] = useState('');
  const found = useLoad<Rec>(`/api/assets/collectors/${c.id}/found`); const log = useLoad<Rec>(`/api/assets/collectors/${c.id}/log`); const [pick, setPick] = useState<Set<string>>(new Set()); const [res, setRes] = useState('');
  useInterval(() => { found.reload(); log.reload(); }, 8000);
  const items = (found.data?.items ?? []) as Rec[];
  return <Modal title={`Collector ${c.name}`} onClose={onClose}><div className="form">
    <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} /></Field>
    <Field label="Ranges to scan (comma separated, each up to a /22)"><input value={cidrs} onChange={(e) => setCidrs(e.target.value)} placeholder="192.168.1.0/24, 10.0.5.0/24" /></Field>
    <Field label="Watch new devices by"><select value={mode} onChange={(e) => setMode(e.target.value)}>{MODES.filter(([k]) => k).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
    <label><input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} /> Install the agent automatically on Windows and Linux computers this controller finds</label>
    <p className="muted" style={{ marginTop: 0 }}>Each scan adds new computers as assets and installs the agent with the saved login for this customer (Assets &gt; Credentials). Printers, cameras and network devices are left alone. Without a saved login nothing is installed; the activity list says so.</p>
    <Err text={err} />
    <div className="tools"><Act label="Save settings" run={async () => { setErr(''); try { await api(`/api/assets/collectors/${c.id}`, { method: 'PATCH', body: { name, scanCidrs: cidrs, monitorDefault: mode, autoInstall: auto } }); } catch (e) { setErr((e as Error).message); throw e; } }} />
      <Act label="Scan now" primary run={async () => { await api(`/api/assets/collectors/${c.id}/scan`, { body: {} }); log.reload(); }} />
      <Act label="Install agents on all devices" run={async () => { const r = await api<Rec>(`/api/assets/collectors/${c.id}/agent-install-all`, { body: {} }); setRes(`${r.queued} devices queued.`); }} />
      <Act label="Move to recycle bin" danger confirm="Move this site controller to the recycle bin? It stops working at once and its devices are no longer watched. You can restore it, with its devices, for 30 days." run={async () => { await api(`/api/assets/collectors/${c.id}`, { method: 'DELETE' }); onClose(); }} /></div>
    {res && <p className="muted">{res}</p>}
    <h3>Found by scans ({items.length})</h3>
    {items.length === 0 ? <Empty>Nothing found yet. Set ranges and choose Scan now; the collector answers within a minute.</Empty> : <>
      <table><thead><tr><th /><th>Address</th><th>Name</th><th>Looks like</th></tr></thead><tbody>{items.map((h) => <tr key={h.ip}><td><input type="checkbox" disabled={h.known} checked={pick.has(h.ip)} onChange={() => setPick((s) => { const n = new Set(s); if (n.has(h.ip)) n.delete(h.ip); else n.add(h.ip); return n; })} aria-label={'Select ' + h.ip} /></td><td className="mono">{h.ip}</td><td>{h.hostname}</td><td>{[h.cat, h.sub].filter(Boolean).join(' / ')} {h.known && <Badge tone="ok">in assets</Badge>}</td></tr>)}</tbody></table>
      <div className="tools"><Act label={`Add ${pick.size} as assets`} primary disabled={pick.size === 0} run={async () => { const r = await api<Rec>(`/api/assets/collectors/${c.id}/adopt`, { body: { ips: [...pick] } }); setRes(`${r.added} added, ${r.skipped} skipped${r.limited ? `, ${r.limited} over the licence limit` : ''}.`); setPick(new Set()); found.reload(); }} /></div></>}
    <h3>Activity</h3>
    <Card>{log.data && ((log.data.items as Rec[]).length === 0 ? <Empty>No activity yet.</Empty> : <ul style={{ margin: 0, paddingLeft: 18, maxHeight: 180, overflow: 'auto' }}>{(log.data.items as Rec[]).map((l) => <li key={l.id}><span className="muted mono">{fmtDate(l.at)}</span> {l.level !== 'info' && <Badge tone={l.level === 'error' ? 'bad' : 'warn'}>{l.level}</Badge>} {l.text}</li>)}</ul>)}</Card>
    <div className="actions"><button onClick={onClose}>Close</button></div></div></Modal>;
}

function RecycleBin({ bin, onRestore }: { bin: { data: Rec | null; reload: () => void }; onRestore: () => void }) {
  const ag = (bin.data?.agents ?? []) as Rec[]; const tk = (bin.data?.tokens ?? []) as Rec[]; const cl = (bin.data?.collectors ?? []) as Rec[];
  if (ag.length + tk.length + cl.length === 0) return null;
  const row = (kind: string, id: string | number, what: string, when: string) => <tr key={kind + id}><td>{what}</td><td>{kind === 'agent' ? 'Agent' : kind === 'collector' ? 'Site controller' : 'Enrolment token'}</td><td>{fmtDate(when)}</td>
    <td><Act label="Restore" run={async () => { await api(`/api/assets/recycle/${kind}/${id}/restore`, { method: 'POST', body: {} }); bin.reload(); onRestore(); }} /> <Act label="Delete for good" danger confirm="Delete this for good? It cannot be restored." run={async () => { await api(`/api/assets/recycle/${kind}/${id}`, { method: 'DELETE' }); bin.reload(); }} /></td></tr>;
  return <>
    <h2>Recycle bin</h2>
    <p className="muted">Removed items are kept for 30 days, then deleted for good. A restored agent starts reporting again if its software is still on the computer; a restored site controller gets back the devices it watched.</p>
    <table><thead><tr><th>Item</th><th>Kind</th><th>Removed</th><th /></tr></thead><tbody>
      {ag.map((a) => row('agent', a.id, a.hostname || a.id, a.deletedAt))}{tk.map((t) => row('token', t.id, `${t.label || 'Token'} (${t.prefix}…)`, t.deletedAt))}{cl.map((c) => row('collector', c.id, `${c.name}${c.company ? ` (${c.company})` : ''}, watched ${c.devices} devices`, c.deletedAt))}</tbody></table>
  </>;
}
