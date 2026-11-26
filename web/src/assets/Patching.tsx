import { useMemo, useState } from 'react';
import { useLoad, type Rec } from '../http';
import { Badge, Empty, Loading, ago } from '../ui';

/** Which computers with an agent have updates waiting, from the last check of each. A check is run from the computer's Maintenance tab. */
export default function Patching() {
  const { data, error, loading } = useLoad<Rec>('/api/assets/patching');
  const [q, setQ] = useState(''); const [show, setShow] = useState('all');
  const rows = useMemo(() => ((data?.items ?? []) as Rec[]).filter((r) => (q === '' || `${r.name} ${r.tag} ${r.company} ${r.osName}`.toLowerCase().includes(q.toLowerCase()))
    && (show === 'all' || (show === 'waiting' ? Number(r.pending) > 0 : show === 'restart' ? !!r.rebootRequired : r.pending === null || r.pending === undefined))), [data, q, show]);
  const all = (data?.items ?? []) as Rec[];
  const waiting = all.filter((r) => Number(r.pending) > 0).length; const restart = all.filter((r) => r.rebootRequired).length; const never = all.filter((r) => r.pending === null || r.pending === undefined).length;
  return <>
    <h1>Patching</h1>
    <p className="muted">What each computer with the agent last reported as waiting. Open a computer and its <b>Maintenance</b> tab to check for updates and install them.</p>
    <Loading loading={loading && !data} error={error} />
    {data && <>
      <p><Badge tone={waiting ? 'warn' : 'ok'}>{waiting} with updates waiting</Badge> <Badge tone={restart ? 'warn' : undefined}>{restart} need a restart</Badge> <Badge>{never} not checked yet</Badge></p>
      <div className="tools">
        <input type="search" placeholder="Find a computer" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Find a computer" />
        <select value={show} onChange={(e) => setShow(e.target.value)} aria-label="Show"><option value="all">All computers</option><option value="waiting">Updates waiting</option><option value="restart">Restart needed</option><option value="never">Not checked yet</option></select>
      </div>
      {rows.length === 0 ? <Empty>No computers match.</Empty> : <div className="table-wrap"><table>
        <thead><tr><th>Computer</th><th>Customer</th><th>System</th><th>Waiting</th><th>Last update installed</th><th>Checked</th></tr></thead>
        <tbody>{rows.map((r) => <tr key={String(r.id)}>
          <td><a href={`#/assets/${r.id}/maintenance`}><b>{String(r.name)}</b></a> <span className="muted mono small">{String(r.tag)}</span></td>
          <td>{String(r.company || '–')}</td><td className="small">{String(r.osName || r.os)}</td>
          <td>{r.pending === null || r.pending === undefined ? <span className="muted">–</span> : Number(r.pending) === 0 ? <Badge tone="ok">Up to date</Badge> : <Badge tone="warn">{String(r.pending)}</Badge>} {r.rebootRequired ? <Badge tone="warn">Restart</Badge> : null}</td>
          <td>{String(r.lastPatch ?? '–')}</td><td>{r.checkedAt ? ago(String(r.checkedAt)) : <span className="muted">never</span>}</td></tr>)}</tbody></table></div>}
    </>}
  </>;
}
