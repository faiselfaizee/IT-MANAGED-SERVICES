import { useState } from 'react';
import { api, uploadInPieces, useLoad, type Rec } from '../http';
import { Act, Badge, Empty, Err, Field, Loading, Modal, useSubmit } from '../ui';
import { JobView } from './DetailOps';

const KIND: Record<string, string> = { file: 'Installer', winget: 'winget', choco: 'Chocolatey', apt: 'Package manager' };
const size = (n: number) => n >= 1 << 30 ? `${(n / (1 << 30)).toFixed(1)} GB` : n >= 1 << 20 ? `${(n / (1 << 20)).toFixed(1)} MB` : n > 0 ? `${Math.max(1, Math.round(n / 1024))} KB` : '';

/** Assets › Software › Repository: the approved programs engineers install on computers in one step. */
export default function Packages() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/assets/packages');
  const [add, setAdd] = useState(false); const [install, setInstall] = useState<Rec | null>(null); const [history, setHistory] = useState<Rec | null>(null); const [edit, setEdit] = useState<Rec | null>(null);
  const items = (data?.items ?? []) as Rec[];
  return <>
    <h1>Software repository</h1>
    <p className="muted">Approved programs to install on computers. An uploaded installer is kept here with its SHA-256; the computer downloads it, checks it and installs it silently. Each install is a job with its own log on the asset.</p>
    <div className="tools"><button className="primary" onClick={() => setAdd(true)}>Add a program</button></div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No programs yet. Add an installer (.msi, .exe, .deb, .rpm) or a winget, Chocolatey or package-manager name.</Empty> : <table><thead><tr><th>Program</th><th>For</th><th>Kind</th><th>Size</th><th className="num">Installs</th><th /></tr></thead><tbody>
      {items.map((p) => <tr key={p.id}><td>{p.name} {p.version} {!p.enabled && <Badge>off</Badge>}<div className="muted small">{p.publisher}{p.kind === 'file' ? ` · ${p.fileName} · SHA-256 ${String(p.sha256).slice(0, 12)}…` : ` · ${p.source}`}</div></td>
        <td>{p.os === 'windows' ? 'Windows' : 'Linux'}</td><td>{KIND[p.kind] ?? p.kind}</td><td>{size(p.size)}</td>
        <td className="num"><a href="#" onClick={(e) => { e.preventDefault(); setHistory(p); }}>{p.installs}</a></td>
        <td><button disabled={!p.enabled} onClick={() => setInstall(p)}>Install on…</button> <button onClick={() => setEdit(p)}>Edit</button>{' '}
          <Act label="Remove" danger confirm={`Remove ${p.name} ${p.version} from the repository? Computers that have it keep it.`} run={async () => { await api(`/api/assets/packages/${p.id}`, { method: 'DELETE' }); reload(); }} /></td></tr>)}</tbody></table>)}
    {add && <AddPackage onClose={(ch) => { setAdd(false); if (ch) reload(); }} />}
    {edit && <EditPackage p={edit} onClose={(ch) => { setEdit(null); if (ch) reload(); }} />}
    {install && <InstallOn p={install} onClose={() => { setInstall(null); reload(); }} />}
    {history && <Installs p={history} onClose={() => setHistory(null)} />}
  </>;
}

function AddPackage({ onClose }: { onClose: (changed: boolean) => void }) {
  const [v, setV] = useState({ os: 'windows', kind: 'file', name: '', version: '', publisher: '', source: '', args: '', notes: '', sha256: '' }); const set = (k: string, x: string) => setV({ ...v, [k]: x });
  const [file, setFile] = useState<File | null>(null); const [sent, setSent] = useState(0); const f = useSubmit(() => onClose(true));
  const kinds = v.os === 'windows' ? ['file', 'winget', 'choco'] : ['file', 'apt'];
  const accept = v.os === 'windows' ? '.msi,.exe' : '.deb,.rpm,.sh';
  const pick = (x?: File) => { setFile(x ?? null); if (x && !v.name) set('name', x.name.replace(/\.(msi|exe|deb|rpm|sh)$/i, '').replace(/[-_]+/g, ' ')); };
  const save = async () => {
    let upload = '';
    if (v.kind === 'file') {
      if (!file) throw new Error('Choose the installer file.');
      const u = await api<Rec>('/api/assets/packages/uploads', { body: {} }); upload = String(u.id);
      await uploadInPieces(`/api/assets/packages/uploads/${upload}`, file, Number(u.maxPart ?? 8 << 20), setSent);
    }
    await api('/api/assets/packages', { body: { ...v, upload, fileName: file?.name ?? '' } });
  };
  return <Modal title="Add a program" onClose={() => onClose(false)}><div className="form">
    <div className="row"><Field label="For"><select value={v.os} onChange={(e) => setV({ ...v, os: e.target.value, kind: 'file' })}><option value="windows">Windows</option><option value="linux">Linux</option></select></Field>
      <Field label="Kind"><select value={v.kind} onChange={(e) => set('kind', e.target.value)}>{kinds.map((k) => <option key={k} value={k}>{KIND[k]}</option>)}</select></Field></div>
    {v.kind === 'file' ? <><Field label={`Installer (${accept})`}><input type="file" accept={accept} onChange={(e) => pick(e.target.files?.[0])} /></Field>
      <Field label="SHA-256 from the vendor (optional, checked after upload)"><input value={v.sha256} onChange={(e) => set('sha256', e.target.value.trim())} spellCheck={false} /></Field></>
      : <Field label={v.kind === 'apt' ? 'Package name (apt, dnf, yum or zypper)' : v.kind === 'winget' ? 'winget id, for example Google.Chrome' : 'Chocolatey id, for example googlechrome'}><input value={v.source} onChange={(e) => set('source', e.target.value.trim())} spellCheck={false} /></Field>}
    <div className="row"><Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} /></Field><Field label="Version"><input value={v.version} onChange={(e) => set('version', e.target.value)} /></Field></div>
    <Field label="Publisher"><input value={v.publisher} onChange={(e) => set('publisher', e.target.value)} /></Field>
    {v.kind !== 'apt' && <Field label={v.kind === 'file' ? 'Silent switches (an .msi gets /qn /norestart already; for an .exe, for example /S or /quiet)' : 'Installer switches to pass on (optional)'}><input value={v.args} onChange={(e) => set('args', e.target.value)} spellCheck={false} /></Field>}
    <Field label="Notes"><textarea rows={2} value={v.notes} onChange={(e) => set('notes', e.target.value)} /></Field>
    {file && f.busy && <p role="status">Uploading {Math.round((sent / Math.max(1, file.size)) * 100)}%…</p>}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !v.name.trim() || (v.kind === 'file' ? !file : !v.source)} onClick={() => void f.submit(save)}>Add</button></div></div></Modal>;
}

function EditPackage({ p, onClose }: { p: Rec; onClose: (changed: boolean) => void }) {
  const [v, setV] = useState({ name: p.name, version: p.version, publisher: p.publisher, args: p.args, notes: p.notes, enabled: !!p.enabled }); const f = useSubmit(() => onClose(true));
  return <Modal title={`Edit ${p.name}`} onClose={() => onClose(false)}><div className="form">
    <div className="row"><Field label="Name"><input value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })} /></Field><Field label="Version"><input value={v.version} onChange={(e) => setV({ ...v, version: e.target.value })} /></Field></div>
    <Field label="Publisher"><input value={v.publisher} onChange={(e) => setV({ ...v, publisher: e.target.value })} /></Field>
    {p.kind !== 'apt' && <Field label="Switches"><input value={v.args} onChange={(e) => setV({ ...v, args: e.target.value })} spellCheck={false} /></Field>}
    <Field label="Notes"><textarea rows={2} value={v.notes} onChange={(e) => setV({ ...v, notes: e.target.value })} /></Field>
    <label><input type="checkbox" checked={v.enabled} onChange={(e) => setV({ ...v, enabled: e.target.checked })} /> Can be installed</label>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !v.name.trim()} onClick={() => void f.submit(() => api(`/api/assets/packages/${p.id}`, { method: 'PUT', body: v }))}>Save</button></div></div></Modal>;
}

/** Choose computers (of the program's system) and start the installs; each shows its job. */
export function InstallOn({ p, assetId, onClose }: { p: Rec; assetId?: number; onClose: () => void }) {
  const [q, setQ] = useState(''); const [sel, setSel] = useState<Set<number>>(new Set(assetId ? [assetId] : []));
  const found = useLoad<Rec>(assetId ? null : `/api/assets?search=${encodeURIComponent(q)}&agent=online&take=60`, [q]);
  const list = ((found.data?.items ?? []) as Rec[]).filter((a) => String(a.os ?? '').toLowerCase().includes(p.os === 'windows' ? 'windows' : '') && (p.os === 'windows' || !String(a.os ?? '').toLowerCase().includes('windows')));
  const [res, setRes] = useState<Rec | null>(null); const [job, setJob] = useState(''); const f = useSubmit(() => undefined);
  const toggle = (id: number) => setSel((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  return <Modal title={`Install ${p.name} ${p.version}`.trim()} onClose={onClose}><div className="form">
    {!res && !assetId && <><Field label="Find computers (agent online)"><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name, tag, person…" autoFocus /></Field>
      <Loading loading={found.loading && !found.data} error={found.error} />
      {found.data && (list.length === 0 ? <Empty>No {p.os === 'windows' ? 'Windows' : 'Linux'} computer with a live agent matches.</Empty> : <table><thead><tr><th /><th>Computer</th><th>OS</th><th>Assigned to</th></tr></thead><tbody>
        {list.map((a) => <tr key={a.id}><td><input type="checkbox" aria-label={'Choose ' + a.name} checked={sel.has(a.id)} onChange={() => toggle(a.id)} /></td><td>{a.name} <span className="muted">{a.tag}</span></td><td>{a.os}</td><td>{a.assignedTo}</td></tr>)}</tbody></table>)}</>}
    {!res && assetId && <p>The computer downloads the program, checks it and installs it silently. You can follow it in the log.</p>}
    {res && <>{(res.queued as Rec[]).length > 0 && <p role="status">{(res.queued as Rec[]).length} install{(res.queued as Rec[]).length === 1 ? '' : 's'} started.</p>}
      <ul>{(res.queued as Rec[]).map((x) => <li key={x.job}>{x.name}: <a href="#" onClick={(e) => { e.preventDefault(); setJob(String(x.job)); }}>follow the install</a></li>)}
        {(res.skipped as Rec[]).map((x) => <li key={x.assetId}>{x.name ?? `Asset ${x.assetId}`}: <span className="muted">{x.reason}</span></li>)}</ul></>}
    <Err text={f.err} /><div className="actions"><button onClick={onClose}>{res ? 'Close' : 'Cancel'}</button>
      {!res && <button className="primary" disabled={f.busy || sel.size === 0} onClick={() => void f.submit(async () => { const r = await api<Rec>(`/api/assets/packages/${p.id}/install`, { body: { assetIds: [...sel] } }); setRes(r); if (assetId && (r.queued as Rec[]).length === 1) setJob(String((r.queued as Rec[])[0].job)); })}>Install{sel.size > 1 ? ` on ${sel.size}` : ''}</button>}</div>
    {job && <JobView jid={job} onClose={() => setJob('')} />}
  </div></Modal>;
}

function Installs({ p, onClose }: { p: Rec; onClose: () => void }) {
  const { data, error, loading } = useLoad<Rec>(`/api/assets/packages/${p.id}/installs`); const [job, setJob] = useState('');
  const items = (data?.items ?? []) as Rec[];
  return <Modal title={`Where ${p.name} was installed`} onClose={onClose}>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>Not installed from the repository yet.</Empty> : <table><thead><tr><th>Computer</th><th>When</th><th>By</th><th>Result</th></tr></thead><tbody>
      {items.map((i) => <tr key={i.job}><td><a href={`#/assets/${i.assetId}`}>{i.asset}</a></td><td>{new Date(i.at).toLocaleString()}</td><td>{i.by}</td>
        <td><a href="#" onClick={(e) => { e.preventDefault(); setJob(String(i.job)); }}><Badge tone={i.status === 'done' && !i.exit ? 'ok' : ['failed', 'cancelled', 'timeout', 'lost'].includes(i.status) || (i.status === 'done' && i.exit) ? 'bad' : undefined}>{i.status || 'unknown'}</Badge></a></td></tr>)}</tbody></table>)}
    <div className="actions"><button onClick={onClose}>Close</button></div>
    {job && <JobView jid={job} onClose={() => setJob('')} />}
  </Modal>;
}

/** On an asset's Applications tab: pick a program of the repository for this computer. */
export function InstallFromRepository({ assetId, os, onClose }: { assetId: number; os: string; onClose: () => void }) {
  const { data, error, loading } = useLoad<Rec>(`/api/assets/packages?os=${os}`); const [p, setP] = useState<Rec | null>(null);
  const items = ((data?.items ?? []) as Rec[]).filter((x) => x.enabled);
  if (p) return <InstallOn p={p} assetId={assetId} onClose={onClose} />;
  return <Modal title="Install from the repository" onClose={onClose}>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No {os === 'windows' ? 'Windows' : 'Linux'} programs in the repository yet. Add them under Software › Repository.</Empty>
      : <table><tbody>{items.map((x) => <tr key={x.id}><td>{x.name} {x.version}<div className="muted small">{x.publisher} · {KIND[x.kind]}</div></td><td><button onClick={() => setP(x)}>Install</button></td></tr>)}</tbody></table>)}
    <div className="actions"><button onClick={onClose}>Close</button></div>
  </Modal>;
}
