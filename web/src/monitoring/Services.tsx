import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { ServiceControls } from '../assets/Services';
import { Empty, Err, useSubmit } from '../ui';

const STATE: Record<string, string> = { running: 'Running', stopped: 'Stopped', failed: 'Failed', starting: 'Starting', stopping: 'Stopping', paused: 'Paused', inactive: 'Not running', unknown: 'Unknown', missing: 'Not installed' };
const START: Record<string, string> = { auto: 'Automatic', delayed: 'Automatic (delayed)', manual: 'Manual', disabled: 'Disabled', other: 'Other' };
const bad = (s: string) => s === 'stopped' || s === 'failed' || s === 'missing';

/**
 * The services of a computer or server: every service the agent listed, with its state and start type. The automatic ones of a server are watched
 * (a stopped one opens an alert); tick or untick any service, or add one by name that is not on the list.
 */
export function Services({ id, list, at, custom, watched, canEdit, agent, kind, onChange }: { id: string; list: Rec[]; at: string | null; custom: string[]; watched: Rec[]; canEdit: boolean; agent: boolean; kind: string; onChange: () => void }) {
  const ctl = useLoad<Rec>(agent ? `/api/assets/${id}/services` : null, [id]); const c = ctl.data ?? {}; const [over, setOver] = useState<Record<string, Rec>>({});
  const [show, setShow] = useState<'watched' | 'problems' | 'all'>('watched'); const [q, setQ] = useState(''); const [name, setName] = useState('');
  const f = useSubmit(() => { setName(''); onChange(); });
  const set = async (n: string, mode: string | null) => { await api(`/api/monitoring/devices/${id}/services`, { method: 'PUT', body: { name: n, mode } }); onChange(); };
  if (!agent) return <Empty>This device has no agent, so its services are not listed. Watch what it offers (a web page, a port, a certificate) with service checks on the asset's Service checks tab.</Empty>;
  const text = q.trim().toLowerCase();
  const rows = list.map((s) => ({ ...s, ...(over[String(s.name)] ?? {}) })).filter((s) => (show === 'all' || (show === 'watched' ? s.watched : s.watched && bad(s.state))) && (!text || (s.name + ' ' + s.display).toLowerCase().includes(text)));
  const stopped = list.filter((s) => s.watched && bad(s.state)).length;
  const live = (n: string) => watched.find((w) => w.kind === 'service' && String(w.name).toLowerCase() === n.toLowerCase());
  return <>
    {list.length === 0 ? <Empty>The agent has not listed this computer's services yet. It does so a few minutes after it starts (agents from version 3.4).</Empty> : <>
      <div className="tools"><span className="muted">{list.filter((s) => s.watched).length} of {list.length} services watched{stopped > 0 ? `, ${stopped} not running` : ''}{at ? `; listed ${new Date(at).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}` : ''}</span>
        <select value={show} onChange={(e) => setShow(e.target.value as typeof show)} aria-label="Which services"><option value="watched">Watched services</option><option value="problems">Watched and not running</option><option value="all">All services</option></select>
        <input type="search" placeholder="Find a service" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Find a service" /></div>
      {rows.length === 0 ? <Empty>No service matches.</Empty> : <div className="table-wrap"><table>
        <thead><tr><th>Watch</th><th>Service</th><th>State</th><th>Starts</th>{c.canControl && <th><span className="sr-only">Actions</span></th>}</tr></thead>
        <tbody>{rows.map((s) => <tr key={s.name}>
          <td>{canEdit ? <input type="checkbox" checked={s.watched} aria-label={`Watch ${s.name}`} onChange={(e) => void set(s.name, e.target.checked ? 'watch' : 'ignore')} /> : s.watched ? 'Yes' : 'No'}</td>
          <td>{s.display && s.display !== s.name ? s.display : s.name}{s.display && s.display !== s.name && <div className="muted small mono">{s.name}</div>}
            {s.mode && <div className="muted small">{s.mode === 'watch' ? 'You chose to watch it.' : 'You chose to ignore it.'} {canEdit && <button className="link" onClick={() => void set(s.name, null)}>Back to the default</button>}</div>}
            {!s.mode && (s.start === 'auto' || s.start === 'delayed') && s.quiet && <div className="muted small">Automatic, but it often stops by itself, so it is not watched unless you tick it.</div>}</td>
          <td>{s.state === 'running' ? <span className="badge ok">Running</span> : <span className={`badge ${s.watched && bad(s.state) ? 'bad' : ''}`}>{STATE[s.state] ?? s.state}</span>}</td>
          <td>{START[s.start] ?? s.start}</td>{c.canControl && <td><ServiceControls assetId={id} s={s} ctl={c} onState={(v) => setOver((o) => ({ ...o, [String(s.name)]: v }))} /></td>}</tr>)}</tbody></table></div>}
      {kind !== 'server' && <p className="muted small">Only servers watch their automatic services on their own. On this device tick the services that matter.</p>}</>}
    {custom.length > 0 && <><h3>Services you added</h3><div className="table-wrap"><table><thead><tr><th>Service</th><th>State</th>{canEdit && <th />}</tr></thead>
      <tbody>{custom.map((n) => { const w = live(n); return <tr key={n}><td>{n}</td>
        <td>{!w ? <span className="muted">Not reported yet (the agent asks once a minute)</span> : w.running ? <span className="badge ok">Running</span> : <span className="badge bad">{STATE[w.state] ?? w.state}</span>}</td>
        {canEdit && <td><button onClick={() => void set(n, null)}>Remove</button></td>}</tr>; })}</tbody></table></div></>}
    {canEdit && <form className="tools" onSubmit={(e) => { e.preventDefault(); if (name.trim()) void f.submit(() => api(`/api/monitoring/devices/${id}/services`, { method: 'PUT', body: { name: name.trim(), mode: 'watch' } })); }}>
      <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Add a service by name (for example MSSQLSERVER)" aria-label="Service to add" spellCheck={false} />
      <button className="primary" disabled={f.busy || !name.trim()}>Watch it</button></form>}
    <Err text={f.err} />
  </>;
}
