import { useCallback, useEffect, useRef, useState } from 'react';
import { api, useInterval, type Rec } from '../http';
import { Act, Badge, Err } from '../ui';
import { LiveLog } from './LiveLog';

const RUNNING = ['queued', 'running'];
const LABEL: Record<string, string> = { none: 'Not installed', queued: 'Queued', running: 'Installing', done: 'Installed', failed: 'Failed' };

/** Installs the agent through the controller that reaches this device, with a live log of every step from the moment the button is pressed. */
export function AgentInstall({ id, onDone }: { id: string; onDone?: () => void }) {
  const [items, setItems] = useState<Rec[]>([]);
  const [st, setSt] = useState<Rec>({ status: 'none', message: '' });
  const [err, setErr] = useState('');
  const last = useRef(0);
  const was = useRef('none');
  const pull = useCallback(async () => {
    try {
      const r = await api<Rec>(`/api/assets/${id}/agent-install/log?after=${last.current}`);
      const add = (r.items ?? []) as Rec[];
      if (add.length) { last.current = Number(add[add.length - 1].seq); setItems((x) => [...x, ...add]); }
      setSt({ status: r.status, message: r.message });
      if (RUNNING.includes(was.current) && !RUNNING.includes(String(r.status))) onDone?.();
      was.current = String(r.status);
      setErr('');
    } catch (e) { setErr((e as Error).message); }
  }, [id, onDone]);
  useEffect(() => { void pull(); }, [pull]);
  const running = RUNNING.includes(String(st.status));
  useInterval(() => { void pull(); }, running ? 1500 : null);
  const tone = st.status === 'done' ? 'ok' : st.status === 'failed' ? 'bad' : running ? 'warn' : undefined;
  return <div>
    <p>Agent installation: <Badge tone={tone}>{LABEL[String(st.status)] ?? String(st.status)}</Badge>{' '}
      {!running && <Act label={st.status === 'none' ? 'Install agent through a collector' : 'Try again'} primary={st.status === 'none'} run={async () => {
        last.current = 0; setItems([]); setErr('');
        await api(`/api/assets/${id}/agent-install`, { body: {} });
        was.current = 'queued'; setSt({ status: 'queued', message: '' });
        await pull();
      }} />}
    </p>
    {st.status === 'failed' && st.message && <p className="err" role="alert">{String(st.message)}</p>}
    <Err text={err} />
    {(items.length > 0 || running) && <LiveLog items={items} running={running} />}
  </div>;
}
