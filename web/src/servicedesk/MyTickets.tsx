import { useEffect, useState } from 'react';
import { api, go, useLoad, type Rec } from '../http';
import { Badge, Empty, Loading, ago } from '../ui';
import { PriorityBadge, STATUS } from './List';

const TABS: [string, string][] = [['open', 'Open'], ['hold', 'Waiting for you'], ['done', 'Finished'], ['all', 'All']];
const STATES: Record<string, string> = { open: 'open,progress', hold: 'hold', done: 'resolved,closed', all: '' };

/** When the fix is due, said the way a person would say it. */
export function dueWords(t: Rec, now = Date.now()): string {
  if (t.status === 'resolved' || t.status === 'closed') return (t.status === 'closed' ? 'Closed ' : 'Resolved ') + ago(t.resolvedAt ?? t.updatedAt);
  if (t.status === 'hold') return 'Waiting for your reply';
  if (!t.resolveDue) return '';
  const h = (new Date(t.resolveDue).getTime() - now) / 3600000;
  const span = (x: number) => x < 1 ? 'less than an hour' : x < 48 ? Math.round(x) + (Math.round(x) === 1 ? ' hour' : ' hours') : Math.round(x / 24) + ' days';
  return h >= 0 ? 'We aim to fix it in ' + span(h) : 'Overdue by ' + span(-h);
}

/** An employee's own tickets: what is open, what is waiting for them, what is finished. */
export default function MyTickets({ me }: { me: Rec | null }) {
  const [tab, setTab] = useState('open'); const [text, setText] = useState(''); const [q, setQ] = useState(''); const [err, setErr] = useState('');
  useEffect(() => { const h = setTimeout(() => setQ(text.trim()), 250); return () => clearTimeout(h); }, [text]);
  const sum = useLoad<Rec>('/api/servicedesk/summary');
  const { data, error, loading, reload } = useLoad<Rec>(`/api/servicedesk/tickets?status=${STATES[tab]}&q=${encodeURIComponent(q)}&take=100`, [tab, q]);
  const items = (data?.items ?? []) as Rec[]; const s = sum.data;
  const count = (k: string) => k === 'open' ? Number(s?.open ?? 0) + Number(s?.progress ?? 0) : k === 'hold' ? Number(s?.hold ?? 0) : k === 'done' ? Number(s?.resolved ?? 0) + Number(s?.closed ?? 0) : null;
  const reopen = async (id: number) => { setErr(''); try { await api(`/api/servicedesk/tickets/${id}/status`, { body: { status: 'open' } }); reload(); sum.reload(); } catch (e) { setErr((e as Error).message); } };
  return <>
    <h1>My tickets</h1>
    <div className="tools">
      <nav className="sub" aria-label="Ticket groups">{TABS.map(([k, l]) => <a key={k} href="#/servicedesk/all" className={tab === k ? 'on' : ''} onClick={(e) => { e.preventDefault(); setTab(k); }}>
        {l}{count(k) ? <> <Badge tone={k === 'hold' ? 'warn' : undefined}>{count(k)}</Badge></> : null}</a>)}</nav>
      <input placeholder="Search number or subject" value={text} onChange={(e) => setText(e.target.value)} aria-label="Search" style={{ minWidth: 220 }} />
      {me?.canRaise && <button className="primary" onClick={() => go('servicedesk/new')}>New ticket</button>}
    </div>
    {err && <p className="err" role="alert">{err}</p>}
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>{q ? 'No ticket matches.' : tab === 'hold' ? 'Nothing is waiting for you.' : tab === 'done' ? 'No finished tickets yet.' : 'You have no open tickets.'}</Empty> : <table><thead><tr><th>Ticket</th><th>Subject</th><th>Status</th><th>When</th><th>Updated</th><th /></tr></thead><tbody>
      {items.map((t) => <tr key={t.id} onClick={() => go('servicedesk/' + t.id)} style={{ cursor: 'pointer' }}>
        <td><a href={'#/servicedesk/' + t.id}>{t.number}</a></td>
        <td>{t.subject}{t.asset ? <div className="muted">{t.asset}</div> : null}</td>
        <td>{t.status === 'hold' ? <Badge tone="warn">Waiting for you</Badge> : <Badge tone={t.status === 'resolved' || t.status === 'closed' ? 'ok' : undefined}>{STATUS[t.status] ?? t.status}</Badge>} <PriorityBadge p={t.priority} /></td>
        <td className={t.sla === 'breached' ? 'err' : 'muted'}>{dueWords(t)}</td>
        <td className="muted">{ago(t.updatedAt)}</td>
        <td>{(t.status === 'resolved' || t.status === 'closed') && me?.canRaise ? <button onClick={(e) => { e.stopPropagation(); void reopen(t.id); }}>Reopen</button> : null}</td>
      </tr>)}</tbody></table>)}
  </>;
}
