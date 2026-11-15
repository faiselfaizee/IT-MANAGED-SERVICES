import { useState } from 'react';
import { copyText } from '../ui';
import * as S from './session';
import { strength } from './crypto';

function Strength({ pw }: { pw: string }) {
  const s = strength(pw);
  return pw ? <div className="pw-meter" data-score={s.score}><i style={{ width: `${(s.score + 1) * 20}%` }} /><span>{s.label}</span></div> : null;
}

/** Shows the recovery code once, until the person says they have kept it. */
export function RecoveryCode({ code, onDone, again }: { code: string; onDone: () => void; again?: boolean }) {
  const [ok, setOk] = useState(false);
  const save = () => {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([`DeskAsk Vault recovery code\n\n${code}\n\nKeep this somewhere safe and offline. With it you can set a new master password.\n`], { type: 'text/plain' }));
    a.download = 'deskask-vault-recovery-code.txt'; a.click();
  };
  return <section className="vault-gate">
    <h1>{again ? 'Your new recovery code' : 'Keep your recovery code'}</h1>
    <p>If you forget your master password, this code is the only way back into your vault. Nobody at DeskAsk or your company can reset it for you.</p>
    <p className="recovery" aria-label="Recovery code">{code}</p>
    <div className="tools"><button type="button" onClick={() => copyText(code)}>Copy</button><button type="button" onClick={save}>Save as a text file</button><button type="button" onClick={() => window.print()}>Print</button></div>
    {again && <p className="muted">The old recovery code no longer works.</p>}
    <label className="check"><input type="checkbox" checked={ok} onChange={(e) => setOk(e.target.checked)} /> I have put the code somewhere safe, away from this computer</label>
    <div className="tools"><button className="primary" disabled={!ok} onClick={onDone}>Continue</button></div>
  </section>;
}

/** First visit: choose a master password. Also used after a reset. */
export function SetUp({ reset = false, onStart, onDone }: { reset?: boolean; onStart?: () => void; onDone: () => void }) {
  const [a, setA] = useState(''); const [b, setB] = useState(''); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false); const [code, setCode] = useState('');
  if (code) return <RecoveryCode code={code} onDone={onDone} />;
  const weak = strength(a).score < 2;
  return <form className="vault-gate" onSubmit={async (e) => {
    e.preventDefault(); setErr('');
    if (a.length < 12) return setErr('Use at least 12 characters. A short sentence you can remember works well.');
    if (weak) return setErr('That master password is too easy to guess. Add words or characters.');
    if (a !== b) return setErr('The two passwords are not the same.');
    setBusy(true);
    onStart?.(); // keep this screen up until the recovery code has been seen
    try { setCode(await S.setUp(a, reset)); } catch (x) { setErr((x as Error).message); } finally { setBusy(false); }
  }}>
    <h1>{reset ? 'Start your vault again' : 'Set up your vault'}</h1>
    <p>Your vault is encrypted on this computer with a master password before anything is sent to the server. The server cannot read your passwords, and it cannot recover your master password either.</p>
    <label><span>Master password</span><input type="password" autoComplete="new-password" value={a} onChange={(e) => setA(e.target.value)} autoFocus /></label>
    <Strength pw={a} />
    <label><span>Type it again</span><input type="password" autoComplete="new-password" value={b} onChange={(e) => setB(e.target.value)} /></label>
    <p className="muted small">Not your sign-in password: choose a different one. You will need it each time the vault has locked itself.</p>
    {err && <p className="err" role="alert">{err}</p>}
    <div className="tools"><button className="primary" disabled={busy}>{busy ? 'Making your keys…' : 'Create my vault'}</button></div>
  </form>;
}

/** The vault is locked: master password, or the recovery code to choose a new one, or (last resort) start again. */
export function Unlock({ onReset }: { onReset: () => void }) {
  const [mode, setMode] = useState<'pw' | 'rec' | 'reset'>('pw');
  const [pw, setPw] = useState(''); const [code, setCode] = useState(''); const [np, setNp] = useState('');
  const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  const run = async (f: () => Promise<unknown>) => { setBusy(true); setErr(''); try { await f(); } catch (x) { setErr((x as Error).message); } finally { setBusy(false); } };
  if (mode === 'reset') return <section className="vault-gate">
    <h1>Start again without your old keys</h1>
    <p>Only when both the master password and the recovery code are lost. Your personal vault is emptied, because nobody can open it any more. Shared vaults come back once a manager of each vault opens the Vault.</p>
    <div className="tools"><button className="danger" onClick={onReset}>Start again</button><button onClick={() => setMode('pw')}>Back</button></div>
  </section>;
  return <form className="vault-gate" onSubmit={(e) => { e.preventDefault(); if (mode === 'pw') void run(() => S.unlockWith(pw)); else void run(async () => { if (strength(np).score < 2 || np.length < 12) throw new Error('Choose a new master password of at least 12 characters that is not easy to guess.'); await S.recover(code, np); }); }}>
    <h1>The vault is locked</h1>
    {mode === 'pw' ? <>
      <label><span>Master password</span><input type="password" autoComplete="current-password" value={pw} onChange={(e) => setPw(e.target.value)} autoFocus /></label>
      {err && <p className="err" role="alert">{err}</p>}
      <div className="tools"><button className="primary" disabled={busy || !pw}>{busy ? 'Unlocking…' : 'Unlock'}</button><button type="button" className="link" onClick={() => { setMode('rec'); setErr(''); }}>Forgot the master password?</button></div>
    </> : <>
      <p>Type your recovery code and choose a new master password.</p>
      <label><span>Recovery code</span><input value={code} onChange={(e) => setCode(e.target.value)} placeholder="XXXX-XXXX-XXXX-…" autoFocus spellCheck={false} /></label>
      <label><span>New master password</span><input type="password" autoComplete="new-password" value={np} onChange={(e) => setNp(e.target.value)} /></label>
      <Strength pw={np} />
      {err && <p className="err" role="alert">{err}</p>}
      <div className="tools"><button className="primary" disabled={busy || !code || !np}>{busy ? 'Checking…' : 'Set the new master password'}</button><button type="button" onClick={() => setMode('pw')}>Back</button>
        <button type="button" className="link" onClick={() => setMode('reset')}>I lost the recovery code too</button></div>
    </>}
  </form>;
}

export { Strength };
