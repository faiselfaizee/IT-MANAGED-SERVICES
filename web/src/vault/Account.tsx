import { useState } from 'react';
import * as S from './session';
import { RecoveryCode, Strength } from './Gate';
import { strength } from './crypto';

/** The person's own vault key: master password and recovery code. */
export default function Account() {
  const [a, setA] = useState(''); const [b, setB] = useState(''); const [c, setC] = useState(''); const [msg, setMsg] = useState(''); const [err, setErr] = useState('');
  const [rp, setRp] = useState(''); const [code, setCode] = useState(''); const [rerr, setRerr] = useState(''); const [busy, setBusy] = useState(false);
  if (code) return <RecoveryCode code={code} again onDone={() => setCode('')} />;
  return <>
    <h1>My vault key</h1>
    <div className="grid">
      <form className="card form" onSubmit={async (e) => { e.preventDefault(); setErr(''); setMsg('');
        if (b.length < 12 || strength(b).score < 2) return setErr('Choose at least 12 characters that are not easy to guess.');
        if (b !== c) return setErr('The two new passwords are not the same.');
        setBusy(true); try { await S.changePassword(a, b); setMsg('Your master password is changed.'); setA(''); setB(''); setC(''); } catch (x) { setErr((x as Error).message); } finally { setBusy(false); } }}>
        <h2>Change the master password</h2>
        <label><span>Current master password</span><input type="password" autoComplete="current-password" value={a} onChange={(e) => setA(e.target.value)} /></label>
        <label><span>New master password</span><input type="password" autoComplete="new-password" value={b} onChange={(e) => setB(e.target.value)} /></label>
        <Strength pw={b} />
        <label><span>Type it again</span><input type="password" autoComplete="new-password" value={c} onChange={(e) => setC(e.target.value)} /></label>
        {err && <p className="err" role="alert">{err}</p>}{msg && <p className="note" role="status">{msg}</p>}
        <div className="tools"><button className="primary" disabled={busy}>Change</button></div>
      </form>
      <form className="card form" onSubmit={async (e) => { e.preventDefault(); setRerr(''); try { setCode(await S.newRecoveryCode(rp)); setRp(''); } catch (x) { setRerr((x as Error).message); } }}>
        <h2>New recovery code</h2>
        <p className="muted">When the old one may have been seen, or is lost. The old code stops working.</p>
        <label><span>Master password</span><input type="password" autoComplete="current-password" value={rp} onChange={(e) => setRp(e.target.value)} /></label>
        {rerr && <p className="err" role="alert">{rerr}</p>}
        <div className="tools"><button disabled={!rp}>Make a new recovery code</button></div>
      </form>
      <section className="card"><h2>Lock</h2><p className="muted">The vault locks itself after {String(S.current().me?.settings?.autoLockMinutes ?? 15)} idle minutes, and when this tab is closed.</p><div className="tools"><button onClick={S.lock}>Lock now</button></div></section>
    </div>
  </>;
}
