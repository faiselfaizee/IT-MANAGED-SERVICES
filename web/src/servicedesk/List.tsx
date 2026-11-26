import { useState } from 'react';
import { go, useLoad, type Rec } from '../http';
import { Badge, Empty, Loading, PageSize, ago, usePageSize } from '../ui';
import { Ring } from '../Rings';

export const STATUS: Record<string, string> = { open: 'Open', progress: 'In progress', hold: 'Awaiting customer', resolved: 'Resolved', closed: 'Closed' };
export const KIND: Record<string, string> = { incident: 'Incident', request: 'Service request' };
export const LEVEL: Record<string, string> = { low: 'Low', medium: 'Medium', high: 'High' };
export const PRIORITY: Record<string, string> = { critical: 'Critical', high: 'High', medium: 'Medium', low: 'Low' };
const SLA: Record<string, [string, 'ok' | 'warn' | 'bad' | undefined]> = {
  ok: ['On track', 'ok'], warning: ['Due soon', 'warn'], breached: ['Overdue', 'bad'], paused: ['Paused', undefined], met: ['Met', 'ok'],
};
export const StatusBadge = ({ s }: { s: string }) => <Badge tone={s === 'resolved' || s === 'closed' ? 'ok' : s === 'hold' ? 'warn' : undefined}>{STATUS[s] ?? s}</Badge>;
export const PriorityBadge = ({ p }: { p: string }) => <Badge tone={p === 'critical' ? 'bad' : p === 'high' ? 'warn' : undefined}>{PRIORITY[p] ?? p}</Badge>;
export const KindBadge = ({ k, major }: { k: string; major?: boolean }) => <>{major && <Badge tone="bad">Major incident</Badge>}<Badge>{KIND[k] ?? k}</Badge></>;
export const SlaBadge = ({ s }: { s: string }) => { const [t, tone] = SLA[s] ?? [s, undefined]; return <Badge tone={tone}>{t}</Badge>; };

/** The ticket list; kind narrows it to incidents or to service requests, major to the major incidents. */
export default function List({ me, kind = '', major = false }: { me: Rec | null; kind?: '' | 'incident' | 'request'; major?: boolean }) {
  const staff = !!me?.staff;
  const [status, setStatus] = useState('active'); const [priority, setPriority] = useState(''); const [assignee, setAssignee] = useState('');
  const [text, setText] = useState(''); const [overdue, setOverdue] = useState(false); const [skip, setSkip] = useState(0); const [take, setTake] = usePageSize('tickets', 25);
  const qs = `status=${status === 'all' ? '' : status}&priority=${priority}&assignee=${assignee}&q=${encodeURIComponent(text)}${overdue ? '&overdue=1' : ''}${kind ? '&kind=' + kind : ''}${major ? '&major=1' : ''}&skip=${skip}&take=${take}`;
  const { data, error, loading } = useLoad<Rec>('/api/servicedesk/tickets?' + qs);
  const sum = useLoad<Rec>('/api/servicedesk/summary' + (major ? '?major=1' : kind ? '?kind=' + kind : ''));
  const items = (data?.items ?? []) as Rec[]; const total = (data?.total ?? 0) as number; const s = sum.data;
  const reset = (f: () => void) => { f(); setSkip(0); };
  return <>
    <h1>{major ? 'Major incidents' : kind === 'incident' ? 'Incidents' : kind === 'request' ? 'Service requests' : 'Tickets'}</h1>
    {s && <div className="rings dash-rings">
      <Ring title="Open" unit="open" empty="Nothing open." parts={[
        { key: 'o', label: 'New', value: Number(s.open ?? 0), tone: 'info' },
        { key: 'p', label: 'In progress', value: Number(s.progress ?? 0), tone: 'ok' },
        { key: 'h', label: 'Awaiting customer', value: Number(s.hold ?? 0), tone: 'none' }]} />
      <Ring title="Service level" unit="open" empty="Nothing open." parts={[
        { key: 'in', label: 'On time', value: Math.max(Number(s.open ?? 0) + Number(s.progress ?? 0) + Number(s.hold ?? 0) - Number(s.overdue ?? 0), 0), tone: 'ok' },
        { key: 'late', label: 'Overdue', value: Number(s.overdue ?? 0), tone: 'bad' }]} />
      <Ring title="Finished" unit="tickets" empty="None finished yet." parts={[
        { key: 'r', label: 'Resolved', value: Number(s.resolved ?? 0), tone: 'ok' },
        { key: 'c', label: 'Closed', value: Number(s.closed ?? 0), tone: 'none' }]} />
    </div>}
    {s && <dl className="stats" style={{ marginBottom: 14 }}>
      <div><dt>Open</dt><dd>{(s.open ?? 0) + (s.progress ?? 0)}</dd></div>
      <div><dt>Awaiting customer</dt><dd>{s.hold ?? 0}</dd></div>
      {staff && <div><dt>Unassigned</dt><dd>{s.unassigned ?? 0}</dd></div>}
      {staff && <div><dt>Assigned to me</dt><dd>{s.mine ?? 0}</dd></div>}
      <div><dt>Overdue</dt><dd>{s.overdue ?? 0}</dd></div>
    </dl>}
    <div className="tools">
      <input placeholder="Search number, subject, person, company…" value={text} onChange={(e) => reset(() => setText(e.target.value))} style={{ minWidth: 260 }} aria-label="Search" />
      <select value={status} onChange={(e) => reset(() => setStatus(e.target.value))} aria-label="Status"><option value="active">Not finished</option><option value="all">Any status</option>{Object.entries(STATUS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select>
      <select value={priority} onChange={(e) => reset(() => setPriority(e.target.value))} aria-label="Priority"><option value="">Any priority</option>{Object.entries(PRIORITY).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select>
      {staff && <select value={assignee} onChange={(e) => reset(() => setAssignee(e.target.value))} aria-label="Assigned to"><option value="">Anyone</option><option value="me">Me</option><option value="none">Unassigned</option></select>}
      <label><input type="checkbox" checked={overdue} onChange={(e) => reset(() => setOverdue(e.target.checked))} /> Overdue only</label>
      {me?.canRaise && <button className="primary" onClick={() => go('servicedesk/new' + (kind ? '?kind=' + kind : ''))}>{kind === 'request' ? 'New service request' : kind === 'incident' || major ? 'New incident' : 'New ticket'}</button>}
    </div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No tickets match.</Empty> : <table><thead><tr><th>Ticket</th><th>Subject</th>{!kind && <th>Type</th>}<th>Priority</th><th>Status</th>{staff && <th>Assigned to</th>}<th>Service level</th><th>Updated</th></tr></thead><tbody>
      {items.map((t) => <tr key={t.id} onClick={() => go('servicedesk/' + t.id)} style={{ cursor: 'pointer' }}>
        <td><a href={'#/servicedesk/' + t.id}>{t.number}</a></td>
        <td>{t.subject}<div className="muted"><span className="for-customers">{t.company ? t.company + ' · ' : ''}</span>{t.requester}</div></td>
        {!kind && <td><KindBadge k={t.kind} major={t.major} /></td>}
        <td>{kind && t.major && <Badge tone="bad">Major</Badge>} <PriorityBadge p={t.priority} /></td><td><StatusBadge s={t.status} /></td>
        {staff && <td>{t.assignee || <span className="muted">Unassigned</span>}</td>}
        <td><SlaBadge s={t.sla} /></td><td className="muted">{ago(t.updatedAt)}</td>
      </tr>)}</tbody></table>)}
    {total > 10 && <div className="tools"><button disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - take))}>Previous</button>
      <span className="muted">{skip + 1}–{Math.min(skip + take, total)} of {total}</span><button disabled={skip + take >= total} onClick={() => setSkip(skip + take)}>Next</button><PageSize value={take} onChange={(n) => { setTake(n); setSkip(0); }} /></div>}
  </>;
}
