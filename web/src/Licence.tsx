import { useState } from 'react';
import { ApiError, api, useLoad, type Rec } from './http';
import { Badge, Card, Err, Field, Loading, fmtDay } from './ui';

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
  const [token, setToken] = useState(''); const [err, setErr] = useState(''); const [ok, setOk] = useState(''); const [busy, setBusy] = useState(false);
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
    <Card title="Install a new licence">
      <div className="form">
        <Field label="Licence token"><textarea rows={5} value={token} onChange={(e) => setToken(e.target.value)} spellCheck={false} placeholder="Paste the whole licence text here" /></Field>
        <Err text={err} />{ok && <p role="status">{ok}</p>}
        <div className="actions"><button className="primary" disabled={busy || !token.trim()} onClick={install}>Install licence</button></div>
      </div>
    </Card>
  </>;
}
