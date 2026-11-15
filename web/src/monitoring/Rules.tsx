import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Empty, Field, Loading, Modal } from '../ui';

const METRICS: [string, string][] = [['offline', 'Device down or not reporting'], ['cpu', 'Processor use'], ['memory', 'Memory use'], ['disk', 'Disk space'], ['check', 'Service check failing'], ['cert', 'Certificate expiring'], ['hardware', 'Hardware fault'],
  ['traffic', 'Port traffic (network devices)'], ['port', 'Watched port down (network devices)'], ['service', 'Named service not running (agent)'], ['autoservice', 'Automatic or chosen service stopped (agent)'], ['process', 'Program not running (agent)'], ['log', 'Device messages (syslog, traps, Windows events)']];
const LEVELS = ['Emergency', 'Alert', 'Critical', 'Error', 'Warning', 'Notice', 'Information', 'Debug'];
const KINDS: [string, string][] = [['computer', 'Computers'], ['server', 'Servers'], ['network', 'Network devices'], ['printer', 'Printers'], ['other', 'Other']];

/** Alert rules: what opens an alert, how serious it is, and whether it mails and opens a ticket. Administrators change them. */
export default function Rules({ admin }: { admin: boolean }) {
  const { data, error, loading, reload } = useLoad<Rec>('/api/monitoring/rules');
  const [edit, setEdit] = useState<Rec | null>(null);
  const items = (data?.items ?? []) as Rec[];
  return <>
    <h1>Alert rules</h1>
    <p className="muted">Every minute each device is checked against the rules that are on. When several rules hit the same thing (a disk below 10% and below 5%), only the most serious opens an alert. An alert clears by itself once the problem has gone for two checks in a row.</p>
    {admin && <div className="tools"><button className="primary" onClick={() => setEdit({ name: '', metric: 'disk', threshold: 10, minutes: 0, severity: 'warning', kinds: [], mail: true, teams: true, ticket: false, ticketPriority: 'high', enabled: true, target: '', eventKind: '', minSeverity: 3, restart: false })}>New rule</button></div>}
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No rules: nothing will open an alert.</Empty> :
      <div className="table-wrap"><table>
        <thead><tr><th>Rule</th><th>When</th><th>Severity</th><th>Customer</th><th>Then</th><th>{admin ? 'On' : 'State'}</th>{admin && <th />}</tr></thead>
        <tbody>{items.map((r) => <tr key={r.id} className={r.enabled ? '' : 'closed'}>
          <td>{r.name}</td><td>{r.description}</td>
          <td><span className={`badge ${r.severity === 'critical' ? 'bad' : 'warn'}`}>{r.severity === 'critical' ? 'Critical' : 'Warning'}</span></td>
          <td>{r.company || <span className="muted">Every customer</span>}</td>
          <td>{[r.mail ? 'Mail' : '', r.teams ? 'Teams' : '', r.ticket ? `Ticket (${r.ticketPriority})` : '', r.restart ? 'Restart' : ''].filter(Boolean).join(', ') || <span className="muted">Show only</span>}</td>
          <td>{admin ? <input type="checkbox" checked={r.enabled} aria-label={`${r.name} on`} onChange={async (e) => { await api(`/api/monitoring/rules/${r.id}`, { method: 'PUT', body: { ...r, enabled: e.target.checked } }); reload(); }} /> : r.enabled ? 'On' : 'Off'}</td>
          {admin && <td><button onClick={() => setEdit(r)}>Change</button> <Act label="Delete" danger confirm={`Delete the rule "${r.name}"? Its open alerts close.`} run={async () => { await api(`/api/monitoring/rules/${r.id}`, { method: 'DELETE' }); reload(); }} /></td>}
        </tr>)}</tbody>
      </table></div>)}
    {edit && <RuleForm rule={edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); reload(); }} />}
  </>;
}

function RuleForm({ rule, onClose, onSaved }: { rule: Rec; onClose: () => void; onSaved: () => void }) {
  const [r, setR] = useState<Rec>({ ...rule }); const [err, setErr] = useState('');
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const set = (k: string, v: unknown) => setR({ ...r, [k]: v });
  const m = r.metric as string;
  const save = async () => {
    try { await api(r.id ? `/api/monitoring/rules/${r.id}` : '/api/monitoring/rules', { method: r.id ? 'PUT' : 'POST', body: { ...r, threshold: Number(r.threshold), minutes: Number(r.minutes), minSeverity: Number(r.minSeverity ?? 3), target: r.target ?? '', eventKind: r.eventKind ?? '', companyId: r.companyId ? Number(r.companyId) : null } }); onSaved(); }
    catch (e) { setErr((e as Error).message); }
  };
  const cos = Array.isArray(companies.data) ? companies.data : ((companies.data as Rec | null)?.items ?? []) as Rec[];
  return <Modal title={r.id ? 'Change rule' : 'New rule'} onClose={onClose}>
    <form className="form" onSubmit={(e) => { e.preventDefault(); void save(); }}>
      <Field label="Name"><input value={r.name} onChange={(e) => set('name', e.target.value)} required maxLength={120} /></Field>
      <Field label="What to watch"><select value={m} onChange={(e) => set('metric', e.target.value)}>{METRICS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
      {(m === 'cpu' || m === 'memory') && <Field label="Above (%)"><input type="number" min={1} max={100} value={r.threshold} onChange={(e) => set('threshold', e.target.value)} /></Field>}
      {(m === 'cpu' || m === 'memory') && <Field label="For at least (minutes; 0 means the latest reading)"><input type="number" min={0} max={1440} value={r.minutes} onChange={(e) => set('minutes', e.target.value)} /></Field>}
      {m === 'offline' && <Field label="Down or silent for (minutes)"><input type="number" min={2} max={10080} value={r.minutes} onChange={(e) => set('minutes', e.target.value)} /></Field>}
      {m === 'disk' && <Field label="Free space below (%)"><input type="number" min={1} max={99} value={r.threshold} onChange={(e) => set('threshold', e.target.value)} /></Field>}
      {m === 'check' && <Field label="Failures in a row"><input type="number" min={1} max={100} value={r.threshold} onChange={(e) => set('threshold', e.target.value)} /></Field>}
      {m === 'cert' && <Field label="Days before expiry"><input type="number" min={1} max={365} value={r.threshold} onChange={(e) => set('threshold', e.target.value)} /></Field>}
      {m === 'traffic' && <Field label="Above (% of the port speed)"><input type="number" min={1} max={100} value={r.threshold} onChange={(e) => set('threshold', e.target.value)} /></Field>}
      {m === 'traffic' && <Field label="For at least (minutes; readings come every 5 minutes)"><input type="number" min={0} max={1440} value={r.minutes} onChange={(e) => set('minutes', e.target.value)} /></Field>}
      {m === 'port' && <Field label="Down for (minutes)"><input type="number" min={1} max={10080} value={r.minutes} onChange={(e) => set('minutes', e.target.value)} /></Field>}
      {m === 'autoservice' && <p className="muted small">Servers: every service that starts automatically is watched, except update helpers and per-user services that stop by themselves. Any device: the services you tick on its Monitoring page. Untick or ignore a service there to leave it out.</p>}
      {(m === 'service' || m === 'process') && <Field label={m === 'service' ? 'Service names (as in Services, or systemd units), separated by commas' : 'Program names (for example sqlservr.exe, nginx), separated by commas'}>
        <textarea rows={2} value={r.target} onChange={(e) => set('target', e.target.value)} placeholder={m === 'service' ? 'Spooler, MSSQLSERVER, W3SVC' : 'sqlservr.exe, java'} /></Field>}
      {m === 'service' && <label className="check"><input type="checkbox" checked={r.restart} onChange={(e) => set('restart', e.target.checked)} /> Ask the agent to restart the service once when the alert opens</label>}
      {m === 'log' && <>
        <Field label="Messages of"><select value={r.eventKind} onChange={(e) => set('eventKind', e.target.value)}><option value="">Every kind</option><option value="syslog">Syslog</option><option value="trap">SNMP traps</option><option value="eventlog">Windows event logs (errors and critical)</option></select></Field>
        <Field label="At this level or worse"><select value={r.minSeverity} onChange={(e) => set('minSeverity', Number(e.target.value))}>{LEVELS.map((l, i) => <option key={l} value={i}>{l}</option>)}</select></Field>
        <Field label="Containing (optional; text, program or trap name)"><input value={r.target} onChange={(e) => set('target', e.target.value)} placeholder="for example: fan, linkDown, 7031" /></Field>
        <Field label="How many messages"><input type="number" min={1} max={10000} value={r.threshold} onChange={(e) => set('threshold', e.target.value)} /></Field>
        <Field label="Within (minutes)"><input type="number" min={1} max={1440} value={r.minutes} onChange={(e) => set('minutes', e.target.value)} /></Field>
      </>}
      <Field label="Severity"><select value={r.severity} onChange={(e) => set('severity', e.target.value)}><option value="warning">Warning</option><option value="critical">Critical</option></select></Field>
      <fieldset><legend>Devices (none ticked means every kind)</legend>{KINDS.map(([k, l]) => <label key={k} className="check"><input type="checkbox" checked={(r.kinds as string[]).includes(k)} onChange={(e) => set('kinds', e.target.checked ? [...r.kinds, k] : (r.kinds as string[]).filter((x) => x !== k))} /> {l}</label>)}</fieldset>
      <Field label="Customer"><select value={r.companyId ?? ''} onChange={(e) => set('companyId', e.target.value || null)}><option value="">Every customer</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
      <label className="check"><input type="checkbox" checked={r.mail} onChange={(e) => set('mail', e.target.checked)} /> Mail the alert recipients</label>
      <label className="check"><input type="checkbox" checked={r.teams !== false} onChange={(e) => set('teams', e.target.checked)} /> Post to the Teams channel</label>
      <label className="check"><input type="checkbox" checked={r.ticket} onChange={(e) => set('ticket', e.target.checked)} /> Open a Service Desk ticket</label>
      {r.ticket && <Field label="Ticket priority"><select value={r.ticketPriority} onChange={(e) => set('ticketPriority', e.target.value)}>{['critical', 'high', 'medium', 'low'].map((p) => <option key={p} value={p}>{p[0].toUpperCase() + p.slice(1)}</option>)}</select></Field>}
      <label className="check"><input type="checkbox" checked={r.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Rule is on</label>
      {err && <p className="err" role="alert">{err}</p>}
      <div className="tools"><button type="submit" className="primary">{r.id ? 'Save rule' : 'Add rule'}</button><button type="button" onClick={onClose}>Cancel</button></div>
    </form>
  </Modal>;
}
