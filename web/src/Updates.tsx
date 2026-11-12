import { useEffect, useState } from 'react';
import { accessToken } from './auth';
import { useLoad, type Rec } from './http';
import { Badge, Card, Err, Loading } from './ui';

const TONE: Record<string, 'ok' | 'warn' | 'bad' | undefined> = { done: 'ok', running: 'warn', rolled_back: 'bad', failed: 'bad' };
const LABEL: Record<string, string> = { done: 'Updated', running: 'Updating', rolled_back: 'Went back to the old version', failed: 'Failed' };

/** Sends the file with progress; the server only drops it in the inbox and the updater service does the rest. */
function upload(file: File, onProgress: (pct: number) => void): Promise<Rec> {
  return new Promise((resolve, reject) => {
    const x = new XMLHttpRequest();
    x.open('POST', '/api/platform/updates?name=' + encodeURIComponent(file.name));
    const t = accessToken(); if (t) x.setRequestHeader('authorization', 'Bearer ' + t);
    x.upload.onprogress = (e) => { if (e.lengthComputable) onProgress(Math.round((e.loaded / e.total) * 100)); };
    x.onload = () => { let j: Rec = {}; try { j = JSON.parse(x.responseText); } catch { /* not json */ } if (x.status < 300) resolve(j); else reject(new Error(String(j.reason ?? j.error ?? `The server answered ${x.status}.`))); };
    x.onerror = () => reject(new Error('The upload was interrupted.'));
    x.send(file);
  });
}

/** Admin page: put in a new release by uploading its .dkb file. No setup page, no other files. */
export default function Updates() {
  const u = useLoad<Rec>('/api/platform/updates');
  const [file, setFile] = useState<File | null>(null); const [pct, setPct] = useState(0); const [busy, setBusy] = useState(false); const [err, setErr] = useState(''); const [ok, setOk] = useState('');
  const d = u.data; const jobs = (d?.jobs ?? []) as Rec[]; const queued = (d?.queued ?? []) as Rec[];
  const working = busy || queued.length > 0 || !!d?.status?.running;
  useEffect(() => { if (!working) return; const i = setInterval(u.reload, 4000); return () => clearInterval(i); }, [working, u.reload]);
  const send = async () => {
    if (!file) return; setBusy(true); setErr(''); setOk(''); setPct(0);
    try { await upload(file, setPct); setOk('Uploaded. The updater is checking the signature and installing it. The page may go offline for a minute; refresh it afterwards.'); setFile(null); u.reload(); }
    catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const when = (ms?: number) => (ms ? new Date(ms).toLocaleString() : '');
  return <>
    <h1>Updates</h1>
    <Loading loading={u.loading && !d} error={u.error} />
    {d && !d.installed && <Card><p>Updates from this page are not set up on this server. Run the setup page once with the install tab; after that, updates are one file.</p></Card>}
    {d?.installed && <Card title="Installed version">
      <p><strong>{d.status?.current || 'Unknown'}</strong> {d.serviceAlive ? <Badge tone="ok">Updater running</Badge> : <Badge tone="bad">Updater not responding</Badge>}</p>
      {!d.serviceAlive && <p className="muted">The updater task did not report in the last 3 minutes. Check the DeskAskUpdater task in Task Scheduler.</p>}
    </Card>}
    {d?.installed && <Card title="Install a new version">
      <p className="muted">Download <code>deskask-host-win-x64-&lt;version&gt;.dkb</code> from the GitHub release and choose it here. Your settings, database and customers stay as they are. The signature is checked first; if the new version does not start, the old one comes back by itself.</p>
      <div className="form">
        <input type="file" accept=".dkb" disabled={busy} onChange={(e) => setFile(e.target.files?.[0] ?? null)} aria-label="Release file" />
        {busy && <progress value={pct} max={100} aria-label="Upload progress" />}
        <Err text={err} />{ok && <p role="status">{ok}</p>}
        {queued.length > 0 && <p role="status">Waiting for the updater: {queued.map((q) => q.file).join(', ')}</p>}
        <div className="actions"><button className="primary" disabled={busy || !file || queued.length > 0} onClick={send}>Upload and install</button></div>
      </div>
    </Card>}
    {jobs.length > 0 && <Card title="History">
      <table><thead><tr><th>File</th><th>Result</th><th>From</th><th>To</th><th>When</th></tr></thead><tbody>
        {jobs.map((j) => <tr key={j.id}><td>{j.file}</td><td><Badge tone={TONE[j.status]}>{LABEL[j.status] ?? j.status}</Badge>{j.error ? <div className="muted">{j.error}</div> : null}</td><td>{j.from}</td><td>{j.to}</td><td className="muted">{when(j.finished || j.started)}</td></tr>)}
      </tbody></table>
    </Card>}
  </>;
}
