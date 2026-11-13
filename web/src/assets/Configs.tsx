import { useEffect, useState } from 'react';
import { api, saveFile, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Err, Loading, ago, fmtBytes, fmtDate } from '../ui';

/** Lines removed from a and added in b, in order; a plain longest-common-subsequence diff, enough for device configurations. */
export function diffLines(a: string, b: string): { kind: ' ' | '+' | '-'; text: string }[] {
  const x = a.split('\n'), y = b.split('\n');
  if (x.length * y.length > 25_000_000) { // too big for the table: show what is new and what is gone
    const sx = new Set(x), sy = new Set(y);
    return [...x.filter((l) => !sy.has(l)).map((text) => ({ kind: '-' as const, text })), ...y.filter((l) => !sx.has(l)).map((text) => ({ kind: '+' as const, text }))];
  }
  const n = x.length, m = y.length; const L: Uint32Array[] = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) L[i][j] = x[i] === y[j] ? L[i + 1][j + 1] + 1 : Math.max(L[i + 1][j], L[i][j + 1]);
  const out: { kind: ' ' | '+' | '-'; text: string }[] = []; let i = 0, j = 0;
  while (i < n && j < m) { if (x[i] === y[j]) { out.push({ kind: ' ', text: x[i] }); i++; j++; } else if (L[i + 1][j] >= L[i][j + 1]) out.push({ kind: '-', text: x[i++] }); else out.push({ kind: '+', text: y[j++] }); }
  while (i < n) out.push({ kind: '-', text: x[i++] }); while (j < m) out.push({ kind: '+', text: y[j++] });
  return out;
}

/** The configuration backups of a network device: the latest text, every kept version, what changed, and a download. */
export function Configs({ id }: { id: string }) {
  const { data, error, loading } = useLoad<Rec>(`/api/assets/${id}/configs`);
  const items = (data?.items ?? []) as Rec[];
  const [sel, setSel] = useState<number | null>(null);
  const [text, setText] = useState<Record<number, string>>({});
  const [mode, setMode] = useState<'text' | 'diff'>('text');
  const [err, setErr] = useState('');
  const cur = sel ?? (items[0]?.id as number | undefined) ?? null;
  const idx = items.findIndex((c) => c.id === cur); const prev = idx >= 0 ? items[idx + 1] : undefined;
  useEffect(() => {
    const need = [cur, mode === 'diff' ? prev?.id : null].filter((x): x is number => typeof x === 'number' && !(x in text));
    if (!need.length) return;
    Promise.all(need.map((c) => api<Rec>(`/api/assets/${id}/configs/${c}`))).then((rs) => setText((t) => ({ ...t, ...Object.fromEntries(rs.map((r) => [Number(r.id), String(r.text)])) }))).catch((e) => setErr((e as Error).message));
  }, [cur, mode, prev?.id, id, text]);
  if (loading && !data) return <Loading loading error={error} />;
  if (error) return <Loading loading={false} error={error} />;
  const c = items[idx];
  return <>
    <p className="muted">Backed up by the controller with the device's other details (every six hours, and on Read now). A new version is kept only when the configuration changed; the latest {data?.keep} are kept. Only administrators can see configurations, and each view is written to the audit log.</p>
    {data?.checkedAt && <p className="muted">Last checked {ago(String(data.checkedAt))}.</p>}
    {data?.error && <p className="note">The last backup failed: {String(data.error)}</p>}
    {items.length === 0 ? <Empty>No configuration yet. Set this device to be read as "Network device" under Overview and save a network device login (SSH or API) under Credentials.</Empty> : <>
      <div className="tools">
        <select value={cur ?? ''} onChange={(e) => setSel(Number(e.target.value))} aria-label="Version">{items.map((v, i) => <option key={v.id} value={v.id}>{fmtDate(String(v.at))}{i === 0 ? ' (latest)' : ''} · {v.lines} lines</option>)}</select>
        <button className={mode === 'text' ? 'primary' : ''} onClick={() => setMode('text')}>Configuration</button>
        <button className={mode === 'diff' ? 'primary' : ''} disabled={!prev} onClick={() => setMode('diff')}>Changes from the version before</button>
        {c && <Act label="Download" run={() => saveFile(`/api/assets/${id}/configs/${c.id}/download`, `config-${c.id}.txt`)} />}
      </div>
      {c && <p className="muted">{c.vendor && <Badge>{String(c.vendor)}</Badge>} Read by {c.source === 'api' ? 'the vendor API' : 'SSH'} · {fmtBytes(Number(c.size))}</p>}
      <Err text={err} />
      {cur != null && mode === 'text' && (text[cur] == null ? <Loading loading error="" /> : <Card title="Configuration"><pre className="config">{text[cur]}</pre></Card>)}
      {cur != null && mode === 'diff' && prev && (text[cur] == null || text[prev.id as number] == null ? <Loading loading error="" /> : <Diff a={text[prev.id as number]} b={text[cur]} />)}
    </>}
  </>;
}

function Diff({ a, b }: { a: string; b: string }) {
  const d = diffLines(a, b); const changed = d.filter((l) => l.kind !== ' ');
  const plus = changed.filter((l) => l.kind === '+').length, minus = changed.length - plus;
  // show changed lines with three lines of context
  const keep = new Set<number>(); d.forEach((l, i) => { if (l.kind !== ' ') for (let k = i - 3; k <= i + 3; k++) keep.add(k); });
  return <Card title={`Changes: ${plus} lines added, ${minus} removed`}>{changed.length === 0 ? <p className="muted">No changes.</p> :
    <pre className="config diff">{d.map((l, i) => keep.has(i) ? <div key={i} className={l.kind === '+' ? 'add' : l.kind === '-' ? 'del' : ''}>{l.kind} {l.text}</div> : (keep.has(i - 1) ? <div key={i} className="muted">…</div> : null))}</pre>}</Card>;
}
