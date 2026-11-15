import { useEffect, useState } from 'react';
import { Logo } from '../chrome';
import * as C from './crypto';

type Meta = { passcode: boolean; expiresAt: string; viewsLeft: number; from: string };

/** The public page of a send link (no sign-in). The key is in the address after the last /, which browsers never send to a server. */
export default function SendOpen({ id, frag }: { id: string; frag: string }) {
  const [meta, setMeta] = useState<Meta | null>(null); const [gone, setGone] = useState(''); const [pass, setPass] = useState('');
  const [text, setText] = useState<string | null>(null); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false); const [shown, setShown] = useState(false);
  useEffect(() => { void fetch(`/api/public/vault/sends/${encodeURIComponent(id)}`).then(async (r) => { const j = await r.json().catch(() => ({})); if (r.ok) setMeta(j as Meta); else setGone(String(j.detail ?? 'This link does not work.')); }, () => setGone('The server could not be reached.')); }, [id]);
  const open = async () => {
    setBusy(true); setErr('');
    try {
      const key = C.unb64(frag);
      const r = await fetch(`/api/public/vault/sends/${encodeURIComponent(id)}/open`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ passcodeCheck: meta?.passcode ? await C.sendCheck(key, pass) : undefined }) });
      const j = await r.json().catch(() => ({}));
      if (!r.ok) { if (r.status === 403) setErr(String(j.detail)); else setGone(String(j.detail ?? 'This link does not work.')); return; }
      const v = await C.decryptJson<{ text: string }>(await C.sendKey(key, meta?.passcode ? pass : undefined), String(j.dataEnc));
      setText(v.text); history.replaceState(null, '', '/#/send/opened');
    } catch { setErr('The link is incomplete or damaged. Ask for a new one.'); } finally { setBusy(false); }
  };
  return <main className="send-page">
    <div className="send-card">
      <div className="send-brand"><Logo size={28} /><strong>DeskAsk</strong></div>
      {text !== null ? <>
        <h1>Here it is</h1>
        <pre className="send-text">{shown ? text : text.replace(/[^\s]/g, '•')}</pre>
        <div className="tools"><button onClick={() => setShown(!shown)}>{shown ? 'Hide' : 'Show'}</button><button className="primary" onClick={() => void navigator.clipboard?.writeText(text)}>Copy</button></div>
        <p className="muted">Save it now: {meta && meta.viewsLeft > 1 ? `the link works ${meta.viewsLeft - 1} more time${meta.viewsLeft > 2 ? 's' : ''}.` : 'the link no longer works.'}</p>
      </> : gone ? <><h1>This link no longer works</h1><p>{gone}</p><p className="muted">Ask the person who sent it for a new one.</p></>
        : !meta ? <p className="muted">Checking the link…</p> : <>
          <h1>{meta.from} sent you something private</h1>
          <p>It opens {meta.viewsLeft === 1 ? 'only once' : `${meta.viewsLeft} more times`}, until {new Date(meta.expiresAt).toLocaleString()}. Open it when you are ready to keep it.</p>
          {meta.passcode && <label><span>Passcode</span><input value={pass} onChange={(e) => setPass(e.target.value)} autoFocus autoComplete="off" /></label>}
          {err && <p className="err" role="alert">{err}</p>}
          <div className="tools"><button className="primary" disabled={busy || (meta.passcode && !pass)} onClick={() => void open()}>{busy ? 'Opening…' : 'Open'}</button></div>
          <p className="muted small">It was encrypted before it left the sender's computer; DeskAsk cannot read it.</p>
        </>}
    </div>
  </main>;
}
