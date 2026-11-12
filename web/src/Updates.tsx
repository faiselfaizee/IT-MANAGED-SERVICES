import { useEffect, useState } from 'react';
import { accessToken } from './auth';
import { useLoad, type Rec } from './http';
import { Badge, Card, Err, Loading } from './ui';

const TONE: Record<string, 'ok' | 'warn' | 'bad' | undefined> = { done: 'ok', running: 'warn', rolled_back: 'bad', failed: 'bad' };
const LABEL: Record<string, string> = { done: 'Updated', running: 'Updating', rolled_back: 'Went back to the old version', failed: 'Failed' };

const PIECE = 5 * 1024 * 1024;
const hex = () => Array.from(crypto.getRandomValues(new Uint8Array(12)), (b) => b.toString(16).padStart(2, '0')).join('');

/** One piece (or the closing call) as its own small request, so a proxy with a time limit cannot cut the upload off. */
function sendPiece(url: string, body: Blob): Promise<{ status: number; json: Rec }> {
  return new Promise((resolve, reject) => {
    const x = new XMLHttpRequest(); x.open('POST', url);
    const t = accessToken(); if (t) x.setRequestHeader('authorization', 'Bearer ' + t);
    x.onload = () => { let j: Rec = {}; try { j = JSON.parse(x.responseText); } catch { /* not json */ } resolve({ status: x.status, json: j }); };
    x.onerror = () => reject(new Error('The connection was interrupted.'));
    x.send(body);
  });
}

/** Sends the file in pieces with progress; the server only drops it in the inbox and the updater service does the rest. */
async function upload(file: File, onProgress: (pct: number) => void): Promise<Rec> {
  const id = hex(); let at = 0; let failures = 0;
  const base = `/api/platform/updates/part?upload=${id}`;
  for (;;) {
    const last = at + PIECE >= file.size; const body = file.slice(at, Math.min(file.size, at + PIECE));
    const q = `${base}&at=${at}` + (last ? `&done=1&name=${encodeURIComponent(file.name)}` : '');
    let r: { status: number; json: Rec };
    try { r = await sendPiece(q, body); } catch (e) { if (++failures > 5) throw e; await new Promise((ok) => setTimeout(ok, 2000)); continue; }
    if (r.status === 409 && r.json.error === 'out_of_order' && at > 0 && ++failures <= 5) { at = Number(r.json.have) || 0; continue; }   // the server has a different amount: carry on from there
    if (r.status >= 500 && ++failures <= 5) { await new Promise((ok) => setTimeout(ok, 2000)); continue; }
    if (r.status >= 300) throw new Error(String(r.json.reason ?? r.json.error ?? `The server answered ${r.status}.`));
    failures = 0;
    if (last) { onProgress(100); return r.json; }
    at += body.size; onProgress(Math.round((at / file.size) * 100));
  }
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
      <p className="muted">Uploading needs two-step sign-in. If you have not set it up: <a href="/account/security?returnUrl=%2F">switch on two-step sign-in</a>, then sign out and sign in again with the code.</p>
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
