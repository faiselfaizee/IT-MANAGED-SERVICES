import { useState } from 'react';
import { ApiError, api, useLoad, type Rec } from '../http';
import { Badge, Empty, Err, Loading, Modal, fmtDate, useSubmit } from '../ui';

/** What the server said about assets that look like the one being saved: a block (same serial, MAC or host name) or a warning (same IP or name). */
export function matchesOf(e: unknown): { kind: 'duplicate' | 'possible_duplicate'; text: string; matches: Rec[] } | null {
  if (!(e instanceof ApiError) || e.status !== 409) return null;
  const k = e.body?.error;
  if (k !== 'duplicate' && k !== 'possible_duplicate') return null;
  return { kind: k, text: e.message, matches: (e.body?.matches ?? []) as Rec[] };
}

/** Shown in the add and edit forms when the asset may already exist. A warning can be overruled; a block cannot, merge instead. */
export function DuplicateNotice({ info, onAnyway, busy }: { info: NonNullable<ReturnType<typeof matchesOf>>; onAnyway: () => void; busy?: boolean }) {
  const hard = info.kind === 'duplicate';
  return <div className={hard ? 'note bad' : 'note'} role="alert">
    <strong>{hard ? 'This asset already exists.' : 'Possible duplicate.'}</strong>
    <ul>{info.matches.map((m) => <li key={m.id}><a href={`#/assets/${m.id}`} target="_blank" rel="noreferrer">{m.tag} {m.name}</a> <span className="muted">same {(m.why ?? []).join(' and ')}</span></li>)}</ul>
    {hard ? <p className="muted small">A serial number, MAC address or host name that another asset of this customer already has cannot be used twice. Open the existing asset, or merge the two under All assets › Duplicates.</p>
      : <div className="actions"><button className="primary" disabled={busy} onClick={onAnyway}>Add anyway</button></div>}
  </div>;
}

/** Assets of one customer that look like the same device, with a button to merge them into the one that has the agent. */
export default function Duplicates() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/assets/duplicates');
  const [pick, setPick] = useState<Rec | null>(null);
  const groups = (data?.groups ?? []) as Rec[];
  return <>
    <h1>Duplicates</h1>
    <p className="muted">Assets of one customer that share a serial number, MAC address, host name, IP address or name. Merge them to keep one record: the asset with the agent is kept, the other is retired, and its contracts, services and history move over.</p>
    <Loading loading={loading && !data} error={error} />
    {data && (groups.length === 0 ? <Empty>No duplicates found.</Empty> : groups.map((g, n) => <div className="card" key={n} style={{ marginBottom: 12 }}>
      <h3>{g.company || 'No customer'} <span className="muted small">{(g.why as string[]).join(', ')}</span></h3>
      <table><thead><tr><th>Tag</th><th>Name</th><th>Host</th><th>IP</th><th>Serial</th><th>Agent</th><th>Source</th><th>Last seen</th></tr></thead><tbody>
        {(g.items as Rec[]).map((a) => <tr key={a.id}><td><a href={`#/assets/${a.id}`}>{a.tag}</a></td><td>{a.name}</td><td>{a.hostname}</td><td className="mono">{a.ip}</td><td className="mono">{a.serial}</td>
          <td>{a.agent ? <Badge tone="ok">agent</Badge> : <span className="muted">none</span>}</td><td>{a.source}</td><td>{a.lastSeen ? fmtDate(a.lastSeen) : ''}</td></tr>)}</tbody></table>
      <div className="actions"><button onClick={() => setPick(g)}>Merge…</button></div>
    </div>))}
    {pick && <Merge group={pick} onClose={(ch) => { setPick(null); if (ch) reload(); }} />}
  </>;
}

function Merge({ group, onClose }: { group: Rec; onClose: (changed: boolean) => void }) {
  const items = group.items as Rec[];
  const [keep, setKeep] = useState<number>(items[0].id);
  const f = useSubmit(() => onClose(true));
  const others = items.filter((a) => a.id !== keep);
  return <Modal title="Merge duplicates" onClose={() => onClose(false)}><div className="form">
    <p>Choose the asset to keep. {others.length === 1 ? 'The other one is' : 'The others are'} retired, with a note that it was merged, and everything of {others.length === 1 ? 'it' : 'them'} moves over.</p>
    {items.map((a) => <label key={a.id} style={{ display: 'block' }}><input type="radio" name="keep" checked={keep === a.id} onChange={() => setKeep(a.id)} /> <strong>{a.tag}</strong> {a.name} {a.agent && <Badge tone="ok">agent</Badge>} <span className="muted">{a.hostname} {a.ip}</span></label>)}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy} onClick={() => f.submit(() => api('/api/assets/merge', { body: { keepId: keep, mergeIds: others.map((a) => a.id) } }))}>Merge into {items.find((a) => a.id === keep)?.tag}</button></div>
  </div></Modal>;
}
