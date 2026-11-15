import { useEffect, useMemo, useState } from 'react';
import { api, apiText, type Rec } from '../http';
import { Empty, Loading } from '../ui';
import * as S from './session';
import * as C from './crypto';
import { passwordFields, typeDef } from './types';

type Row = { id: number; vaultId: number; title: string; type: string; field: string; pw: string; age: number; managed: boolean };
const DAY = 86_400_000;

/** Everything here is worked out in this browser: the server never sees the passwords. The breach check sends only 5 characters of each password's SHA-1 hash. */
export default function Health() {
  const session = S.useSession();
  const [rows, setRows] = useState<Row[] | null>(null); const [err, setErr] = useState(''); const [vault, setVault] = useState('');
  const [breach, setBreach] = useState<Record<string, number>>({}); const [checking, setChecking] = useState(''); const [breachOff, setBreachOff] = useState('');
  useEffect(() => { void (async () => {
    try {
      const items = ((await api<Rec>('/api/vault/items')).items as S.Item[]).filter((i) => i.canRead && i.dataEnc);
      const out: Row[] = [];
      for (const i of items) {
        const d = await S.data(i); if (!d) continue; const t = await S.title(i);
        const since = new Date(String(i.pwChangedAt ?? i.createdAt)).getTime();
        for (const f of passwordFields(i.type)) if (d[f]) out.push({ id: i.id, vaultId: i.vaultId, title: t, type: i.type, field: typeDef(i.type).fields.find((x) => x.k === f)!.label, pw: d[f], age: Math.floor((Date.now() - since) / DAY), managed: i.managed });
      }
      setRows(out);
    } catch (e) { setErr((e as Error).message); }
  })(); }, [session.vaults.length]);
  const shown = useMemo(() => (rows ?? []).filter((r) => !vault || String(r.vaultId) === vault), [rows, vault]);
  const counts = useMemo(() => { const m = new Map<string, number>(); for (const r of rows ?? []) m.set(r.pw, (m.get(r.pw) ?? 0) + 1); return m; }, [rows]);
  const weak = shown.filter((r) => C.strength(r.pw).score <= 1);
  const reused = shown.filter((r) => (counts.get(r.pw) ?? 0) > 1);
  const old = shown.filter((r) => r.age > 365 && !r.managed);
  const breached = shown.filter((r) => (breach[r.pw] ?? 0) > 0);
  const bad = new Set([...weak, ...reused, ...old, ...breached].map((r) => r.id + r.field));
  const score = shown.length ? Math.round(100 * (1 - bad.size / shown.length)) : 100;
  const check = async () => {
    setBreachOff(''); const found: Record<string, number> = { ...breach }; const todo = [...new Set(shown.map((r) => r.pw))].filter((p) => !(p in found));
    let n = 0;
    try {
      for (const p of todo) {
        const h = await C.sha1Hex(p);
        found[p] = C.breachCount(await apiText(`/api/vault/breach/${h.slice(0, 5)}`), h);
        n++; setChecking(`Checked ${n} of ${todo.length}…`);
      }
      setBreach(found); setChecking('');
    } catch (e) { setChecking(''); setBreachOff((e as Error).message); }
  };
  const List = ({ title, items, why }: { title: string; items: Row[]; why: (r: Row) => string }) => <section className="card health-list">
    <h2>{title} <span className="count">{items.length}</span></h2>
    {items.length === 0 ? <Empty>None.</Empty> : <ul className="plain">{items.map((r) => <li key={r.id + r.field}><a href={`#/vault?item=${r.id}`}>{r.title}</a> <span className="muted small">{r.field} · {S.vaultById(r.vaultId)?.name} · {why(r)}</span></li>)}</ul>}
  </section>;
  return <>
    <h1>Password health</h1>
    <Loading loading={!rows && !err} error={err} />
    {rows && <>
      <div className="tools">
        <select value={vault} onChange={(e) => setVault(e.target.value)} aria-label="Vault"><option value="">Every vault I can open</option>{session.vaults.map((v) => <option key={v.id} value={v.id}>{v.name}</option>)}</select>
        {session.me?.settings?.breachCheck !== false && <button onClick={() => void check()} disabled={!!checking}>{checking || 'Check against known breaches'}</button>}
      </div>
      {breachOff && <p className="err" role="alert">{breachOff}</p>}
      <div className="health-head">
        <p className="health-score"><b>{score}</b><span>out of 100</span></p>
        <dl className="stats">
          <div><dt>Passwords</dt><dd>{shown.length}</dd></div>
          <div><dt>Weak</dt><dd>{weak.length}</dd></div>
          <div><dt>Used more than once</dt><dd>{reused.length}</dd></div>
          <div><dt>Older than a year</dt><dd>{old.length}</dd></div>
          <div><dt>Found in breaches</dt><dd>{Object.keys(breach).length ? breached.length : '–'}</dd></div>
        </dl>
      </div>
      <div className="grid">
        {Object.keys(breach).length > 0 && <List title="Found in known breaches" items={breached} why={(r) => `seen ${breach[r.pw].toLocaleString()} times`} />}
        <List title="Weak" items={weak} why={(r) => C.strength(r.pw).label.toLowerCase()} />
        <List title="Used more than once" items={reused} why={(r) => `in ${counts.get(r.pw)} places`} />
        <List title="Not changed for over a year" items={old} why={(r) => `${Math.floor(r.age / 30)} months old`} />
      </div>
      <p className="muted small">Only items you can open are counted. Managed accounts are changed by DeskAsk on schedule and are not counted as old.</p>
    </>}
  </>;
}
