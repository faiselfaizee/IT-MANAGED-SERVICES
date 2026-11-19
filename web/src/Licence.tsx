import { useState } from 'react';
import { ApiError, api, useLoad, type Rec } from './http';
import { Act, Badge, Card, Copy, Err, Field, Loading, fmtDate, fmtDay } from './ui';

const NAMES: Record<string, string> = { servicedesk: 'Service Desk', assets: 'Asset management', monitoring: 'Monitoring', adsuite: 'AD Manager', vault: 'Vault', backup: 'Backup' };
/** What each limit counts, by module and limit name (as written in the licence). */
const LIMITS: Record<string, Record<string, string>> = {
  assets: { max_assets: 'Assets in service', customer_write: 'Customer administrators may change data' }, monitoring: { max_devices: 'Monitored devices' },
  servicedesk: { agents: 'Staff accounts (administrators and engineers)' }, adsuite: { max_domains: 'AD domains' }, vault: { max_users: 'Vault users' }, backup: { max_devices: 'Devices with file backup' },
};
const STATE: Record<string, [string, 'ok' | 'warn' | 'bad' | undefined]> = {
  valid: ['Valid', 'ok'], grace: ['Expired, in grace period', 'warn'], expired: ['Expired', 'bad'], missing: ['No licence installed', 'bad'], invalid: ['Not valid', 'bad'], notyetvalid: ['Not valid yet', 'warn'],
};

/** Admin page: what the licence covers, and where to paste a new one. */
export default function Licence() {
  const lic = useLoad<Rec>('/api/platform/license');
  const usage = useLoad<Record<string, number>>('/api/platform/license/usage');
  const act = useLoad<Rec>('/api/platform/license/activation');
  const [token, setToken] = useState(''); const [err, setErr] = useState(''); const [ok, setOk] = useState(''); const [busy, setBusy] = useState(false);
  const [key, setKey] = useState(''); const [keyErr, setKeyErr] = useState(''); const [keyOk, setKeyOk] = useState('');
  const a = act.data?.activation as Rec | undefined;
  const activate = async () => {
    setBusy(true); setKeyErr(''); setKeyOk('');
    try { await api('/api/platform/license/activate', { body: { key: key.trim() } }); setKey(''); setKeyOk('Activated. Renewals and changes now arrive by themselves.'); lic.reload(); act.reload(); usage.reload(); }
    catch (e) { setKeyErr((e as Error).message); }
    finally { setBusy(false); }
  };
  const l = lic.data; const [label, tone] = STATE[l?.state ?? ''] ?? [l?.state ?? '', undefined];
  const mods = (l?.modules ?? []) as Rec[];
  const install = async () => {
    setBusy(true); setErr(''); setOk('');
    try { await api('/api/platform/license', { method: 'PUT', body: { token: token.trim() } }); setToken(''); setOk('The licence is installed.'); lic.reload(); }
    catch (e) { const b = (e as ApiError).body; setErr(b?.reason ? String(b.reason) : (e as Error).message); }
    finally { setBusy(false); }
  };
  return <>
    <h1>Licence</h1>
    <Loading loading={lic.loading && !l} error={lic.error} />
    {l && <Card>
      <p><Badge tone={tone}>{label}</Badge> {l.plan ? <strong style={{ marginLeft: 8 }}>{l.plan}</strong> : null}</p>
      {l.reason && l.state !== 'valid' && <p className="muted">{l.reason}</p>}
      {l.expires && <p className="muted">Valid until {fmtDay(l.expires)}{l.state === 'grace' ? `. Modules stop on ${fmtDay(l.graceUntil)}.` : '.'}</p>}
      <p className="muted">Customer id: {l.tenant}</p>
    </Card>}
    {l?.vendorKey?.problem ? <p role="alert" className="err">{String(l.vendorKey.problem)}</p> : null}
    {l && (l.readOnly ?? []).length > 0 && <p role="alert" className="err">The licence has ended. The modules stay visible with all their data, but nothing can be changed and their background work (monitoring, backups, reminders) has stopped. Renew the licence to switch them back on.</p>}
    {l && <Card title="Included modules">
      <table><thead><tr><th>Module</th><th>Included</th><th>Used / allowed</th></tr></thead><tbody>
        {Object.keys(NAMES).map((k) => {
          const m = mods.find((x) => x.key === k); const lims = (m?.limits ?? {}) as Record<string, number>; const used = usage.data ?? {};
          return <tr key={k}><td>{NAMES[k]}</td><td>{m ? <Badge tone="ok">Yes</Badge> : <Badge>No</Badge>}</td>
            <td>{!m ? null : Object.keys(LIMITS[k] ?? {}).map((name) => {
              const allowed = lims[name]; const u = used[`${k}:${name}`];
              if (name === 'customer_write') return allowed ? <div key={name} className="small">{LIMITS[k][name]}</div> : null;
              const full = allowed != null && u != null && u >= allowed;
              return <div key={name} className="small">{LIMITS[k][name]}: {u != null ? <b className={full ? 'err' : ''} style={{ margin: 0 }}>{u}</b> : '–'} / {allowed != null ? allowed : 'no limit'}{full ? ' (limit reached)' : ''}</div>;
            })}</td></tr>;
        })}
      </tbody></table>
    </Card>}
    <Card title="Licence key (online activation)">
      {a?.activated ? <>
        <p><Badge tone="ok">Activated</Badge> <span className="muted">Key ending <b>{a.keyHint}</b>, activated {fmtDay(a.activatedAt)}.</span></p>
        <p className="muted">This installation asks the licence server once a day for a newer licence, so renewals, upgrades and changes arrive by themselves.
          Last check: {a.lastCheck ? fmtDate(a.lastCheck) : 'not yet'}{a.lastResult ? ' (' + a.lastResult + ')' : ''}.</p>
        <div className="actions">
          <Act label="Check now" primary run={async () => { await api('/api/platform/license/refresh', { method: 'POST' }); lic.reload(); act.reload(); }} />
          <Act label="Release this installation" danger confirm="Release the licence key from this installation? Use this before moving DeskAsk to another server. The current licence keeps working until it ends, but renewals stop arriving here." run={async () => { await api('/api/platform/license/release', { method: 'POST' }); act.reload(); }} />
        </div>
      </> : <div className="form">
        <p className="muted">Type the licence key from your order (DKA-…). The installation fetches its licence from the licence server and keeps it up to date.</p>
        <Field label="Licence key"><input value={key} onChange={(e) => setKey(e.target.value)} placeholder="DKA-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX" spellCheck={false} autoComplete="off" /></Field>
        <Err text={keyErr} />{keyOk && <p role="status">{keyOk}</p>}
        <div className="actions"><button className="primary" disabled={busy || key.trim().length < 10} onClick={activate}>Activate</button></div>
      </div>}
      {act.data?.installation && <p className="muted small">No internet on this server? Give your supplier the customer id <Copy text={String(l?.tenant ?? '')} /> and installation id <Copy text={String(act.data.installation)} />; they can send a licence file to paste below.</p>}
    </Card>
    <Card title="Install a new licence">
      <div className="form">
        <Field label="Licence token"><textarea rows={5} value={token} onChange={(e) => setToken(e.target.value)} spellCheck={false} placeholder="Paste the whole licence text here" /></Field>
        <Err text={err} />{ok && <p role="status">{ok}</p>}
        <div className="actions"><button className="primary" disabled={busy || !token.trim()} onClick={install}>Install licence</button></div>
      </div>
    </Card>
  </>;
}
