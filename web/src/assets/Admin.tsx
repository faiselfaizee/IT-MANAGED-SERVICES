import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, Modal, fmtDate, useSubmit } from '../ui';
import { JobView } from './DetailOps';

/** Everything that was run remotely, across all devices. */
export function RemoteLog() {
  const [days, setDays] = useState(30); const { data, error, loading } = useLoad<Rec>(`/api/assets/remote-log?days=${days}`); const [job, setJob] = useState<string | null>(null);
  return <>
    <h1>Remote activity</h1>
    <div className="tools"><select value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label="Period">{[1, 7, 30, 90, 365].map((d) => <option key={d} value={d}>Last {d} day{d > 1 ? 's' : ''}</option>)}</select></div>
    <Loading loading={loading && !data} error={error} />
    {data && (data.items.length === 0 ? <Empty>Nothing was run in this period.</Empty> : <table><thead><tr><th>When</th><th>Device</th><th>Customer</th><th>What</th><th>By</th><th>Result</th><th /></tr></thead><tbody>
      {(data.items as Rec[]).map((r) => <tr key={r.id}><td className="mono">{fmtDate(r.at)}</td><td><a href={`#/assets/${r.assetId}/commands`}>{r.asset}</a></td><td>{r.company}</td><td>{r.title}<div className="muted mono" style={{ maxHeight: 60, overflow: 'hidden' }}>{r.what}</div></td><td>{r.who}</td><td><Badge tone={r.status === 'done' && !r.exit ? 'ok' : undefined}>{r.status}</Badge></td><td><button onClick={() => setJob(r.id)}>Output</button></td></tr>)}</tbody></table>)}
    {job && <JobView jid={job} onClose={() => setJob(null)} />}
  </>;
}

/** The troubleshooting command repository (what the Commands tab offers). */
export function Repository() {
  const [q, setQ] = useState(''); const { data, error, loading, reload } = useLoad<Rec>(`/api/assets/commands?q=${encodeURIComponent(q)}`); const [edit, setEdit] = useState<Rec | null>(null);
  const blank = { title: '', description: '', category: 'General', os: 'windows', shell: 'powershell', body: '', risk: 'safe', params: [], timeout: 120 };
  return <>
    <h1>Troubleshooting command repository</h1>
    <div className="tools"><input placeholder="Search commands" value={q} onChange={(e) => setQ(e.target.value)} /><button className="primary" onClick={() => setEdit({ ...blank })}>Add command</button><span className="muted">{data?.count ?? 0} commands. Use {'{{name}}'} in the text for values asked when it runs.</span></div>
    <Loading loading={loading && !data} error={error} />
    {data && <table><thead><tr><th>Command</th><th>System</th><th>Shell</th><th>Risk</th><th /></tr></thead><tbody>
      {(data.items as Rec[]).map((c) => <tr key={c.id} style={c.enabled ? undefined : { opacity: 0.5 }}><td><b>{c.title}</b> {c.builtin && <Badge>built in</Badge>}<div className="muted">{c.category} · {c.description}</div></td><td>{c.os}</td><td>{c.shell}</td><td><Badge tone={c.risk === 'safe' ? 'ok' : c.risk === 'caution' ? 'warn' : 'bad'}>{c.risk}</Badge></td>
        <td><button onClick={() => setEdit(c)}>Edit</button> <Act label={c.enabled ? (c.builtin ? 'Disable' : 'Delete') : 'Enable'} danger={c.enabled} run={async () => { if (c.enabled) await api(`/api/assets/commands/${c.id}`, { method: 'DELETE' }); else await api(`/api/assets/commands/${c.id}`, { method: 'PUT', body: { enabled: true } }); reload(); }} /></td></tr>)}</tbody></table>}
    {edit && <CommandForm init={edit} onClose={(ch) => { setEdit(null); if (ch) reload(); }} />}
  </>;
}
function CommandForm({ init, onClose }: { init: Rec; onClose: (changed: boolean) => void }) {
  const [v, setV] = useState<Rec>({ ...init, paramsText: JSON.stringify(init.params ?? [], null, 1) }); const f = useSubmit(() => onClose(true)); const set = (k: string, x: unknown) => setV({ ...v, [k]: x }); const [err, setErr] = useState('');
  const save = () => f.submit(async () => { let params: unknown; try { params = JSON.parse(v.paramsText || '[]'); } catch { throw new Error('The parameters must be a JSON list, for example [{"name":"host","label":"Host","default":"8.8.8.8"}].'); }
    return api(init.id ? `/api/assets/commands/${init.id}` : '/api/assets/commands', { method: init.id ? 'PUT' : 'POST', body: { title: v.title, description: v.description, category: v.category, os: v.os, shell: v.shell, body: v.body, risk: v.risk, params, timeout: Number(v.timeout) || 120 } }); });
  return <Modal title={init.id ? 'Edit command' : 'Add command'} onClose={() => onClose(false)}><div className="form">
    <Field label="Title"><input value={v.title} onChange={(e) => set('title', e.target.value)} /></Field><Field label="Description"><input value={v.description} onChange={(e) => set('description', e.target.value)} /></Field>
    <div className="row"><Field label="Category"><input value={v.category} onChange={(e) => set('category', e.target.value)} /></Field><Field label="System"><select value={v.os} onChange={(e) => set('os', e.target.value)}><option>windows</option><option>linux</option><option>any</option></select></Field>
      <Field label="Shell"><select value={v.shell} onChange={(e) => set('shell', e.target.value)}><option>cmd</option><option>powershell</option><option>bash</option></select></Field><Field label="Risk"><select value={v.risk} onChange={(e) => set('risk', e.target.value)}><option>safe</option><option>caution</option><option>disruptive</option></select></Field></div>
    <Field label="Command"><textarea rows={5} value={v.body} onChange={(e) => set('body', e.target.value)} /></Field>
    <Field label="Parameters (JSON)"><textarea rows={4} value={v.paramsText} onChange={(e) => set('paramsText', e.target.value)} /></Field>
    <Err text={err || f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button><button className="primary" disabled={f.busy || !v.title.trim() || !v.body.trim()} onClick={() => { setErr(''); void save(); }}>Save</button></div></div></Modal>;
}

/** RustDesk server details used by remote control. */
export function Settings() {
  const { data, error, reload } = useLoad<Rec>('/api/assets/settings/remote-control'); const [v, setV] = useState<Rec | null>(null); const [saved, setSaved] = useState(false);
  const cur = v ?? data; const set = (k: string, x: unknown) => { setSaved(false); setV({ ...(cur as Rec), [k]: x }); };
  return <>
    <h1>Remote control settings</h1>
    <Err text={error} />
    {cur && <Card title="RustDesk"><div className="form" style={{ maxWidth: 560 }}>
      <label><input type="checkbox" checked={!!cur.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Remote control is switched on</label>
      <Field label="ID / rendezvous server"><input value={cur.idServer} onChange={(e) => set('idServer', e.target.value)} placeholder="rd.example.com" /></Field>
      <Field label="Relay server (optional)"><input value={cur.relay} onChange={(e) => set('relay', e.target.value)} /></Field>
      <Field label="Public key (id_ed25519.pub)"><input value={cur.key} onChange={(e) => set('key', e.target.value)} /></Field>
      <Field label="Installer address (https://…)"><input value={cur.installer} onChange={(e) => set('installer', e.target.value)} /></Field>
      <Field label="Installer SHA-256 (optional)"><input value={cur.sha256} onChange={(e) => set('sha256', e.target.value)} className="mono" /></Field>
      <div className="actions"><Act label="Save" primary run={async () => { await api('/api/assets/settings/remote-control', { method: 'PUT', body: cur }); setSaved(true); setV(null); reload(); }} />{saved && <span className="muted">Saved.</span>}</div></div></Card>}
  </>;
}
