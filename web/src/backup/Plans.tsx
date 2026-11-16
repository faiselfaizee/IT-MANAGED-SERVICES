import { useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { ago, Act, Copy, Empty, Err, fmtBytes, fmtDate, Loading, Modal, useSubmit } from '../ui';
import { StatusBadge } from './BackupApp';

const n = (v: unknown) => Number(v ?? 0);
const KIND: Record<string, string> = { local: 'Folder or USB disk', smb: 'SMB share', s3: 'S3-compatible storage', azure: 'Azure Blob' };
export const schedule = (p: Rec) => n(p.everyHours) === 0 ? 'Only when asked' : n(p.everyHours) === 24 && n(p.atHour) >= 0 ? `Daily at ${String(p.atHour).padStart(2, '0')}:00` : n(p.everyHours) === 168 ? 'Weekly' : `Every ${p.everyHours} hours`;
const RSTATE: Record<string, [string, string]> = { queued: ['Waiting for the agent', ''], running: ['Restoring', 'warn'], done: ['Done', 'ok'], failed: ['Failed', 'bad'] };

/** The agent's own file backups: one plan per device and set of folders. */
export function Plans({ admin }: { admin: boolean }) {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/backup/plans');
  useInterval(reload, 60_000);
  const [form, setForm] = useState<Rec | null>(null);
  const list = Array.isArray(data) ? data : [];
  return <>
    <header className="dash-head"><h1>File backups</h1><p className="muted">The DeskAsk agent backs up chosen folders, encrypted, to a disk, a share or cloud storage.</p>
      {admin && <button className="primary" onClick={() => setForm({})}>New file backup</button>}</header>
    <Loading loading={loading && !data} error={error} />
    {data && (list.length === 0 ? <div className="card"><h2 style={{ marginTop: 0 }}>No file backups yet</h2>
      <p>A file backup plan says which folders of a device (with the agent 5.2 or later) are backed up, where to, how often and how long they are kept. Only what changed is stored each time, compressed and encrypted with AES-256; open files are read from a shadow copy on Windows.</p>
      <p>Backups can go to a folder or USB disk, an SMB share (a NAS or file server), S3-compatible storage (Amazon S3, Wasabi, Backblaze B2, MinIO, Synology C2) or Azure Blob.</p></div> :
      <div className="table-wrap"><table>
        <thead><tr><th>Status</th><th>Plan</th><th>Device</th><th>Folders</th><th>Goes to</th><th>When</th><th>Last backup</th><th>Kept</th></tr></thead>
        <tbody>{list.map((p) => <tr key={p.id}>
          <td>{p.enabled ? (p.status ? <StatusBadge s={p.status} /> : <span className="badge">No run yet</span>) : <span className="badge">Switched off</span>}</td>
          <td><a href={`#/backup/plan/${p.id}`}>{p.name}</a></td><td>{p.assetName}</td>
          <td className="small">{(p.paths as string[]).slice(0, 3).join(', ')}{(p.paths as string[]).length > 3 ? ` and ${(p.paths as string[]).length - 3} more` : ''}</td>
          <td className="small">{KIND[p.storageKind]}<div className="muted">{p.storageWhere}</div></td>
          <td>{schedule(p)}</td><td>{p.lastSnapshot ? <>{ago(p.lastSnapshot)}<div className="muted small">{fmtBytes(n(p.lastBytes))}</div></> : <span className="muted">none yet</span>}</td>
          <td>{n(p.snapshots)} backups</td></tr>)}</tbody></table></div>)}
    {form && <PlanForm plan={form} onClose={() => setForm(null)} onSaved={(id) => { setForm(null); location.hash = `#/backup/plan/${id}`; }} />}
  </>;
}

export function PlanPage({ id, admin, canRun }: { id: string; admin: boolean; canRun: boolean }) {
  const { data, error, loading, reload } = useLoad<Rec>(`/api/backup/plans/${encodeURIComponent(id)}`);
  useInterval(reload, 15_000);
  const [edit, setEdit] = useState(false); const [restore, setRestore] = useState<Rec | null>(null);
  const [msg, setMsg] = useState(''); const [pw, setPw] = useState<Rec | null>(null);
  if (loading && !data) return <Loading loading error={error} />;
  if (error || !data) return <Loading loading={false} error={error || 'Not found'} />;
  const p = data.plan as Rec; const snaps = (Array.isArray(data.snapshots) ? data.snapshots : []) as Rec[]; const restores = (Array.isArray(data.restores) ? data.restores : []) as Rec[];
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/backup">Backup</a><span aria-hidden="true">›</span><a href="#/backup/plans">File backups</a><span aria-hidden="true">›</span><span aria-current="page">{p.name}</span></nav>
    <header className="dash-head"><h1>{p.name}</h1>{p.status && <StatusBadge s={p.status} />}
      {canRun && <Act label="Back up now" primary run={async () => { await api(`/api/backup/plans/${p.id}/run`, { method: 'POST', body: {} }); setMsg('The agent has been asked to back up now; the result appears here when it is done.'); }} />}
      {admin && <><button onClick={() => setEdit(true)}>Change</button>
        <Act label="Show backup password" run={async () => setPw(await api<Rec>(`/api/backup/plans/${p.id}/password`, { method: 'POST', body: {} }))} />
        <Act label="Delete" danger confirm={`Delete the plan "${p.name}"? The agent stops backing up; the backups already made stay where they are.`} run={async () => { await api(`/api/backup/plans/${p.id}`, { method: 'DELETE' }); location.hash = '#/backup/plans'; }} /></>}</header>
    {msg && <p className="note" role="status">{msg}</p>}
    {pw && <div className="note"><p style={{ marginTop: 0 }}>Keep this password safe: with it, the backups in <b>{p.storageWhere}</b> (folder <code>{pw.folder}</code>) can be opened even without this server. Showing it has been recorded.</p><Copy text={String(pw.password)} /></div>}
    <div className="grid">
      <section className="card"><h2 style={{ marginTop: 0 }}>Plan</h2><dl className="kv">
        <dt>Device</dt><dd><a href={`#/assets/${p.assetId}`}>{p.assetName}</a></dd>
        <dt>Folders</dt><dd>{(p.paths as string[]).map((x) => <div key={x}><code>{x}</code></div>)}</dd>
        {(p.excludes as string[]).length > 0 && <><dt>Left out</dt><dd className="small">{(p.excludes as string[]).join(', ')}</dd></>}
        <dt>Goes to</dt><dd>{KIND[p.storageKind]}: {p.storageWhere}</dd>
        <dt>When</dt><dd>{schedule(p)}{p.enabled ? '' : ' (switched off)'}</dd>
        <dt>Kept</dt><dd>the last {p.keepLast} backups{n(p.keepDays) > 0 ? `, and every backup of the last ${p.keepDays} days` : ''}</dd>
        <dt>Open files</dt><dd>{p.useVss ? 'Read from a shadow copy (Windows)' : 'Skipped when in use'}</dd>
        {p.jobId && <><dt>Results</dt><dd><a href={`#/backup/job/${p.jobId}`}>Run history and alerts</a></dd></>}
      </dl></section>
      <section className="card"><h2 style={{ marginTop: 0 }}>Restores</h2>
        {restores.length === 0 ? <Empty>No restores yet. Choose a backup below and Restore.</Empty> : <table><tbody>{restores.map((r) => { const [w, t] = RSTATE[r.state] ?? [r.state, '']; return <tr key={r.id}>
          <td><span className={`badge ${t}`}>{w}</span></td><td className="small">{fmtDate(r.created)} by {r.by}<div className="muted">{(r.include as string[]).length ? (r.include as string[]).join(', ') : 'everything'} → {r.target || 'original places'}</div>{r.message && <div>{r.message}</div>}</td></tr>; })}</tbody></table>}
      </section>
    </div>
    <h2>Backups</h2>
    {snaps.length === 0 ? <Empty>No backup made yet. {p.enabled && n(p.everyHours) > 0 ? `The first runs ${schedule(p).toLowerCase()}, or now with Back up now.` : ''}</Empty> : <div className="table-wrap"><table>
      <thead><tr><th>Made</th><th>Files</th><th>Size</th><th>Stored new</th><th>Skipped</th><th /></tr></thead>
      <tbody>{snaps.map((s) => <tr key={s.id}><td>{fmtDate(s.at)}</td><td>{n(s.files).toLocaleString()}</td><td>{fmtBytes(n(s.bytes))}</td><td>{fmtBytes(n(s.newBytes))}</td>
        <td>{n(s.warnings) > 0 ? <span className="badge warn">{s.warnings}</span> : <span className="muted">0</span>}</td>
        <td>{canRun && <button onClick={() => setRestore(s)}>Restore</button>}</td></tr>)}</tbody></table></div>}
    {edit && <PlanForm plan={{ ...p, storageEdit: data.storageEdit }} onClose={() => setEdit(false)} onSaved={() => { setEdit(false); reload(); }} />}
    {restore && <RestoreForm plan={p} snap={restore} onClose={() => setRestore(null)} onDone={() => { setRestore(null); setMsg('The restore has been handed to the agent; its progress shows under Restores.'); reload(); }} />}
  </>;
}

function RestoreForm({ plan, snap, onClose, onDone }: { plan: Rec; snap: Rec; onClose: () => void; onDone: () => void }) {
  const [include, setInclude] = useState(''); const [target, setTarget] = useState('C:\\DeskAsk-Restore'); const [replace, setReplace] = useState(false);
  const { err, busy, submit } = useSubmit(onDone);
  return <Modal title={`Restore from ${fmtDate(snap.at)}`} onClose={onClose}>
    <form className="form" onSubmit={(e) => { e.preventDefault(); void submit(() => api(`/api/backup/plans/${plan.id}/restore`, { method: 'POST', body: { snapshot: snap.id, include, target: replace ? '' : target, replace } })); }}>
      <label><span>What to restore: files or folders as they were backed up, one per line (empty restores everything)</span><textarea rows={3} value={include} onChange={(e) => setInclude(e.target.value)} placeholder={(plan.paths as string[])[0] + '\\…'} /></label>
      <label className="check"><input type="checkbox" checked={replace} onChange={(e) => setReplace(e.target.checked)} /> Put the files back where they were, replacing the files there now</label>
      {!replace && <label><span>Restore into this folder on {plan.assetName} (the original folders are kept under it)</span><input value={target} onChange={(e) => setTarget(e.target.value)} /></label>}
      <Err text={err} />
      <div className="actions"><button type="button" onClick={onClose}>Cancel</button><button className="primary" disabled={busy || (!replace && !target.trim())}>Restore</button></div>
    </form>
  </Modal>;
}

function DevicePicker({ value, onPick }: { value: Rec | null; onPick: (d: Rec) => void }) {
  const [q, setQ] = useState('');
  const res = useLoad<Rec>(q.trim().length >= 2 ? `/api/assets?search=${encodeURIComponent(q.trim())}&take=20` : null, [q]);
  const items = ((Array.isArray(res.data?.items) ? res.data!.items : []) as Rec[]).filter((a) => a.agent?.state && a.agent.state !== 'none');
  if (value) return <p>{value.name} <span className="muted">{value.tag}</span> <button type="button" onClick={() => onPick(null as unknown as Rec)}>Choose another</button></p>;
  return <div>
    <input placeholder="Search a device with the agent (name or tag)" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Device" />
    {q.trim().length >= 2 && <ul className="objlist" style={{ maxHeight: 200, overflow: 'auto' }}>{items.length === 0 ? <li className="muted">No device with the agent matches.</li> :
      items.map((a) => <li key={a.id} onClick={() => onPick(a)} style={{ cursor: 'pointer' }}>{a.name} <span className="muted">{a.tag}{a.company ? ' · ' + a.company : ''}</span></li>)}</ul>}
  </div>;
}

function PlanForm({ plan, onClose, onSaved }: { plan: Rec; onClose: () => void; onSaved: (id: number) => void }) {
  const isNew = !plan.id;
  const [device, setDevice] = useState<Rec | null>(null);
  const [f, setF] = useState<Rec>({
    name: plan.name ?? 'Documents', paths: ((plan.paths as string[]) ?? ['C:\\Users']).join('\n'), excludes: ((plan.excludes as string[]) ?? []).join('\n'),
    everyHours: plan.everyHours ?? 24, atHour: plan.atHour ?? 22, keepLast: plan.keepLast ?? 30, keepDays: plan.keepDays ?? 30, useVss: plan.useVss ?? true, enabled: plan.enabled ?? true,
  });
  const [st, setSt] = useState<Rec>(plan.storageEdit ? { ...plan.storageEdit } : { kind: plan.storageKind ?? 'smb', path: '', region: 'us-east-1' });
  const set = (k: string, v: unknown) => setF({ ...f, [k]: v }); const ss = (k: string, v: unknown) => setSt({ ...st, [k]: v });
  const { err, busy, submit } = useSubmit(() => undefined);
  const save = () => submit(async () => {
    const body = { ...f, assetId: device?.id, paths: String(f.paths), excludes: String(f.excludes), everyHours: Number(f.everyHours), atHour: Number(f.atHour), keepLast: Number(f.keepLast), keepDays: Number(f.keepDays), storage: st };
    const r = await api<Rec>(isNew ? '/api/backup/plans' : `/api/backup/plans/${plan.id}`, { method: isNew ? 'POST' : 'PUT', body });
    onSaved(Number(r.id));
  });
  const keep = isNew ? '' : ' (leave empty to keep the saved one)';
  return <Modal title={isNew ? 'New file backup' : `Change ${plan.name}`} onClose={onClose}>
    <form className="form" onSubmit={(e) => { e.preventDefault(); void save(); }}>
      {isNew ? <label><span>Device</span><DevicePicker value={device} onPick={setDevice} /></label> : <p className="muted">Device: {plan.assetName}</p>}
      <label><span>Name</span><input value={f.name} onChange={(e) => set('name', e.target.value)} /></label>
      <label><span>Folders to back up, one per line</span><textarea rows={3} value={f.paths} onChange={(e) => set('paths', e.target.value)} /></label>
      <label><span>Leave out, one per line (for example *.iso, node_modules, *\Temp\*)</span><textarea rows={2} value={f.excludes} onChange={(e) => set('excludes', e.target.value)} /></label>
      <div className="row">
        <label><span>When</span><select value={String(f.everyHours)} onChange={(e) => set('everyHours', Number(e.target.value))}>
          <option value="24">Daily</option><option value="12">Every 12 hours</option><option value="6">Every 6 hours</option><option value="4">Every 4 hours</option><option value="1">Every hour</option><option value="168">Weekly</option><option value="0">Only when asked</option></select></label>
        {Number(f.everyHours) === 24 && <label><span>From (device's time)</span><select value={String(f.atHour)} onChange={(e) => set('atHour', Number(e.target.value))}>
          <option value="-1">Any time</option>{Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}</select></label>}
      </div>
      <div className="row">
        <label><span>Keep the last (backups)</span><input type="number" min={1} max={1000} value={f.keepLast} onChange={(e) => set('keepLast', e.target.value)} /></label>
        <label><span>And every backup of the last (days)</span><input type="number" min={0} max={3650} value={f.keepDays} onChange={(e) => set('keepDays', e.target.value)} /></label>
      </div>
      <label className="check"><input type="checkbox" checked={!!f.useVss} onChange={(e) => set('useVss', e.target.checked)} /> Read open files from a shadow copy (Windows)</label>
      <label className="check"><input type="checkbox" checked={!!f.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Back up on schedule</label>
      <fieldset style={{ border: 0, padding: 0, margin: 0, display: 'grid', gap: 12 }}>
        <label><span>Where the backups go</span><select value={st.kind} onChange={(e) => ss('kind', e.target.value)}>{Object.entries(KIND).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></label>
        {st.kind === 'local' && <label><span>Folder on the device (D:\Backups, or E:\ for a USB disk)</span><input value={st.path ?? ''} onChange={(e) => ss('path', e.target.value)} /></label>}
        {st.kind === 'smb' && <>
          <label><span>Share (\\server\share or \\server\share\folder)</span><input value={st.path ?? ''} onChange={(e) => ss('path', e.target.value)} placeholder="\\nas01\backup" /></label>
          <div className="row"><label><span>Account (domain\user; empty to use the computer's own)</span><input value={st.user ?? ''} onChange={(e) => ss('user', e.target.value)} /></label>
            <label><span>Password{keep}</span><input type="password" value={st.password ?? ''} onChange={(e) => ss('password', e.target.value)} autoComplete="new-password" /></label></div></>}
        {st.kind === 's3' && <>
          <label><span>Endpoint (empty for Amazon S3; https://s3.wasabisys.com, https://s3.eu-central-003.backblazeb2.com, https://minio.local:9000)</span><input value={st.endpoint ?? ''} onChange={(e) => ss('endpoint', e.target.value)} /></label>
          <div className="row"><label><span>Region</span><input value={st.region ?? ''} onChange={(e) => ss('region', e.target.value)} /></label>
            <label><span>Bucket</span><input value={st.bucket ?? ''} onChange={(e) => ss('bucket', e.target.value)} /></label>
            <label><span>Folder in the bucket (optional)</span><input value={st.prefix ?? ''} onChange={(e) => ss('prefix', e.target.value)} /></label></div>
          <div className="row"><label><span>Access key</span><input value={st.accessKey ?? ''} onChange={(e) => ss('accessKey', e.target.value)} /></label>
            <label><span>Secret key{keep}</span><input type="password" value={st.secretKey ?? ''} onChange={(e) => ss('secretKey', e.target.value)} autoComplete="new-password" /></label></div>
          <label className="check"><input type="checkbox" checked={!!st.pathStyle} onChange={(e) => ss('pathStyle', e.target.checked)} /> Bucket in the path (MinIO and some others)</label></>}
        {st.kind === 'azure' && <>
          <label><span>Container SAS address (read, add, create, write, delete and list){keep}</span><input value={st.sasUrl ?? ''} onChange={(e) => ss('sasUrl', e.target.value)} placeholder="https://account.blob.core.windows.net/backups?sv=...&sig=..." /></label>
          <label><span>Folder in the container (optional)</span><input value={st.prefix ?? ''} onChange={(e) => ss('prefix', e.target.value)} /></label></>}
      </fieldset>
      <p className="muted small">Each plan gets its own folder there (deskask-plan-…) and its own password, made by DeskAsk; administrators can show it on the plan to restore without this server.</p>
      <Err text={err} />
      <div className="actions"><button type="button" onClick={onClose}>Cancel</button><button className="primary" disabled={busy || (isNew && !device)}>Save</button></div>
    </form>
  </Modal>;
}
