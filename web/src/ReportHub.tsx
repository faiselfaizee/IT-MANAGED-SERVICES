import { useState, type ReactNode } from 'react';
import { saveFile, useLoad, type Rec } from './http';
import { Act, Empty, Loading } from './ui';

/** One entry on a module's Reports page. `href` opens it; `about` says what it answers. */
export type ReportEntry = { key: string; group: string; title: string; about: string; href: string };

/** A module's Reports page: every report it has, grouped, each with one line on what it shows. */
export function ReportList({ title, intro, entries }: { title: string; intro: string; entries: ReportEntry[] }) {
  const groups = [...new Set(entries.map((e) => e.group))];
  return <>
    <h1>{title}</h1>
    <p className="muted">{intro}</p>
    <div className="report-list">
      {groups.map((g) => <section key={g} aria-labelledby={'rg-' + g}>
        <h2 id={'rg-' + g}>{g}</h2>
        <ul>{entries.filter((e) => e.group === g).map((e) => <li key={e.key}><a href={e.href}>{e.title}</a><span className="muted">{e.about}</span></li>)}</ul>
      </section>)}
    </div>
  </>;
}

/**
 * A report shown as a table from a server report endpoint: { title, about, columns, rows, total, shown }. The first value of each row is the record id
 * and is not shown; `link` turns it into an address for the first visible column. Filters go on the query string; the CSV button sends the same filters.
 */
export function TableReport({ url, back, filters, link, extra }: { url: string; back: string; filters?: (set: (k: string, v: string) => void, f: Record<string, string>) => ReactNode; link?: (id: string, data: Rec) => string | null; extra?: Record<string, string> }) {
  const [f, setF] = useState<Record<string, string>>({ q: '', ...(extra ?? {}) });
  const [typed, setTyped] = useState('');
  const qs = '?' + Object.entries(f).filter(([, v]) => v !== '').map(([k, v]) => k + '=' + encodeURIComponent(v)).join('&');
  const { data, error, loading } = useLoad<Rec>(url + qs, [qs]);
  const set = (k: string, v: string) => setF((x) => ({ ...x, [k]: v }));
  const rows = (data?.rows ?? []) as string[][]; const cols = (data?.columns ?? []) as string[];
  const name = String(data?.key ?? 'report');
  return <>
    <p className="crumb-back"><a href={back}>All reports</a></p>
    <h1>{data?.title ?? 'Report'}</h1>
    {data?.about ? <p className="muted">{String(data.about)}</p> : null}
    <form className="tools" onSubmit={(e) => { e.preventDefault(); set('q', typed.trim()); }}>
      <input type="search" value={typed} onChange={(e) => setTyped(e.target.value)} placeholder="Find…" aria-label="Find in this report" maxLength={100} />
      <button type="submit">Find</button>
      {filters?.(set, f)}
      <Act label="Export (CSV)" run={() => saveFile(url + '.csv' + qs, name + '-report.csv')} />
    </form>
    <Loading loading={loading && !data} error={error} />
    {data && (rows.length === 0 ? <Empty>Nothing matches.</Empty> : <div className="table-wrap"><table className="report-table">
      <thead><tr>{cols.map((c) => <th key={c} scope="col">{c}</th>)}</tr></thead>
      <tbody>{rows.map((r, i) => { const href = link?.(r[0], data); return <tr key={r[0] + ':' + i}>{r.slice(1).map((v, j) => <td key={j}>{j === 0 && href ? <a href={href}>{v || '(no name)'}</a> : v}</td>)}</tr>; })}</tbody>
    </table></div>)}
    {data && Number(data.total) > rows.length && <p className="muted">Showing {rows.length} of {Number(data.total).toLocaleString()} rows. The export has all of them.</p>}
    {data && Number(data.total) <= rows.length && rows.length > 0 && <p className="muted">{rows.length.toLocaleString()} {rows.length === 1 ? 'row' : 'rows'}.</p>}
  </>;
}
