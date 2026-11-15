import { useState } from 'react';
import { useInterval, useLoad, type Rec } from '../http';
import { ago, Empty, fmtDate, Loading } from '../ui';

export const SEVERITIES = ['Emergency', 'Alert', 'Critical', 'Error', 'Warning', 'Notice', 'Information', 'Debug'];
const KIND: Record<string, string> = { syslog: 'Syslog', trap: 'SNMP trap', eventlog: 'Windows event' };
const tone = (s: number) => (s <= 2 ? 'bad' : s === 3 ? 'bad' : s === 4 ? 'warn' : '');

/** A list of device messages, newest first. With asset set it shows one device's (on the device page). */
export function EventList({ asset, company, kind = '', severity = 7, search = '', take = 50, showDevice = true }: { asset?: string; company?: string; kind?: string; severity?: number; search?: string; take?: number; showDevice?: boolean }) {
  const [skip, setSkip] = useState(0);
  const q = `/api/monitoring/events?kind=${kind}&severity=${severity}&search=${encodeURIComponent(search)}` + (asset ? `&asset=${asset}` : '') + (company ? `&company=${encodeURIComponent(company)}` : '') + `&skip=${skip}&take=${take}`;
  const { data, error, loading, reload } = useLoad<Rec>(q);
  useInterval(reload, 60_000);
  const items = (data?.items ?? []) as Rec[]; const total = Number(data?.total ?? 0);
  return <>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No messages{severity < 7 ? ' at this level or worse' : ''}. Devices send syslog and SNMP traps to the site controller (UDP 514 and 162); agents send Windows event log errors when a rule asks for them.</Empty> :
      <div className="table-wrap"><table>
        <thead><tr><th>When</th><th>Level</th>{showDevice && <th>Device</th>}<th>From</th><th>Message</th></tr></thead>
        <tbody>{items.map((e) => <tr key={e.id}>
          <td title={fmtDate(e.at)}>{ago(e.at)}</td>
          <td><span className={`badge ${tone(e.severity)}`}>{SEVERITIES[e.severity] ?? e.severity}</span></td>
          {showDevice && <td><a href={`#/monitoring/device/${e.assetId}`}>{e.asset}</a><div className="muted small">{e.company}</div></td>}
          <td>{KIND[e.kind] ?? e.kind}<div className="muted small">{e.source}{e.eventId ? ` · event ${e.eventId}` : ''}</div></td>
          <td className="msg">{e.text}</td>
        </tr>)}</tbody>
      </table></div>)}
    {total > take && <div className="tools"><button disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - take))}>Newer</button><span className="muted">{skip + 1}–{Math.min(skip + take, total)} of {total}</span><button disabled={skip + take >= total} onClick={() => setSkip(skip + take)}>Older</button></div>}
  </>;
}

/** Syslog messages, SNMP traps and Windows event log errors from every device. */
export default function Events() {
  const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
  const [kind, setKind] = useState(qs.get('kind') ?? ''); const [severity, setSeverity] = useState(Number(qs.get('severity') ?? 4)); const [search, setSearch] = useState('');
  return <>
    <h1>Device messages</h1>
    <p className="muted">Kept 14 days. Alert rules of the kind "Device messages" turn the ones that matter into alerts.</p>
    <div className="tools">
      <select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Kind"><option value="">Every kind</option><option value="syslog">Syslog</option><option value="trap">SNMP traps</option><option value="eventlog">Windows events</option></select>
      <select value={severity} onChange={(e) => setSeverity(Number(e.target.value))} aria-label="Level">{SEVERITIES.map((s, i) => <option key={s} value={i}>{i === 7 ? 'Every level' : `${s} or worse`}</option>)}</select>
      <input placeholder="Search text, program or device" value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Search messages" />
    </div>
    <EventList key={`${kind}|${severity}|${search}`} kind={kind} severity={severity} search={search} company={qs.get('company') ?? undefined} />
  </>;
}
