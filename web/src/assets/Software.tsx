import { useState } from 'react';
import { download, useLoad, type Rec } from '../http';
import { Empty, Loading, Modal } from '../ui';

export default function Software() {
  const [q, setQ] = useState(''); const [company, setCompany] = useState(''); const [os, setOs] = useState(''); const [sort, setSort] = useState('count'); const [pick, setPick] = useState<Rec | null>(null);
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const qs = `q=${encodeURIComponent(q)}&company=${company}&os=${os}&sort=${sort}`;
  const { data, error, loading } = useLoad<Rec>(`/api/assets/software?${qs}`);
  return <>
    <h1>Software inventory</h1>
    <div className="tools"><input placeholder="Search programs or publishers" value={q} onChange={(e) => setQ(e.target.value)} style={{ minWidth: 240 }} />
      <select value={company} onChange={(e) => setCompany(e.target.value)} aria-label="Customer"><option value="">All customers</option>{(Array.isArray(companies.data) ? companies.data : []).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
      <select value={os} onChange={(e) => setOs(e.target.value)} aria-label="Operating system"><option value="">Windows and Linux</option><option value="windows">Windows</option><option value="linux">Linux</option></select>
      <select value={sort} onChange={(e) => setSort(e.target.value)} aria-label="Sort"><option value="count">Most installed first</option><option value="name">By name</option></select>
      <button onClick={() => download(`/api/assets/software/export.csv?${qs}`, 'software.csv')}>Export CSV</button></div>
    <Loading loading={loading && !data} error={error} />
    {data && (data.items.length === 0 ? <Empty>No programs found. Inventory arrives from agents and collectors.</Empty> : <><table><thead><tr><th>Program</th><th>Publisher</th><th className="num">Computers</th><th className="num">Versions</th></tr></thead><tbody>
      {(data.items as Rec[]).map((s) => <tr key={s.name}><td><a href="#/assets/software" onClick={(e) => { e.preventDefault(); setPick(s); }}>{s.name}</a></td><td>{s.publisher}</td><td className="num">{s.computers}</td><td className="num">{s.versions}</td></tr>)}</tbody></table>
      {data.truncated && <p className="muted">Showing the first 1000 programs. Narrow the search to see the rest.</p>}</>)}
    {pick && <Computers prog={pick} qs={`company=${company}&os=${os}`} onClose={() => setPick(null)} />}
  </>;
}
function Computers({ prog, qs, onClose }: { prog: Rec; qs: string; onClose: () => void }) {
  const { data, error, loading } = useLoad<Rec>(`/api/assets/software/computers?name=${encodeURIComponent(prog.name)}&${qs}`);
  return <Modal title={`${prog.name} is installed on`} onClose={onClose}><Loading loading={loading} error={error} />
    {data && <table><thead><tr><th>Computer</th><th>Customer</th><th>Version</th><th>Installed</th></tr></thead><tbody>
      {(data.items as Rec[]).map((c) => <tr key={c.id}><td><a href={`#/assets/${c.id}/apps`} onClick={onClose}>{c.hostname || c.name}</a></td><td>{c.company}</td><td>{c.version}</td><td>{c.installedOn}</td></tr>)}</tbody></table>}
    <div className="actions"><button onClick={onClose}>Close</button></div></Modal>;
}
