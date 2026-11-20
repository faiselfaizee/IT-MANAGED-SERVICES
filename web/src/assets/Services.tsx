import { useEffect, useMemo, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Badge, Empty, Err, Field, Loading, Modal, ago } from '../ui';
import * as S from '../vault/session';
import { Unlock } from '../vault/Gate';

const STATE: Record<string, [string, 'ok' | 'warn' | 'bad' | undefined]> = {
  running: ['Running', 'ok'], stopped: ['Stopped', undefined], starting: ['Starting', 'warn'], stopping: ['Stopping', 'warn'], paused: ['Paused', 'warn'],
  failed: ['Failed', 'bad'], inactive: ['Not running', undefined], unknown: ['Unknown', undefined],
};
const START: Record<string, string> = { auto: 'Automatic', delayed: 'Automatic (delayed start)', manual: 'Manual', disabled: 'Disabled', other: 'Other' };

/** Follows an action until the agent reports back, then hands over its result. */
async function follow(assetId: string, jid: string, onStep: (s: string) => void): Promise<Rec> {
  for (let i = 0; i < 120; i++) {
    const r = await api<Rec>(`/api/assets/${assetId}/actions/${jid}`);
    if (['done', 'failed', 'timeout', 'cancelled'].includes(String(r.status))) return r;
    onStep(r.status === 'running' ? 'The computer is working on it…' : 'Waiting for the agent…');
    await new Promise((ok) => setTimeout(ok, i < 10 ? 1500 : 3000));
  }
  throw new Error('No answer from the agent after six minutes. Check Remote activity later.');
}

/**
 * The services of a computer with the agent, as last reported, with start, stop, restart, startup type and (Windows, administrators) the
 * Log On account. Used on the asset page (Services tab) and the Monitoring device page.
 */
export function ServicesPanel({ assetId }: { assetId: string }) {
  const { data, error, loading, reload } = useLoad<Rec>(`/api/assets/${assetId}/services`, [assetId]);
  const [q, setQ] = useState(''); const [show, setShow] = useState('all');
  const [over, setOver] = useState<Record<string, Rec>>({});   // states the computer reported after an action, until the next inventory
  const [busy, setBusy] = useState<Record<string, string>>({}); const [msg, setMsg] = useState<{ name: string; text: string; bad?: boolean } | null>(null);
  const [startup, setStartup] = useState<Rec | null>(null); const [logon, setLogon] = useState<Rec | null>(null);
  const win = data?.platform === 'windows';
  const items = useMemo(() => ((Array.isArray(data?.items) ? data!.items : []) as Rec[]).map((s) => ({ ...s, ...(over[String(s.name)] ?? {}) }))
    .filter((s) => (q === '' || `${s.name} ${s.display ?? ''} ${s.logon ?? ''}`.toLowerCase().includes(q.toLowerCase()))
      && (show === 'all' || (show === 'running' ? s.state === 'running' : show === 'stopped-auto' ? s.state !== 'running' && (s.start === 'auto' || s.start === 'delayed') : s.state !== 'running')))
    .sort((a, b) => String(a.display || a.name).localeCompare(String(b.display || b.name))), [data, over, q, show]);

  const act = async (name: string, action: string, extra: Rec = {}) => {
    setBusy((b) => ({ ...b, [name]: action })); setMsg(null);
    try {
      const { id } = await api<Rec>(`/api/assets/${assetId}/services/action`, { method: 'POST', body: { name, action, ...extra } });
      const r = await follow(assetId, String(id), (t) => setMsg({ name, text: t }));
      if (r.service) setOver((o) => ({ ...o, [name]: r.service as Rec }));
      setMsg({ name, text: String(r.output || (r.status === 'done' ? 'Done.' : 'The action did not finish.')), bad: r.status !== 'done' });
    } catch (e) { setMsg({ name, text: (e as Error).message, bad: true }); }
    finally { setBusy((b) => { const n = { ...b }; delete n[name]; return n; }); }
  };

  if (loading && !data) return <Loading loading error="" />;
  if (error) return <Err text={error} />;
  if (!data?.hasAgent) return <Empty>Services are read and controlled through the agent. Install the agent on this computer to see them.</Empty>;
  const can = !!data.canControl;
  return <>
    <div className="tools">
      <input type="search" placeholder="Find a service" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Find a service" />
      <select value={show} onChange={(e) => setShow(e.target.value)} aria-label="Show"><option value="all">All services</option><option value="running">Running</option><option value="stopped">Not running</option><option value="stopped-auto">Automatic but not running</option></select>
      <button type="button" onClick={reload}>Refresh</button>
      <span className="muted">{data.at ? `Reported ${ago(String(data.at))}. The agent sends the list every few minutes.` : 'The agent has not sent its list of services yet.'}</span>
    </div>
    {msg && <p role="status" className={msg.bad ? 'err' : 'ok-note'}><b>{msg.name}:</b> {msg.text}</p>}
    {items.length === 0 ? <Empty>No services match.</Empty> : <div className="table-wrap"><table className="svc-table">
      <thead><tr><th>Service</th><th>State</th><th>Startup type</th>{win && <th>Log On as</th>}{can && <th><span className="sr-only">Actions</span></th>}</tr></thead>
      <tbody>{items.map((s) => { const name = String(s.name); const [label, tone] = STATE[String(s.state)] ?? [String(s.state), undefined]; const b = busy[name];
        return <tr key={name}>
          <td><b>{s.display || name}</b>{s.display ? <div className="muted mono small">{name}</div> : null}</td>
          <td><Badge tone={tone}>{b ? 'Working…' : label}</Badge></td>
          <td>{START[String(s.start)] ?? s.start}</td>
          {win && <td className="small">{s.logon || <span className="muted">unknown</span>}</td>}
          {can && <td className="svc-acts">
            {s.state !== 'running' && <button type="button" disabled={!!b || s.start === 'disabled'} title={s.start === 'disabled' ? 'A disabled service cannot start; change its startup type first.' : undefined} onClick={() => void act(name, 'start')}>Start</button>}
            {s.state === 'running' && <button type="button" disabled={!!b} onClick={() => { if (confirm(`Stop ${s.display || name}? Programs that need it may stop working.`)) void act(name, 'stop'); }}>Stop</button>}
            {s.state === 'running' && <button type="button" disabled={!!b} onClick={() => { if (confirm(`Restart ${s.display || name}?`)) void act(name, 'restart'); }}>Restart</button>}
            <button type="button" disabled={!!b} onClick={() => setStartup(s)}>Startup type</button>
            {data.canLogon && <button type="button" disabled={!!b} onClick={() => setLogon(s)}>Log On</button>}
          </td>}
        </tr>; })}</tbody></table></div>}
    {startup && <StartupForm s={startup} win={win} onClose={() => setStartup(null)} onPick={(v) => { const n = String(startup.name); setStartup(null); void act(n, 'startup', { startup: v }); }} />}
    {logon && <LogonForm assetId={assetId} s={logon} onClose={() => setLogon(null)} onDone={(text, bad, svc) => { const n = String(logon.name); setLogon(null); setMsg({ name: n, text, bad }); if (svc) setOver((o) => ({ ...o, [n]: svc })); }} />}
  </>;
}

function StartupForm({ s, win, onClose, onPick }: { s: Rec; win: boolean; onClose: () => void; onPick: (v: string) => void }) {
  const [v, setV] = useState(String(s.start === 'other' ? 'manual' : s.start));
  const opts = win ? ['auto', 'delayed', 'manual', 'disabled'] : ['auto', 'disabled'];
  return <Modal title={`Startup type: ${s.display || s.name}`} onClose={onClose}><form className="form" onSubmit={(e) => { e.preventDefault(); onPick(v); }}>
    <fieldset className="radio-list"><legend className="sr-only">Startup type</legend>
      {opts.map((o) => <label key={o}><input type="radio" name="startup" value={o} checked={v === o} onChange={() => setV(o)} /> {win ? START[o] : o === 'auto' ? 'Start at boot (enabled)' : 'Do not start at boot (disabled)'}</label>)}
    </fieldset>
    <div className="actions"><button type="button" onClick={onClose}>Cancel</button><button className="primary" type="submit" disabled={v === s.start}>Change startup type</button></div>
  </form></Modal>;
}

const BUILT_IN: [string, string][] = [['LocalSystem', 'Local System'], ['NT AUTHORITY\\NetworkService', 'Network Service'], ['NT AUTHORITY\\LocalService', 'Local Service']];

/** Log On: a built-in account, or an account typed in or filled from the Vault, with a reason. The password is sent once and never kept. */
function LogonForm({ assetId, s, onClose, onDone }: { assetId: string; s: Rec; onClose: () => void; onDone: (text: string, bad: boolean, svc?: Rec) => void }) {
  const current = String(s.logon ?? '');
  const [kind, setKind] = useState(BUILT_IN.some(([k]) => k.toLowerCase() === current.toLowerCase()) ? current : current ? 'account' : 'LocalSystem');
  const [account, setAccount] = useState(BUILT_IN.some(([k]) => k.toLowerCase() === current.toLowerCase()) ? '' : current);
  const [pw, setPw] = useState(''); const [pw2, setPw2] = useState(''); const [reason, setReason] = useState('');
  const [picker, setPicker] = useState(false); const [err, setErr] = useState(''); const [step, setStep] = useState('');
  const isAccount = kind === 'account';
  const ready = reason.trim() !== '' && (!isAccount || (account.trim() !== '' && pw !== '' && pw === pw2));
  const send = async () => {
    setErr(''); setStep('Sending…');
    try {
      const r = await api<Rec>(`/api/assets/${assetId}/services/logon`, { method: 'POST', body: { name: s.name, account: isAccount ? account.trim() : kind, password: isAccount ? pw : '', reason } });
      setPw(''); setPw2('');
      if (r.kind === 'job') {
        const done = await follow(assetId, String(r.id), setStep);
        onDone(String(done.output || 'Done.'), done.status !== 'done', done.service as Rec | undefined);
        return;
      }
      for (let i = 0; i < 80; i++) {
        const h = await api<Rec>(`/api/assets/${assetId}/services/logon/${r.id}`);
        if (h.state === 'done') { onDone(`${s.display || s.name} now runs as ${account.trim()}. Restart the service for the change to take effect.`, false, { logon: account.trim() }); return; }
        if (h.state === 'failed' || h.state === 'cancelled') { setErr(String(h.error || 'The agent could not change the account.')); setStep(''); return; }
        setStep(h.state === 'fetched' ? 'The computer is changing the account…' : 'Waiting for the agent…');
        await new Promise((ok) => setTimeout(ok, 2000));
      }
      setErr('No answer from the agent yet. Agents older than 6.3 cannot change a service account: update the agent and try again.'); setStep('');
    } catch (e) { setErr((e as Error).message); setStep(''); }
  };
  return <Modal title={`Log On: ${s.display || s.name}`} onClose={onClose}>
    <form className="form" onSubmit={(e) => { e.preventDefault(); if (ready && !step) void send(); }}>
      <p className="muted">The account this service runs as. {current ? <>Now: <b>{current}</b>.</> : null} The change takes effect when the service is next restarted.</p>
      <fieldset className="radio-list"><legend className="sr-only">Log on as</legend>
        {BUILT_IN.map(([k, l]) => <label key={k}><input type="radio" name="logon" checked={kind === k} onChange={() => setKind(k)} /> {l}</label>)}
        <label><input type="radio" name="logon" checked={isAccount} onChange={() => setKind('account')} /> This account</label>
      </fieldset>
      {isAccount && <>
        <div className="actions" style={{ justifyContent: 'flex-start', marginTop: 0 }}><button type="button" onClick={() => setPicker(true)}>Fill from Vault</button></div>
        <Field label="Account" hint="DOMAIN\name, .\name for an account on this computer, or name@domain"><input value={account} onChange={(e) => setAccount(e.target.value)} autoComplete="off" spellCheck={false} /></Field>
        <Field label="Password"><input type="password" value={pw} onChange={(e) => setPw(e.target.value)} autoComplete="new-password" /></Field>
        <Field label="Confirm the password"><input type="password" value={pw2} onChange={(e) => setPw2(e.target.value)} autoComplete="new-password" /></Field>
        {pw2 !== '' && pw !== pw2 && <p className="err small">The passwords are not the same.</p>}
        <p className="muted small">The password goes to the computer once over the agent's encrypted connection and is not kept by DeskAsk. The account is also given the "Log on as a service" right.</p>
      </>}
      <Field label="Reason"><input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} placeholder="For example: SQL Server must run as the new service account (CHG-0042)" /></Field>
      <Err text={err} />{step && <p role="status" className="muted">{step}</p>}
      <div className="actions"><button type="button" onClick={onClose}>Cancel</button><button className="primary" type="submit" disabled={!ready || !!step}>Change account</button></div>
    </form>
    {picker && <VaultPicker assetId={assetId} onClose={() => setPicker(false)} onPick={(u, p) => { setAccount(u); setPw(p); setPw2(p); setPicker(false); }} />}
  </Modal>;
}

/** Picks a login from the Vault: titles and secrets are decrypted in this browser; using one is recorded on the item. Items linked to this asset come first. */
function VaultPicker({ assetId, onClose, onPick }: { assetId: string; onClose: () => void; onPick: (user: string, password: string) => void }) {
  const session = S.useSession();
  const [items, setItems] = useState<S.Item[] | null>(null); const [linked, setLinked] = useState<number[]>([]); const [q, setQ] = useState(''); const [err, setErr] = useState('');
  useEffect(() => { if (!session.me) S.loadMe().catch((e: Error) => setErr(e.message)); }, [session.me]);
  useEffect(() => {
    if (!session.unlocked) return;
    void (async () => {
      try {
        const all = ((await api<Rec>('/api/vault/items')).items ?? []) as S.Item[];
        const mine = ((await api<Rec>(`/api/vault/assets/${assetId}/items`)).items ?? []) as Rec[];
        setLinked(mine.map((m) => Number(m.id)));
        setItems(all.filter((i) => i.canRead && ['login', 'server', 'database', 'network'].includes(i.type)));
      } catch (e) { setErr((e as Error).message); }
    })();
  }, [session.unlocked, assetId]);
  const titles = S.useTitles(items ?? []);
  const list = (items ?? []).filter((i) => q === '' || (titles[i.id] ?? '').toLowerCase().includes(q.toLowerCase()))
    .sort((a, b) => Number(linked.includes(b.id)) - Number(linked.includes(a.id)) || (titles[a.id] ?? '').localeCompare(titles[b.id] ?? ''));
  const pick = async (it: S.Item) => {
    setErr('');
    try {
      const d = await S.data(it);
      let password = d?.password ?? '';
      if (!password && it.managed) password = String((await api<Rec>(`/api/vault/items/${it.id}/secret`)).password ?? '');
      if (!d?.username || !password) { setErr('This item has no user name or no password.'); return; }
      S.note(it.id, 'copied', 'password (service Log On)');
      onPick(String(d.username), password);
    } catch (e) { setErr((e as Error).message); }
  };
  return <Modal title="Fill from Vault" onClose={onClose}>
    {!session.me ? <Loading loading error={err} /> : !session.me.setUp ? <Empty>You have not set up the Vault yet. Open Vault from the menu first.</Empty>
      : !session.unlocked ? <Unlock onReset={() => setErr('Reset your Vault key from the Vault itself.')} />
      : <>
        <input type="search" placeholder="Find a login" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Find a login" style={{ width: '100%', marginBottom: 10 }} />
        {items === null ? <Loading loading error="" /> : list.length === 0 ? <Empty>No logins you can read.</Empty>
          : <ul className="pick-list">{list.slice(0, 100).map((i) => <li key={i.id}><button type="button" onClick={() => void pick(i)}>
            <b>{titles[i.id] ?? '…'}</b>{linked.includes(i.id) && <Badge tone="ok">This asset</Badge>}<span className="muted small">{S.vaultById(i.vaultId)?.name}</span></button></li>)}</ul>}
        <p className="muted small">Using an item is recorded on it, as when you copy its password in the Vault.</p>
      </>}
    <Err text={err} />
  </Modal>;
}

/** The Uninstall form: a reason, then the agent runs the program's silent uninstaller (Windows) or the package manager (Linux). */
export function UninstallForm({ assetId, app, onClose, onDone }: { assetId: string; app: Rec; onClose: () => void; onDone: (text: string, ok: boolean) => void }) {
  const [reason, setReason] = useState(''); const [err, setErr] = useState(''); const [step, setStep] = useState('');
  const run = async () => {
    setErr(''); setStep('Sending…');
    try {
      const { id } = await api<Rec>(`/api/assets/${assetId}/apps/uninstall`, { method: 'POST', body: { name: app.name, version: app.version ?? '', reason } });
      const r = await follow(assetId, String(id), setStep);
      if (r.status === 'done') void api(`/api/assets/${assetId}/actions/refresh`, { method: 'POST', body: {} }).catch(() => undefined);
      onDone(String(r.output || (r.status === 'done' ? 'Uninstalled.' : 'The uninstall did not finish.')), r.status === 'done');
    } catch (e) { setErr((e as Error).message); setStep(''); }
  };
  return <Modal title={`Uninstall ${app.name}`} onClose={onClose}><form className="form" onSubmit={(e) => { e.preventDefault(); if (reason.trim() && !step) void run(); }}>
    <p>{app.name}{app.version ? ` ${app.version}` : ''}{app.publisher ? <span className="muted"> by {app.publisher}</span> : null} is removed from this computer without asking the user. Some programs need a restart to finish.</p>
    <Field label="Reason"><input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} placeholder="For example: not licensed, replaced by the new version" autoFocus /></Field>
    <Err text={err} />{step && <p role="status" className="muted">{step}</p>}
    <div className="actions"><button type="button" onClick={onClose}>Cancel</button><button className="danger" type="submit" disabled={!reason.trim() || !!step}>Uninstall</button></div>
  </form></Modal>;
}

/**
 * The action buttons for one service, for lists outside the asset page (the Monitoring device page). Shows its own progress and result,
 * and passes the state the computer reported afterwards to `onState`.
 */
export function ServiceControls({ assetId, s, ctl, onState }: { assetId: string; s: Rec; ctl: Rec; onState?: (svc: Rec) => void }) {
  const [busy, setBusy] = useState(''); const [msg, setMsg] = useState<{ text: string; bad?: boolean } | null>(null);
  const [startup, setStartup] = useState(false); const [logon, setLogon] = useState(false);
  const name = String(s.name); const win = ctl.platform === 'windows';
  if (!ctl.canControl || !ctl.hasAgent) return null;
  const act = async (action: string, extra: Rec = {}) => {
    setBusy(action); setMsg(null);
    try {
      const { id } = await api<Rec>(`/api/assets/${assetId}/services/action`, { method: 'POST', body: { name, action, ...extra } });
      const r = await follow(assetId, String(id), (t) => setMsg({ text: t }));
      if (r.service) onState?.(r.service as Rec);
      setMsg({ text: String(r.output || (r.status === 'done' ? 'Done.' : 'The action did not finish.')), bad: r.status !== 'done' });
    } catch (e) { setMsg({ text: (e as Error).message, bad: true }); } finally { setBusy(''); }
  };
  return <div className="svc-acts">
    {s.state !== 'running' && <button type="button" disabled={!!busy || s.start === 'disabled'} onClick={() => void act('start')}>Start</button>}
    {s.state === 'running' && <button type="button" disabled={!!busy} onClick={() => { if (confirm(`Stop ${s.display || name}?`)) void act('stop'); }}>Stop</button>}
    {s.state === 'running' && <button type="button" disabled={!!busy} onClick={() => { if (confirm(`Restart ${s.display || name}?`)) void act('restart'); }}>Restart</button>}
    <button type="button" disabled={!!busy} onClick={() => setStartup(true)}>Startup type</button>
    {ctl.canLogon && <button type="button" disabled={!!busy} onClick={() => setLogon(true)}>Log On</button>}
    {msg && <span role="status" className={'small ' + (msg.bad ? 'err' : 'muted')} style={{ flexBasis: '100%', textAlign: 'right' }}>{msg.text}</span>}
    {startup && <StartupForm s={s} win={win} onClose={() => setStartup(false)} onPick={(v) => { setStartup(false); void act('startup', { startup: v }); }} />}
    {logon && <LogonForm assetId={assetId} s={s} onClose={() => setLogon(false)} onDone={(text, bad, svc) => { setLogon(false); setMsg({ text, bad }); if (svc) onState?.(svc); }} />}
  </div>;
}
