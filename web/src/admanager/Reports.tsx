import { useState } from 'react';
import { download, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Loading, fmtDate } from '../ui';
import { DomainPick, useDomain } from './AdApp';

export default function Reports({ domains }: { domains: Rec[] }) {
  const [domain, pick] = useDomain(domains);
  const cat = useLoad<Rec[]>('/api/adsuite/reports');
  const [key, setKey] = useState('inactive-users'); const [days, setDays] = useState(90); const [run, setRun] = useState(0);
  const def = ((cat.data ?? []) as Rec[]).find((c) => c.key === key);
  const q = `report=${key}${def?.days ? '&days=' + days : ''}`;
  const rep = useLoad<Rec>(domain && run > 0 ? `/api/adsuite/domains/${domain.id}/report?${q}` : null, [run]);
  if (!domain) return null;
  const rows = (rep.data?.items ?? []) as Rec[];
  return <>
    <div className="tools"><DomainPick domains={domains} value={domain} onPick={pick} />
      <select aria-label="Report" value={key} onChange={(e) => { setKey(e.target.value); const d = ((cat.data ?? []) as Rec[]).find((c) => c.key === e.target.value); if (d?.defaultDays) setDays(Number(d.defaultDays)); }}>
        {((cat.data ?? []) as Rec[]).map((c) => <option key={c.key} value={c.key}>{c.title}</option>)}</select>
      {def?.days ? <label className="inline">Days <input aria-label="Days" type="number" min={1} max={3650} value={days} onChange={(e) => setDays(Number(e.target.value))} style={{ width: 80 }} /></label> : null}
      <button className="primary" onClick={() => setRun(run + 1)}>Run</button>
      {rep.data && <Act label="Download CSV" run={() => download(`/api/adsuite/domains/${domain.id}/report?${q}&format=csv`, `${key}.csv`)} />}</div>
    {def && <p className="muted">{String(def.description)}</p>}
    <Loading loading={rep.loading} error={rep.error} />
    {rep.data && <Card title={`${String(rep.data.title)}: ${rows.length}${rep.data.truncated ? '+' : ''}`}>
      {rows.length === 0 ? <Empty>Nothing to report. That is good news.</Empty> : <table><thead><tr><th>Name</th><th>Sign-in</th><th>Last sign-in</th><th>Expires</th><th /></tr></thead><tbody>
        {rows.map((r) => <tr key={r.dn as string}><td>{r.name}<div className="muted small">{r.description || r.dns}</div></td><td>{r.sam}</td><td>{r.lastLogon ? fmtDate(r.lastLogon) : '–'}</td>
          <td>{r.accountExpires ? fmtDate(r.accountExpires) : '–'}</td><td>{r.disabled && <Badge tone="bad">Disabled</Badge>}{r.locked && <Badge tone="bad">Locked</Badge>}</td></tr>)}</tbody></table>}
      {rep.data.truncated ? <p className="muted small">The list was cut short. Narrow the days or download what is here.</p> : null}
    </Card>}
  </>;
}
