import { useCallback, useEffect, useRef, useState } from 'react';
import { api, useInterval, type Rec } from '../http';
import { Act, Badge, Err } from '../ui';
import { LiveLog } from './LiveLog';

const LABEL: Record<string, string> = { running: 'Updating', done: 'Updated', failed: 'Update failed', stalled: 'No answer' };

/** The agent version, whether an update is due, and "Update agent now" with a live log of every step. */
export function AgentUpdate({ id, admin, onDone }: { id: string; admin: boolean; onDone?: () => void }) {
  const [items, setItems] = useState<Rec[]>([]);
  const [st, setSt] = useState<Rec | null>(null);
  const [err, setErr] = useState('');
  const last = useRef(0); const was = useRef('');
  const pull = useCallback(async () => {
    try {
      const r = await api<Rec>(`/api/assets/${id}/agent-update/log?after=${last.current}`);
      const add = (r.items ?? []) as Rec[];
      if (add.length) { last.current = Number(add[add.length - 1].seq); setItems((x) => [...x, ...add]); }
      setSt(r);
      if (was.current === 'running' && r.status !== 'running') onDone?.();
      was.current = String(r.status); setErr('');
    } catch (e) { setErr((e as Error).message); }
  }, [id, onDone]);
  useEffect(() => { void pull(); }, [pull]);
  const running = st?.status === 'running';
  useInterval(() => { void pull(); }, running ? 1500 : null);
  if (!st) return null;
  const due = !!st.latest && !!st.version && st.version !== st.latest;
  const tone = st.status === 'done' ? 'ok' : st.status === 'failed' || st.status === 'stalled' ? 'bad' : running ? 'warn' : undefined;
  return <div className="agent-update">
    <p>Agent version <b className="mono">{String(st.version || 'unknown')}</b>{' '}
      {due ? <Badge tone="warn">update due: {String(st.latest)}</Badge> : st.latest ? <Badge tone="ok">up to date</Badge> : null}{' '}
      {LABEL[String(st.status)] && <Badge tone={tone}>{LABEL[String(st.status)]}</Badge>}{' '}
      {admin && due && !running && <Act label="Update agent now" primary run={async () => {
        last.current = 0; setItems([]); setErr('');
        await api(`/api/assets/${id}/agent-update`, { body: {} });
        was.current = 'running'; await pull();
      }} />}
    </p>
    {due && !st.online && !running && <p className="note">The agent is offline, so it cannot be asked to update. When the computer is on again it updates by itself; otherwise reinstall it through the site controller.</p>}
    {st.status === 'stalled' && <p className="note">The agent did not come back with the new version within 15 minutes. Check that the computer is on and that DeskAsk agent is running, then press Update agent now again.</p>}
    <Err text={err} />
    {(items.length > 0 || running) && <LiveLog items={items} running={running} />}
  </div>;
}
