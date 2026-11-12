import { useState } from 'react';
import { api, go, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, Modal, fmtBytes, useSubmit } from '../ui';

export default function Discover() {
  const [tab, setTab] = useState<'probe' | 'scan' | 'creds'>('probe'); const [prefill, setPrefill] = useState('');
  return <>
    <h1>Discover devices</h1>
    <nav className="sub">{([['probe', 'Look up a device'], ['scan', 'Scan a range'], ['creds', 'Saved credentials']] as const).map(([k, l]) => <a key={k} href="#/assets/discover" className={tab === k ? 'on' : ''} onClick={(e) => { e.preventDefault(); setTab(k); }}>{l}</a>)}</nav>
    {tab === 'probe' && <Probe key={prefill} initial={prefill} />}{tab === 'scan' && <Scan onPick={(ip) => { setPrefill(ip); setTab('probe'); }} />}{tab === 'creds' && <Creds />}
  </>;
}

function Probe({ initial = '' }: { initial?: string }) {
  const [target, setTarget] = useState(initial); const [res, setRes] = useState<Rec | null>(null); const [details, setDetails] = useState<Rec | null>(null); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false); const [adopt, setAdopt] = useState(false);
  const run = async () => { setBusy(true); setErr(''); setDetails(null); try { setRes(await api('/api/assets/discover/probe', { body: { target } })); } catch (e) { setErr((e as Error).message); setRes(null); } finally { setBusy(false); } };
  const inv = details?.inventory as Rec | undefined;
  return <>
    <div className="tools"><input placeholder="IP address or DNS name" value={target} onChange={(e) => setTarget(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter' && target.trim()) void run(); }} style={{ minWidth: 260 }} />
      <button className="primary" disabled={busy || !target.trim()} onClick={run}>{busy ? 'Probing…' : 'Probe'}</button></div>
    <Err text={err} />
    {res && <Card title={res.hostname || res.target}>
      <Dl rows={[['Addresses', (res.ips as string[]).join(', ')], ['Reachable', <Badge tone={res.reachable ? 'ok' : 'bad'}>{res.reachable ? 'Yes' : 'No answer'}</Badge>], ['Open ports', (res.openNames as string[]).join(', ') || 'none found'], ['MAC address', res.mac], ['Looks like', [res.guess?.cat, res.guess?.sub].filter(Boolean).join(' / ')]]} />
      {res.existing ? <p className="note">Already in the asset list: <a href={`#/assets/${res.existing.id}`}>{res.existing.name}</a></p> : null}
      <div className="tools"><Act label="Fetch details" run={async () => { setDetails(await api('/api/assets/discover/lookup', { body: { host: res.target } })); }} />
        <button className="primary" onClick={() => setAdopt(true)}>{res.existing ? 'Update asset' : 'Add as asset'}</button></div>
      <p className="muted">Fetching details uses the agent when one is installed, otherwise a saved credential (WinRM for Windows, SSH for Linux).</p>
    </Card>}
    {details && inv && <Card title={`Details (from ${details.source})`}><Dl rows={[['Host', String(inv.hostname ?? '')], ['Operating system', [inv.os_name, inv.os_version].filter(Boolean).join(' ')], ['Make / model', [inv.make, inv.model].filter(Boolean).join(' ')], ['Serial', String(inv.serial_number ?? '')], ['Processor', String(inv.cpu_model ?? '')], ['Memory', inv.ram_bytes ? fmtBytes(Number(inv.ram_bytes)) : ''], ['Storage', inv.total_storage_bytes ? fmtBytes(Number(inv.total_storage_bytes)) : '']]} /></Card>}
    {adopt && res && <Adopt probe={res} raw={details?.raw} source={details?.source} onClose={(id) => { setAdopt(false); if (id) go(`assets/${id}`); }} />}
  </>;
}

function Adopt({ probe, raw, source, onClose }: { probe: Rec; raw?: Rec; source?: string; onClose: (id?: number) => void }) {
  const companies = useLoad<Rec[]>('/api/assets/companies'); const [name, setName] = useState<string>(probe.hostname || probe.target); const [co, setCo] = useState(''); const [cat, setCat] = useState<string>(probe.guess?.cat ?? ''); const [loc, setLoc] = useState(''); const [notes, setNotes] = useState('');
  const f = useSubmit(() => undefined); const [err, setErr] = useState('');
  return <Modal title={probe.existing ? 'Update asset' : 'Add as asset'} onClose={() => onClose()}><div className="form">
    <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} /></Field>
    <div className="row"><Field label="Customer"><select value={co} onChange={(e) => setCo(e.target.value)}><option value="">(own organisation)</option>{(Array.isArray(companies.data) ? companies.data : []).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
      <Field label="Category"><input value={cat} onChange={(e) => setCat(e.target.value)} /></Field></div>
    <Field label="Location"><input value={loc} onChange={(e) => setLoc(e.target.value)} /></Field><Field label="Notes"><textarea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} /></Field>
    <Err text={err || f.err} /><div className="actions"><button onClick={() => onClose()}>Cancel</button>
      <button className="primary" onClick={async () => { setErr(''); try { const r = await api<Rec>('/api/assets/discover/adopt', { body: { probe, name, companyId: co ? Number(co) : undefined, category: cat, location: loc, notes, raw, source, assetId: probe.existing?.id } }); onClose(r.id); } catch (e) { setErr((e as Error).message); } }}>Save</button></div></div></Modal>;
}

function Scan({ onPick }: { onPick: (ip: string) => void }) {
  const [cidr, setCidr] = useState('192.168.1.0/24'); const [res, setRes] = useState<Rec | null>(null); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  return <>
    <div className="tools"><input value={cidr} onChange={(e) => setCidr(e.target.value)} aria-label="Range" /><button className="primary" disabled={busy} onClick={async () => { setBusy(true); setErr(''); setRes(null); try { setRes(await api('/api/assets/discover/scan', { body: { cidr } })); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } }}>{busy ? 'Scanning…' : 'Scan'}</button>
      <span className="muted">Up to 1024 addresses (a /22). The scan runs from the server, so it only sees networks the server can reach; use a site collector for other sites.</span></div>
    <Err text={err} />
    {res && (res.found.length === 0 ? <Empty>No device answered among {res.scanned} addresses.</Empty> : <><p className="muted">{res.found.length} of {res.scanned} addresses answered.</p>
      <table><thead><tr><th>Address</th><th>Name</th><th>Looks like</th><th>Open ports</th><th /></tr></thead><tbody>
        {(res.found as Rec[]).map((h) => <tr key={h.ip}><td className="mono">{h.ip}</td><td>{h.hostname}</td><td>{[h.cat, h.sub].filter(Boolean).join(' / ')}</td><td className="mono">{(h.open as number[]).join(', ')}</td><td>{h.known ? <Badge tone="ok">in assets</Badge> : <a href="#/assets/discover" onClick={(e) => { e.preventDefault(); onPick(h.ip); }}>Look up</a>}</td></tr>)}</tbody></table></>)}
  </>;
}

function Creds() {
  const { data, error, reload } = useLoad<Rec>('/api/assets/discover/creds'); const [add, setAdd] = useState(false);
  return <>
    <div className="tools"><button className="primary" onClick={() => setAdd(true)}>Add credential</button></div><Err text={error} />
    <p className="muted">Credentials are encrypted in this customer's database and never shown again. Use an account with just enough rights to read hardware and software details.</p>
    {data && (data.items.length === 0 ? <Empty>No saved credentials.</Empty> : <table><thead><tr><th>Name</th><th>Kind</th><th>User</th><th>Customer</th><th /></tr></thead><tbody>
      {(data.items as Rec[]).map((c) => <tr key={c.id}><td>{c.name}</td><td>{c.kind === 'windows' ? 'Windows (WinRM)' : c.kind === 'ssh' ? 'Linux (SSH key)' : c.kind === 'sshpw' ? 'Linux (SSH password)' : c.kind === 'snmp' ? 'SNMP community' : c.kind}</td><td>{c.username}</td><td>{c.company || '(any)'}</td>
        <td><Act label="Delete" danger confirm={`Delete the credential "${c.name}"?`} run={async () => { await api(`/api/assets/discover/creds/${c.id}`, { method: 'DELETE' }); reload(); }} /></td></tr>)}</tbody></table>)}
    {add && <CredForm onClose={(ch) => { setAdd(false); if (ch) reload(); }} />}
  </>;
}
function CredForm({ onClose }: { onClose: (changed: boolean) => void }) {
  const [v, setV] = useState({ name: '', kind: 'windows', username: '', secret: '' }); const f = useSubmit(() => onClose(true));
  return <Modal title="Add credential" onClose={() => onClose(false)}><div className="form">
    <Field label="Name"><input value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })} placeholder="Domain admin (read only)" /></Field>
    <Field label="Kind"><select value={v.kind} onChange={(e) => setV({ ...v, kind: e.target.value })}><option value="windows">Windows (user and password, WinRM)</option><option value="sshpw">Linux (user and SSH password)</option><option value="ssh">Linux (user and SSH private key)</option><option value="snmp">Printer, switch or firewall (SNMP community)</option></select></Field>
    {v.kind !== 'snmp' && <Field label="User name"><input value={v.username} onChange={(e) => setV({ ...v, username: e.target.value })} autoComplete="off" /></Field>}
    <Field label={v.kind === 'ssh' ? 'Private key' : v.kind === 'snmp' ? 'Community string' : 'Password'}>{v.kind === 'ssh' ? <textarea rows={5} value={v.secret} onChange={(e) => setV({ ...v, secret: e.target.value })} /> : <input type="password" value={v.secret} onChange={(e) => setV({ ...v, secret: e.target.value })} autoComplete="new-password" />}</Field>
    {v.kind === 'windows' && <p className="muted">The collector sends this login to Windows computers over PowerShell remoting. For a domain account write DOMAIN\\user.</p>}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button><button className="primary" disabled={f.busy} onClick={() => f.submit(() => api('/api/assets/discover/creds', { body: v }))}>Save</button></div></div></Modal>;
}
