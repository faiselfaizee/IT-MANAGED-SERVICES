import { useEffect, useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { ago, Empty, fmtDate, Loading } from '../ui';
import * as S from './session';
import * as C from './crypto';
import { Fields } from './Items';
import { typeDef } from './types';

const left = (s: string) => { const m = Math.max(0, Math.round((new Date(s).getTime() - Date.now()) / 60000)); return m >= 60 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${m} min`; };

function Waiting({ r, title, onDone }: { r: Rec; title: string; onDone: () => void }) {
  const [minutes, setMinutes] = useState(Number(r.minutes)); const [note, setNote] = useState(''); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  const run = async (fn: () => Promise<unknown>) => { setBusy(true); setErr(''); try { await fn(); onDone(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } };
  return <li className="req">
    <div><strong>{r.user}</strong> asks for <strong>{title}</strong> <span className="muted">({typeDef(r.type).label}, {r.vault})</span> for {r.minutes} minutes, {ago(r.at)}
      <div className="said">“{r.reason}”{r.ticket ? <> · ticket <a href={`#/servicedesk?search=${encodeURIComponent(r.ticket)}`}>{r.ticket}</a></> : null}</div></div>
    {!r.publicKey ? <p className="muted">They have not set up their vault, so nothing can be shared with them yet.</p> : <div className="tools">
      <select value={minutes} onChange={(e) => setMinutes(Number(e.target.value))} aria-label="Allow for">{[15, 30, 60, 120, 240, 480, 1440].filter((m) => m <= Number(r.minutes)).map((m) => <option key={m} value={m}>{m < 60 ? `${m} min` : `${m / 60} h`}</option>)}</select>
      <input value={note} onChange={(e) => setNote(e.target.value)} placeholder="Note to them (optional)" aria-label="Note" />
      <button className="primary" disabled={busy} onClick={() => void run(async () => {
        // encrypt the item once more, for the requester only
        const it = await api<S.Item>(`/api/vault/items/${r.itemId}`);
        const data = (await S.data(it)) ?? {};
        const dataEnc = await C.sealFor(String(r.publicKey), { title, type: it.type, data });
        await api(`/api/vault/requests/${r.id}/approve`, { method: 'POST', body: { dataEnc, minutes, note } });
      })}>Approve</button>
      <button disabled={busy} onClick={() => void run(() => api(`/api/vault/requests/${r.id}/deny`, { method: 'POST', body: { note } }))}>Deny</button>
    </div>}
    {r.managed && <p className="muted small">A managed account: the password itself is read through DeskAsk while the approval lasts.</p>}
    {err && <p className="err" role="alert">{err}</p>}
  </li>;
}

function Grant({ g, onDone }: { g: Rec; onDone: () => void }) {
  const [open, setOpen] = useState<Rec | null>(null); const [pw, setPw] = useState(''); const [err, setErr] = useState('');
  return <li className="req">
    <div><strong>{open ? String(open.title) : 'An item'}</strong> <span className="muted">({typeDef(g.type).label}) · approved by {g.grantedBy} · {left(g.expiresAt)} left</span></div>
    {open ? <><Fields type={String(open.type)} data={open.data as S.Data} itemId={g.itemId} />
      {g.managed && (pw ? <p>Password: <code>{pw}</code></p> : <button onClick={async () => { try { setPw(String((await api<Rec>(`/api/vault/items/${g.itemId}/secret`)).password)); } catch (e) { setErr((e as Error).message); } }}>Show the password</button>)}</>
      : <button className="primary" onClick={async () => { try { setOpen(await C.openSealed<Rec>(S.key(), g.dataEnc)); S.note(g.itemId, 'viewed', 'approved request'); } catch (e) { setErr((e as Error).message); } }}>Open</button>}
    <button onClick={async () => { await api(`/api/vault/grants/${g.id}`, { method: 'DELETE' }); onDone(); }}>I am done</button>
    {err && <p className="err" role="alert">{err}</p>}
  </li>;
}

export default function Requests() {
  const approve = useLoad<Rec>('/api/vault/requests?scope=approve');
  const mine = useLoad<Rec>('/api/vault/requests');
  const grants = useLoad<Rec>('/api/vault/grants');
  useInterval(() => { approve.reload(); grants.reload(); }, 30_000);
  const wait = (approve.data?.items ?? []) as Rec[]; const my = (mine.data?.items ?? []) as Rec[]; const gs = (grants.data?.items ?? []) as Rec[];
  const titles = S.useTitles([...wait, ...my].map((r) => ({ id: Number(r.itemId), vaultId: Number(r.vaultId), titleEnc: String(r.titleEnc) })));
  const [, setTick] = useState(0); useEffect(() => { const t = setInterval(() => setTick((n) => n + 1), 30_000); return () => clearInterval(t); }, []);
  const reload = () => { approve.reload(); mine.reload(); grants.reload(); };
  const status: Record<string, string> = { pending: 'Waiting', approved: 'Approved', denied: 'Denied', expired: 'Ended', cancelled: 'Cancelled' };
  return <>
    <h1>Access requests</h1>
    <Loading loading={approve.loading && !approve.data} error={approve.error} />
    {wait.length > 0 && <section className="card"><h2>Waiting for your answer</h2><ul className="plain reqs">{wait.map((r) => <Waiting key={r.id} r={r} title={titles[Number(r.itemId)] ?? '…'} onDone={reload} />)}</ul></section>}
    <section className="card"><h2>Open now</h2>
      {gs.length === 0 ? <Empty>Nothing approved for you right now.</Empty> : <ul className="plain reqs">{gs.map((g) => <Grant key={g.id} g={g} onDone={reload} />)}</ul>}</section>
    <section className="card"><h2>What you asked for</h2>
      {my.length === 0 ? <Empty>You have not asked for anything. Items you may only ask for show “ask to open” in the list.</Empty>
        : <div className="table-wrap"><table><thead><tr><th>Item</th><th>Why</th><th>Asked</th><th>Answer</th><th /></tr></thead>
          <tbody>{my.map((r) => <tr key={r.id}><td>{titles[Number(r.itemId)] ?? '…'}<div className="muted small">{r.vault}</div></td><td>{r.reason}{r.ticket ? <div className="muted small">{r.ticket}</div> : null}</td><td title={fmtDate(r.at)}>{ago(r.at)}</td>
            <td><span className={'badge ' + (r.status === 'approved' ? 'ok' : r.status === 'denied' ? 'bad' : '')}>{status[r.status] ?? r.status}</span>{r.decidedBy ? <div className="muted small">{r.decidedBy}{r.note ? `: ${r.note}` : ''}</div> : null}</td>
            <td>{r.status === 'pending' && <button className="small" onClick={async () => { await api(`/api/vault/requests/${r.id}/cancel`, { method: 'POST', body: {} }); reload(); }}>Cancel</button>}</td></tr>)}</tbody></table></div>}
    </section>
  </>;
}
