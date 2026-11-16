import { useState } from 'react';
import { api, useHash, useInterval, useLoad, type Rec } from '../http';
import { ago, Act, Copy, Empty, Err, fmtBytes, fmtDate, Loading, Modal, useSubmit } from '../ui';
import { CountUp, MiniRing, Ring, type Part } from '../Rings';
import { BarList } from '../monitoring/Dashboard';
import { Plans, PlanPage } from './Plans';

const n = (v: unknown) => Number(v ?? 0);
export const TOOLS: Record<string, string> = {
  deskask: 'DeskAsk file backup', veeam: 'Veeam', wsb: 'Windows Server Backup', mars: 'Azure Recovery Services agent (MARS)', mabs: 'Azure Backup Server (MABS/DPM)',
  synology: 'Synology', qnap: 'QNAP', acronis: 'Acronis', azure: 'Microsoft Azure Backup', macrium: 'Macrium', other: 'Other',
};
const STATUS: Record<string, [string, string]> = {
  success: ['Success', 'ok'], warning: ['Warning', 'warn'], failed: ['Failed', 'bad'], missed: ['Missed', 'bad'], waiting: ['Waiting for a first result', ''], ignored: ['Not watched', ''],
};
const AGE: Record<string, string> = { day: 'Good backup within a day', week: 'Last good backup 1 to 7 days ago', older: 'Last good backup over a week ago', never: 'Never a good backup' };
const SOURCE: Record<string, string> = { agent: 'Agent', syslog: 'Syslog', mail: 'Report mail', api: 'Report address' };

const coKey = (m: Rec) => (m.companyId == null ? (m.company === 'All customers' ? 'all' : 'none') : String(m.companyId));
export const jobsHref = (co: string, filter = '') => '#/backup/jobs' + (() => { const q = [co !== 'all' ? `company=${co}` : '', filter].filter(Boolean).join('&'); return q ? '?' + q : ''; })();

export function statusParts(m: Rec, co: string): Part[] {
  return [
    { key: 'success', label: 'Success', value: n(m.success), tone: 'ok', href: jobsHref(co, 'status=success') },
    { key: 'warning', label: 'Warning', value: n(m.warning), tone: 'warn', href: jobsHref(co, 'status=warning') },
    { key: 'failed', label: 'Failed', value: n(m.failed), tone: 'bad', href: jobsHref(co, 'status=failed') },
    { key: 'missed', label: 'Missed', value: n(m.missed), tone: 'bad', href: jobsHref(co, 'status=missed') },
    { key: 'waiting', label: 'Waiting', value: n(m.waiting), tone: 'none', href: jobsHref(co, 'status=waiting') },
  ];
}

export function StatusBadge({ s }: { s: string }) {
  const [w, t] = STATUS[s] ?? [s, ''];
  return <span className={`badge ${t}`}>{w}</span>;
}

const MENU: { group: string; items: { key: string; label: string; admin?: boolean }[] }[] = [
  { group: '', items: [{ key: '', label: 'Dashboard' }, { key: 'jobs', label: 'Backup jobs' }, { key: 'plans', label: 'File backups' }, { key: 'devices', label: 'Devices with backup software' }] },
  { group: 'Set-up', items: [{ key: 'settings', label: 'Alerts', admin: true }, { key: 'senders', label: 'Report mails', admin: true }, { key: 'keys', label: 'Report address', admin: true }, { key: 'connect', label: 'How to connect' }] },
];

/** The Backup module: watches the backup software customers already run. Routes inside #/backup. */
export default function BackupApp() {
  const [, a, b] = useHash();
  const me = useLoad<Rec>('/api/platform/me');
  const role = String(me.data?.role ?? '');
  const admin = role === 'admin'; const canEdit = admin || role === 'engineer';
  const section = a === 'customer' ? '' : a === 'job' ? 'jobs' : a === 'plan' ? 'plans' : a ?? '';
  let page;
  switch (a) {
    case 'customer': page = <CustomerDash key={b} id={b ?? 'all'} />; break;
    case 'jobs': page = <Jobs key={location.hash} />; break;
    case 'job': page = <JobPage key={b} id={b ?? ''} canEdit={canEdit} />; break;
    case 'devices': page = <Devices />; break;
    case 'plans': page = <Plans admin={admin} />; break;
    case 'plan': page = <PlanPage key={b} id={b ?? ''} admin={admin} canRun={canEdit} />; break;
    case 'settings': page = admin ? <Settings /> : <Dashboard />; break;
    case 'senders': page = admin ? <Senders /> : <Dashboard />; break;
    case 'keys': page = admin ? <Keys /> : <Dashboard />; break;
    case 'connect': page = <Connect />; break;
    default: page = <Dashboard />;
  }
  return <div className="modshell">
    <aside className="modnav" aria-label="Backup">
      <div className="modnav-head"><strong>Backup</strong></div>
      {MENU.map((g) => { const items = g.items.filter((i) => !i.admin || admin); return items.length === 0 ? null : <div key={g.group} className="modnav-group">
        {g.group && <h4>{g.group}</h4>}
        {items.map((i) => <a key={i.key} href={'#/backup' + (i.key ? '/' + i.key : '')} className={section === i.key ? 'on' : ''}>{i.label}</a>)}</div>; })}
    </aside>
    <main className="modmain">{page}</main>
  </div>;
}

/** One tile per customer, worst first, with a small ring of its jobs' last results. */
function Dashboard() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/backup/dashboard');
  useInterval(reload, 60_000);
  const [q, setQ] = useState('');
  if (loading && !data) return <Loading loading error={error} />;
  if (error) return <Loading loading={false} error={error} />;
  const tiles = (Array.isArray(data?.tiles) ? data!.tiles : []) as Rec[];
  const all = (data?.all ?? {}) as Rec;
  if (data?.restricted && tiles.length === 1) return <CustomerDash id={coKey(tiles[0])} />;
  const list = tiles.filter((t) => !q || String(t.company).toLowerCase().includes(q.toLowerCase()));
  return <>
    <header className="dash-head"><h1>Backup</h1><p className="muted">{tiles.length} customers · {n(all.jobs)} jobs · {n(all.failed)} failed, {n(all.missed)} missed, {n(all.warning)} with a warning</p></header>
    {tiles.length === 0 ? <div className="card"><h2 style={{ marginTop: 0 }}>No backup results yet</h2>
      <p>Backup reads the results of the backup software your customers already use. Results come from the agent (Veeam, Windows Server Backup, Azure Backup), syslog from NAS devices, report mails and a report address for scripts.</p>
      <p><a className="button primary" href="#/backup/connect">How to connect each tool</a> {n(all.devicesWithTools) > 0 && <span className="muted">The agent has found backup software on {n(all.devicesWithTools)} devices; their first results arrive within 15 minutes.</span>}</p></div> : <>
      <div className="tools"><input placeholder="Find a customer" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Find a customer" /><a className="button" href={jobsHref('all', 'status=alert')}>Jobs with an alert</a></div>
      <div className="co-tiles">
        {!q && <Tile m={all} index={0} />}
        {list.map((t, i) => <Tile key={coKey(t)} m={t} index={i + 1} />)}
      </div></>}
  </>;
}

function Tile({ m, index }: { m: Rec; index: number }) {
  const co = coKey(m); const delay = Math.min(index, 12) * 70;
  const bad = n(m.failed) + n(m.missed);
  return <a className={`co-tile${co === 'all' ? ' all' : ''}${bad > 0 ? ' attn' : n(m.warning) > 0 ? ' warn' : ''}`} href={`#/backup/customer/${co}`}>
    <div className="co-tile-head"><b className="co-name">{m.company}</b><span className="muted"><CountUp value={n(m.jobs)} delay={delay} /> jobs</span></div>
    <div className="co-tile-body">
      <MiniRing parts={statusParts(m, co)} label={`Backup jobs of ${m.company}`} delay={delay} />
      <ul className="co-figs">
        <li className={n(m.failed) > 0 ? 'bad' : ''}><i className="dot tone-bad" aria-hidden="true" /><span>Failed</span><b><CountUp value={n(m.failed)} delay={delay} /></b></li>
        <li className={n(m.missed) > 0 ? 'bad' : ''}><i className="dot tone-bad" aria-hidden="true" /><span>Missed</span><b><CountUp value={n(m.missed)} delay={delay} /></b></li>
        <li className={n(m.warning) > 0 ? 'warn' : ''}><i className="dot tone-warn" aria-hidden="true" /><span>Warning</span><b><CountUp value={n(m.warning)} delay={delay} /></b></li>
        <li><i className="dot tone-ok" aria-hidden="true" /><span>Success</span><b><CountUp value={n(m.success)} delay={delay} /></b></li>
        <li className={n(m.ageNever) + n(m.ageOlder) > 0 ? 'bad' : ''}><span>No good backup this week</span><b><CountUp value={n(m.ageNever) + n(m.ageOlder)} delay={delay} /></b></li>
      </ul>
    </div>
  </a>;
}

/** One customer's backup rings; every part opens the job list filtered to it. */
function CustomerDash({ id }: { id: string }) {
  const { data, error, loading, reload } = useLoad<Rec>('/api/backup/dashboard');
  useInterval(reload, 60_000);
  if (loading && !data) return <Loading loading error={error} />;
  if (error) return <Loading loading={false} error={error} />;
  const tiles = (Array.isArray(data?.tiles) ? data!.tiles : []) as Rec[];
  const m = id === 'all' ? (data?.all as Rec | undefined) : tiles.find((t) => coKey(t) === id);
  if (!m) return <Empty>This customer has no backup jobs.</Empty>;
  const co = id; const bt = (m.byTool ?? {}) as Rec;
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/backup">Backup</a><span aria-hidden="true">›</span><span aria-current="page">{m.company}</span></nav>
    <header className="dash-head"><h1>{m.company}</h1><p className="muted">{n(m.jobs)} jobs on {n(m.devices)} devices · {n(m.alerts)} with an open alert</p>
      <a className="button primary" href={jobsHref(co)}>View all jobs</a></header>
    <div className="rings dash-rings">
      <Ring title="Last result" unit="jobs" delay={0} empty="No jobs yet." parts={statusParts(m, co)} />
      <Ring title="Last good backup" unit="jobs" delay={120} empty="No watched jobs." parts={[
        { key: 'day', label: 'Within a day', value: n(m.ageDay), tone: 'ok', href: jobsHref(co, 'age=day') },
        { key: 'week', label: '1 to 7 days ago', value: n(m.ageWeek), tone: 'warn', href: jobsHref(co, 'age=week') },
        { key: 'older', label: 'Over a week ago', value: n(m.ageOlder), tone: 'bad', href: jobsHref(co, 'age=older') },
        { key: 'never', label: 'Never', value: n(m.ageNever), tone: 'none', href: jobsHref(co, 'age=never') }]} />
      <BarList title="Jobs by software" empty="No jobs yet." rows={Object.keys(TOOLS).filter((k) => n(bt[k]) > 0).map((k) => ({ key: k, label: TOOLS[k], value: n(bt[k]), href: jobsHref(co, 'tool=' + k) }))} />
    </div>
    <p className="muted tile-foot">A job is missed when nothing has arrived for its interval plus the grace hours in Backup › Alerts. Jobs that are not watched are left out of the rings.</p>
  </>;
}

const FILTERS: Record<string, Record<string, string>> = {
  status: { ...Object.fromEntries(Object.entries(STATUS).map(([k, v]) => [k, v[0]])), alert: 'With an open alert' },
  age: AGE, tool: TOOLS,
};

function Jobs() {
  const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
  const company = qs.get('company') ?? ''; const asset = qs.get('asset') ?? '';
  const filters = Object.keys(FILTERS).map((k) => [k, qs.get(k) ?? ''] as const).filter(([k, v]) => v && FILTERS[k][v]);
  const [q, setQ] = useState('');
  const url = '/api/backup/jobs?q=' + encodeURIComponent(q) + (company ? '&company=' + encodeURIComponent(company) : '') + (asset ? '&asset=' + encodeURIComponent(asset) : '') + filters.map(([k, v]) => `&${k}=${v}`).join('');
  const { data, error, loading, reload } = useLoad<Rec>(url);
  useInterval(reload, 60_000);
  const items = (Array.isArray(data?.items) ? data!.items : []) as Rec[];
  const without = (key: string) => { const x = new URLSearchParams(qs); x.delete(key); const t = x.toString(); return '#/backup/jobs' + (t ? '?' + t : ''); };
  const coName = company === 'none' ? 'No customer' : company ? String(items[0]?.company ?? 'Customer') : '';
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/backup">Backup</a><span aria-hidden="true">›</span>
      {company && <><a href={`#/backup/customer/${company}`}>{coName}</a><span aria-hidden="true">›</span></>}
      <span aria-current="page">{filters.length ? filters.map(([k, v]) => FILTERS[k][v]).join(', ') : 'All jobs'}</span></nav>
    <h1>Backup jobs</h1>
    {(company || asset || filters.length > 0) && <div className="filter-chips">{company && <a href={without('company')} title="Show every customer">{coName} ✕</a>}
      {asset && <a href={without('asset')} title="Show every device">{String(items[0]?.assetName ?? 'Device')} ✕</a>}
      {filters.map(([k, v]) => <a key={k} href={without(k)} title="Remove this filter">{FILTERS[k][v]} ✕</a>)}</div>}
    <div className="tools"><input placeholder="Search job, device or customer" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search jobs" /></div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No jobs match.</Empty> : <div className="table-wrap"><table>
      <thead><tr><th>Status</th><th>Job</th><th>Software</th><th>Device</th><th>Customer</th><th>Last result</th><th>Last good backup</th><th>Expected</th></tr></thead>
      <tbody>{items.map((j) => <tr key={j.id}>
        <td><StatusBadge s={j.status} />{j.ticketNumber && <div className="muted small">{j.ticketNumber}</div>}</td>
        <td><a href={`#/backup/job/${j.id}`}>{j.name}</a><div className="muted small">{SOURCE[j.source] ?? j.source}</div></td>
        <td>{j.toolName}</td><td>{j.assetName || <span className="muted">–</span>}</td><td>{j.company || <span className="muted">–</span>}</td>
        <td>{j.lastAt ? ago(j.lastAt) : <span className="muted">none yet</span>}</td>
        <td>{j.lastSuccessAt ? ago(j.lastSuccessAt) : <span className="muted">never</span>}</td>
        <td>{n(j.everyHours) > 0 ? `every ${j.everyHours} h` : <span className="muted">no schedule</span>}</td>
      </tr>)}</tbody></table></div>)}
    {n(data?.total) > items.length && <p className="muted">Showing {items.length} of {n(data?.total)}; narrow the search to see the rest.</p>}
  </>;
}

function JobPage({ id, canEdit }: { id: string; canEdit: boolean }) {
  const { data, error, loading, reload } = useLoad<Rec>(`/api/backup/jobs/${encodeURIComponent(id)}`);
  const [edit, setEdit] = useState(false);
  if (loading && !data) return <Loading loading error={error} />;
  if (error || !data) return <Loading loading={false} error={error || 'Not found'} />;
  const j = data.job as Rec; const runs = (Array.isArray(data.runs) ? data.runs : []) as Rec[]; const mo = (data.month ?? {}) as Rec;
  const co = j.companyId == null ? 'none' : String(j.companyId);
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/backup">Backup</a><span aria-hidden="true">›</span><a href={`#/backup/customer/${co}`}>{j.company || 'No customer'}</a><span aria-hidden="true">›</span><span aria-current="page">{j.name}</span></nav>
    <header className="dash-head"><h1>{j.name}</h1><StatusBadge s={j.status} />
      {canEdit && <><button onClick={() => setEdit(true)}>Change</button>
        <Act label="Delete" danger confirm={`Delete the job "${j.name}" and its history? If the software reports it again, it comes back as a new job.`} run={async () => { await api(`/api/backup/jobs/${j.id}`, { method: 'DELETE' }); location.hash = '#/backup/jobs'; }} /></>}</header>
    {j.alert && <p className="note">Alert open since {fmtDate(j.alertAt ?? j.lastAt)}: {j.alert === 'missed' ? 'no result arrived in time' : j.alert === 'warning' ? 'the last run finished with a warning' : `the last ${n(j.fails) > 1 ? n(j.fails) + ' runs' : 'run'} failed`}{j.ticketNumber ? ` · ticket ${j.ticketNumber}` : ''}.</p>}
    <div className="grid">
      <section className="card"><h2 style={{ marginTop: 0 }}>Job</h2><dl className="kv">
        <dt>Software</dt><dd>{j.toolName}</dd><dt>Device</dt><dd>{j.assetId ? <a href={`#/assets/${j.assetId}`}>{j.assetName}</a> : j.assetName || '–'}</dd>
        <dt>Customer</dt><dd>{j.company || '–'}</dd><dt>Reported by</dt><dd>{SOURCE[j.source] ?? j.source}</dd>
        <dt>Expected</dt><dd>{n(j.everyHours) > 0 ? `every ${j.everyHours} hours (missed after ${n(j.everyHours) + n(data.graceHours)} hours without a result)` : 'no schedule (never missed)'}</dd>
        <dt>Watched</dt><dd>{j.watched ? 'Yes' : 'No: no status and no alerts'}</dd>
        {j.notes && <><dt>Notes</dt><dd style={{ whiteSpace: 'pre-wrap' }}>{j.notes}</dd></>}
      </dl></section>
      <section className="card"><h2 style={{ marginTop: 0 }}>Last 30 days</h2>
        <Ring title="Runs" unit="runs" empty="No runs in the last 30 days." parts={[
          { key: 's', label: 'Success', value: n(mo.success), tone: 'ok' }, { key: 'w', label: 'Warning', value: n(mo.warning), tone: 'warn' }, { key: 'f', label: 'Failed', value: n(mo.failed), tone: 'bad' }]} />
        <dl className="kv"><dt>Last run</dt><dd>{j.lastAt ? `${fmtDate(j.lastAt)} (${ago(j.lastAt)})` : 'none yet'}</dd><dt>Last good backup</dt><dd>{j.lastSuccessAt ? `${fmtDate(j.lastSuccessAt)} (${ago(j.lastSuccessAt)})` : 'never'}</dd></dl>
      </section>
    </div>
    <h2>Runs</h2>
    {runs.length === 0 ? <Empty>No runs yet.</Empty> : <div className="table-wrap"><table>
      <thead><tr><th>Result</th><th>Finished</th><th>Took</th><th>Size</th><th>Message</th></tr></thead>
      <tbody>{runs.map((r) => <tr key={r.id}><td><StatusBadge s={r.result} /></td><td>{fmtDate(r.ended)}</td><td>{r.minutes != null ? `${r.minutes} min` : '–'}</td>
        <td>{n(r.bytes) > 0 ? fmtBytes(n(r.bytes)) : '–'}</td><td className="small" style={{ maxWidth: 520, overflowWrap: 'anywhere' }}>{r.message || <span className="muted">–</span>}</td></tr>)}</tbody></table></div>}
    {edit && <EditJob j={j} onClose={() => setEdit(false)} onSaved={() => { setEdit(false); reload(); }} />}
  </>;
}

function EditJob({ j, onClose, onSaved }: { j: Rec; onClose: () => void; onSaved: () => void }) {
  const [every, setEvery] = useState(String(j.everyHours)); const [watched, setWatched] = useState(!!j.watched); const [notes, setNotes] = useState(String(j.notes ?? ''));
  const { err, busy, submit } = useSubmit(onSaved);
  return <Modal title={`Change ${j.name}`} onClose={onClose}>
    <form className="form" onSubmit={(e) => { e.preventDefault(); void submit(() => api(`/api/backup/jobs/${j.id}`, { method: 'PUT', body: { everyHours: Number(every), watched, notes } })); }}>
      <label><span>Expected every (hours; 24 for daily, 168 for weekly, 0 for no schedule)</span><input type="number" min={0} max={744} value={every} onChange={(e) => setEvery(e.target.value)} /></label>
      <label className="check"><input type="checkbox" checked={watched} onChange={(e) => setWatched(e.target.checked)} /> Watch this job (status, alerts, dashboard)</label>
      <label><span>Notes</span><textarea rows={3} value={notes} onChange={(e) => setNotes(e.target.value)} /></label>
      <Err text={err} />
      <div className="actions"><button type="button" onClick={onClose}>Cancel</button><button className="primary" disabled={busy}>Save</button></div>
    </form>
  </Modal>;
}

function Devices() {
  const { data, error, loading } = useLoad<Rec[]>('/api/backup/devices');
  const list = Array.isArray(data) ? data : [];
  return <>
    <h1>Devices with backup software</h1>
    <p className="muted">What the agent found on each device in the last week. Its results are read every 15 minutes.</p>
    <Loading loading={loading && !data} error={error} />
    {data && (list.length === 0 ? <Empty>The agent has not found Veeam, Windows Server Backup or Azure Backup on any device yet.</Empty> : <div className="table-wrap"><table>
      <thead><tr><th>Device</th><th>Customer</th><th>Backup software</th><th>Jobs</th><th>Seen</th></tr></thead>
      <tbody>{list.map((d) => <tr key={d.assetId}><td><a href={`#/assets/${d.assetId}`}>{d.name}</a><div className="muted small">{d.tag}</div></td><td>{d.company || '–'}</td>
        <td>{(Array.isArray(d.tools) ? d.tools : []).map((t: Rec) => t.name).join(', ')}</td>
        <td>{n(d.jobs) > 0 ? <a href={jobsHref('all', 'asset=' + d.assetId)}>{d.jobs}</a> : <span className="muted">no results yet</span>}</td><td>{ago(d.seenAt)}</td></tr>)}</tbody></table></div>)}
  </>;
}

function useCompanies() { const c = useLoad<Rec[]>('/api/assets/companies'); return Array.isArray(c.data) ? c.data : []; }

function Settings() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/backup/settings');
  const companies = useCompanies();
  const [f, setF] = useState<Rec | null>(null); const [msg, setMsg] = useState(''); const [err, setErr] = useState('');
  if (loading && !data) return <Loading loading error={error} />;
  const s = f ?? data ?? {};
  const set = (k: string, v: unknown) => setF({ ...s, [k]: v });
  const own = (s.companies ?? {}) as Record<string, string>;
  const save = async () => { setErr(''); setMsg(''); try { await api('/api/backup/settings', { method: 'PUT', body: s }); setMsg('Saved.'); setF(null); reload(); } catch (e) { setErr((e as Error).message); } };
  return <>
    <h1>Backup alerts</h1>
    <section className="card"><div className="form">
      <label><span>Send alerts to (everyone who looks after backups; commas between addresses)</span><input value={s.recipients ?? ''} onChange={(e) => set('recipients', e.target.value)} /></label>
      <div className="row">
        <label><span>Alert after this many failed runs in a row</span><input type="number" min={1} max={20} value={s.failuresBeforeAlert ?? 1} onChange={(e) => set('failuresBeforeAlert', Number(e.target.value))} /></label>
        <label><span>Grace before a job counts as missed (hours)</span><input type="number" min={0} max={72} value={s.graceHours ?? 6} onChange={(e) => set('graceHours', Number(e.target.value))} /></label>
      </div>
      <label className="check"><input type="checkbox" checked={!!s.alertWarnings} onChange={(e) => set('alertWarnings', e.target.checked)} /> A run that finishes with a warning alerts too</label>
      <label className="check"><input type="checkbox" checked={s.ticket ?? true} onChange={(e) => set('ticket', e.target.checked)} /> Open a Service Desk ticket for each alert</label>
      <label><span>Ticket priority</span><select value={s.ticketPriority ?? 'high'} onChange={(e) => set('ticketPriority', e.target.value)}>{['low', 'medium', 'high', 'critical'].map((p) => <option key={p} value={p}>{p}</option>)}</select></label>
      <label className="check"><input type="checkbox" checked={s.mailResolved ?? true} onChange={(e) => set('mailResolved', e.target.checked)} /> Mail again when a job is back to normal</label>
    </div></section>
    <section className="card"><h2 style={{ marginTop: 0 }}>A customer's own recipients</h2><p className="muted">They get their own customer's alerts as well as the list above.</p>
      {companies.length === 0 ? <Empty>No customers yet.</Empty> : <table><tbody>{companies.map((c) => <tr key={c.id}><td>{c.name}</td>
        <td><input style={{ width: '100%' }} placeholder="it@customer.example" value={own[String(c.id)] ?? ''} onChange={(e) => set('companies', { ...own, [String(c.id)]: e.target.value })} aria-label={`Recipients for ${c.name}`} /></td></tr>)}</tbody></table>}
    </section>
    <div className="actions" style={{ justifyContent: 'flex-start' }}><button className="primary" onClick={() => void save()}>Save</button>{msg && <span className="muted" role="status">{msg}</span>}<Err text={err} /></div>
  </>;
}

function Senders() {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/backup/senders');
  const companies = useCompanies();
  const [pattern, setPattern] = useState(''); const [company, setCompany] = useState(''); const [tool, setTool] = useState(''); const [err, setErr] = useState('');
  const list = Array.isArray(data) ? data : [];
  const add = async () => {
    setErr('');
    try { await api('/api/backup/senders', { method: 'POST', body: { pattern, companyId: company ? Number(company) : null, tool } }); setPattern(''); reload(); } catch (e) { setErr((e as Error).message); }
  };
  return <>
    <h1>Report mails</h1>
    <p>Backup software that mails a report after each job (Veeam, Acronis, Synology, QNAP, Macrium, Azure Backup and most others) can send it to the Service Desk mailbox. Mail from the senders listed here is read as a backup result and never becomes a ticket. The result and job come from the subject; the customer from the sender.</p>
    <p className="muted">Needs the mailbox under Administration › Mail › Incoming mail. A sender is an address (backup@nas.customer.com) or a whole domain (@customer.com).</p>
    <section className="card"><div className="row">
      <label><span>Sender</span><input value={pattern} onChange={(e) => setPattern(e.target.value)} placeholder="backup@nas.customer.com or @customer.com" /></label>
      <label><span>Customer</span><select value={company} onChange={(e) => setCompany(e.target.value)}><option value="">No customer</option>{companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
      <label><span>Software (when the mail does not say)</span><select value={tool} onChange={(e) => setTool(e.target.value)}><option value="">Work it out from the mail</option>{Object.entries(TOOLS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></label>
    </div><div className="actions" style={{ justifyContent: 'flex-start' }}><button className="primary" disabled={!pattern.trim()} onClick={() => void add()}>Add sender</button><Err text={err} /></div></section>
    <Loading loading={loading && !data} error={error} />
    {data && (list.length === 0 ? <Empty>No senders yet.</Empty> : <table><thead><tr><th>Sender</th><th>Customer</th><th>Software</th><th /></tr></thead>
      <tbody>{list.map((s) => <tr key={s.id}><td>{s.pattern}</td><td>{s.company || '–'}</td><td>{s.tool ? TOOLS[s.tool] ?? s.tool : 'From the mail'}</td>
        <td><Act label="Remove" danger run={async () => { await api(`/api/backup/senders/${s.id}`, { method: 'DELETE' }); reload(); }} /></td></tr>)}</tbody></table>)}
  </>;
}

function Keys() {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/backup/keys');
  const companies = useCompanies();
  const [name, setName] = useState(''); const [company, setCompany] = useState(''); const [token, setToken] = useState(''); const [err, setErr] = useState('');
  const list = Array.isArray(data) ? data : [];
  const url = location.origin + '/api/public/backup/report';
  const create = async () => {
    setErr('');
    try { const r = await api<Rec>('/api/backup/keys', { method: 'POST', body: { name, companyId: company ? Number(company) : null } }); setToken(String(r.token)); setName(''); reload(); } catch (e) { setErr((e as Error).message); }
  };
  const example = `$body = @{ job = "Robocopy to USB"; result = "success"; device = $env:COMPUTERNAME; tool = "other"; message = "" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "${url}" -Headers @{ Authorization = "Bearer <key>" } -ContentType "application/json" -Body $body`;
  return <>
    <h1>Report address</h1>
    <p>Scripts and tools that can call a web address after a job (Robocopy, rsync, SQL Server agent jobs, a NAS task, Acronis or Veeam post-job scripts) send their result here with a key. Each key belongs to one customer.</p>
    <dl className="kv"><dt>Address</dt><dd><Copy text={url} /></dd><dt>Method</dt><dd>POST, JSON, header <code>Authorization: Bearer &lt;key&gt;</code></dd>
      <dt>Fields</dt><dd><code>job</code> and <code>result</code> (success, warning or failed) are needed; <code>device</code>, <code>tool</code>, <code>started</code>, <code>ended</code>, <code>message</code>, <code>bytes</code> and <code>key</code> (sending the same key twice stores the run once) may be added.</dd></dl>
    <pre className="term" style={{ minHeight: 0 }}>{example}</pre>
    <section className="card"><div className="row">
      <label><span>Name</span><input value={name} onChange={(e) => setName(e.target.value)} placeholder="Acme file server scripts" /></label>
      <label><span>Customer</span><select value={company} onChange={(e) => setCompany(e.target.value)}><option value="">No customer</option>{companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
    </div><div className="actions" style={{ justifyContent: 'flex-start' }}><button className="primary" disabled={!name.trim()} onClick={() => void create()}>Create key</button><Err text={err} /></div></section>
    {token && <p className="note">The new key is shown only now. Copy it into the script: <Copy text={token} /></p>}
    <Loading loading={loading && !data} error={error} />
    {data && (list.length === 0 ? <Empty>No keys yet.</Empty> : <table><thead><tr><th>Name</th><th>Customer</th><th>Starts with</th><th>Last used</th><th /></tr></thead>
      <tbody>{list.map((k) => <tr key={k.id}><td>{k.name}</td><td>{k.company || '–'}</td><td><code>{k.prefix}…</code></td><td>{k.lastUsed ? ago(k.lastUsed) : 'never'}</td>
        <td><Act label="Revoke" danger confirm={`Revoke the key "${k.name}"? Scripts using it will be refused.`} run={async () => { await api(`/api/backup/keys/${k.id}`, { method: 'DELETE' }); reload(); }} /></td></tr>)}</tbody></table>)}
  </>;
}

/** How each tool reaches DeskAsk. */
function Connect() {
  const rows: [string, string, string][] = [
    ['Veeam Backup & Replication, Veeam Agent for Windows', 'Agent', 'Install the DeskAsk agent (5.1 or later) on the Veeam server or the computer with Veeam Agent. It reads the "Veeam Backup" and "Veeam Agent" event logs every 15 minutes. Veeam can also send syslog to the site controller (Options › Event Forwarding) or report mails.'],
    ['Windows Server Backup', 'Agent', 'The agent on the server reads the backup history (Get-WBJob). Nothing to set up.'],
    ['Azure Recovery Services agent (MARS)', 'Agent', 'The agent on the protected server reads the Azure Backup job history (Get-OBJob). Nothing to set up.'],
    ['Azure Backup Server (MABS) / System Center DPM', 'Agent', 'The agent on the MABS or DPM server reads the jobs of the last three days (Get-DPMJob) per protection group and data source.'],
    ['Synology (Hyper Backup, Active Backup for Business)', 'Syslog or mail', 'In Log Center (or Active Backup › Settings › Log forwarding) send logs to the site controller\'s address, UDP 514. The NAS must be an asset of that site. Or list its notification sender under Report mails.'],
    ['QNAP (Hybrid Backup Sync)', 'Syslog or mail', 'Notification Center › System logs › send to a syslog server (the site controller, UDP 514), with the NAS as an asset of that site. Or mail notifications from a listed sender.'],
    ['Acronis, Macrium, Commvault and others', 'Mail or report address', 'Point the product\'s email notifications at the Service Desk mailbox and list the sender under Report mails, or call the report address from a post-job script.'],
    ['Microsoft Azure Backup (Recovery Services vault alerts)', 'Mail', 'Send the vault\'s backup alerts (Azure Monitor action group email) to the Service Desk mailbox and list azure-noreply@microsoft.com under Report mails with the customer.'],
  ];
  return <>
    <h1>How to connect each tool</h1>
    <p className="muted">DeskAsk only reads results; it never starts, stops or changes a backup. A new job appears with its first result and is expected daily; change that on the job if it runs weekly or more often.</p>
    <table><thead><tr><th>Software</th><th>Route</th><th>What to do</th></tr></thead><tbody>{rows.map(([a, b, c]) => <tr key={a}><td><b>{a}</b></td><td>{b}</td><td>{c}</td></tr>)}</tbody></table>
  </>;
}
