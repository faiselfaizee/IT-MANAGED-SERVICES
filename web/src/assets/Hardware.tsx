import { useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, ago, fmtBytes, fmtDate } from '../ui';

/** How a controller reads a device: only whether it answers, or its details too. */
export const MODES: [string, string][] = [['', 'Not watched'], ['ping', 'Ping only'], ['device', 'Ping and ports (no login)'], ['windows', 'Windows (WinRM)'],
  ['ssh', 'Linux (SSH)'], ['snmp', 'Printer, switch or firewall (SNMP)'], ['vmware', 'VMware ESXi or vCenter']];
const READING = ['windows', 'ssh', 'snmp', 'vmware'];
const LOGIN_HINT: Record<string, string> = {
  vmware: 'Uses the VMware logins saved under Credentials for this customer (hardware category VMware and Nutanix). A read-only account is enough.',
  snmp: 'Uses the SNMP communities saved under Credentials for this customer.', windows: 'Uses the Windows logins saved under Credentials for this customer.', ssh: 'Uses the Linux logins saved under Credentials for this customer.',
};

/** Which controller watches this device and how; "Read now" asks it to read the details at once. */
export function Watch({ id, d, onChange }: { id: string; d: Rec; onChange: () => void }) {
  const w = (d.watch ?? {}) as Rec;
  const cols = useLoad<Rec>('/api/assets/collectors');
  const [mode, setMode] = useState<string>(String(w.mode ?? '')); const [col, setCol] = useState<string>(String(w.collectorId ?? ''));
  const [err, setErr] = useState('');
  const items = ((cols.data?.items ?? []) as Rec[]);
  const reading = !!w.readRequested;
  useInterval(() => onChange(), reading ? 3000 : null);
  const changed = mode !== String(w.mode ?? '') || col !== String(w.collectorId ?? '');
  return <Card title="Read by a controller">
    <div className="row">
      <Field label="Controller"><select value={col} onChange={(e) => setCol(e.target.value)}><option value="">None</option>{items.map((c) => <option key={c.id} value={c.id}>{c.name}{c.company ? ` (${c.company})` : ''}{c.online ? '' : ' - offline'}</option>)}</select></Field>
      <Field label="Read as"><select value={mode} onChange={(e) => setMode(e.target.value)}>{MODES.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
    </div>
    {LOGIN_HINT[mode] && <p className="muted">{LOGIN_HINT[mode]}</p>}
    <p>
      <Act label="Save" primary={changed} run={async () => {
        setErr(''); try { await api(`/api/assets/${id}/monitor`, { body: { mode, collectorId: mode ? col : '' } }); onChange(); } catch (e) { setErr((e as Error).message); }
      }} />{' '}
      {!changed && READING.includes(String(w.mode)) && w.collectorId && <Act label="Read now" run={async () => { setErr(''); try { await api(`/api/assets/${id}/read-now`, { body: {} }); onChange(); } catch (e) { setErr((e as Error).message); } }} />}{' '}
      {reading && <Badge tone="warn">Reading…</Badge>}
    </p>
    <Dl rows={[['Answers', w.up == null ? '' : w.up ? 'Yes' : 'No'], ['Checked', w.checkedAt ? ago(String(w.checkedAt)) : ''], ['Details read', d.agent?.inventoryAt && d.agent?.source === 'collector' ? fmtDate(String(d.agent.inventoryAt)) : '']]} />
    <p className="muted">A failed read is written to the Logs tab with the reason (for example a refused login).</p>
    <Err text={err} />
  </Card>;
}

const STATE_TONE: Record<string, 'ok' | 'warn' | 'bad' | undefined> = { OK: 'ok', Warning: 'warn', Critical: 'bad', green: 'ok', yellow: 'warn', red: 'bad', connected: 'ok', disconnected: 'bad' };
const num = (v: unknown) => (typeof v === 'number' ? v : Number(v ?? 0));

/** Hardware read by a controller: power supplies, sensors, graphics cards, and for VMware the hosts, datastores and virtual machines. */
export function Hardware({ d }: { d: Rec }) {
  const h = (d.hardware ?? {}) as Rec;
  const psu = (h.power_supplies ?? []) as Rec[], sensors = (h.sensors ?? []) as Rec[], gpus = (h.gpus ?? []) as Rec[], hosts = (h.hosts ?? []) as Rec[], ds = (h.datastores ?? []) as Rec[], vms = (h.vms ?? []) as Rec[];
  const [all, setAll] = useState(false);
  if (![psu, sensors, gpus, hosts, ds, vms].some((x) => x.length)) return <Empty>No hardware details yet. Choose a controller and "VMware ESXi or vCenter" under Overview, then press Read now.</Empty>;
  const bad = sensors.filter((s) => s.state !== 'OK');
  const st = (h.host_state ?? {}) as Rec;
  return <>
    {h.api && <p className="muted">Read from {String(h.api)}{st.overall ? <> · overall health <Badge tone={STATE_TONE[String(st.overall)]}>{String(st.overall)}</Badge></> : null}</p>}
    {psu.length > 0 && <Card title="Power supplies"><table><thead><tr><th>Power supply</th><th>State</th><th>Reading</th></tr></thead><tbody>
      {psu.map((p, i) => <tr key={i}><td>{p.name}</td><td><Badge tone={STATE_TONE[p.state]}>{p.state}</Badge></td><td>{p.reading}</td></tr>)}</tbody></table></Card>}
    {gpus.length > 0 && <Card title="Graphics cards"><table><thead><tr><th>Card</th><th>Vendor</th><th>Slot</th></tr></thead><tbody>
      {gpus.map((g, i) => <tr key={i}><td>{g.name}</td><td>{g.vendor}</td><td className="mono">{g.slot}</td></tr>)}</tbody></table></Card>}
    {sensors.length > 0 && <Card title={`Hardware sensors (${bad.length ? `${bad.length} not OK` : 'all OK'})`}>
      <table><thead><tr><th>Sensor</th><th>Type</th><th>State</th><th className="num">Reading</th></tr></thead><tbody>
        {(all ? sensors : sensors.slice(0, 12)).map((s, i) => <tr key={i}><td>{s.name}</td><td>{s.type}</td><td><Badge tone={STATE_TONE[s.state]}>{s.state}</Badge></td><td className="num">{s.reading}</td></tr>)}</tbody></table>
      {sensors.length > 12 && <button onClick={() => setAll(!all)}>{all ? 'Show fewer' : `Show all ${sensors.length}`}</button>}</Card>}
    {hosts.length > 0 && <Card title={`Hosts (${hosts.length})`}><table><thead><tr><th>Host</th><th>Hardware</th><th>Serial</th><th>Processor</th><th className="num">Memory</th><th>ESXi</th><th>State</th></tr></thead><tbody>
      {hosts.map((x, i) => <tr key={i}><td>{x.name}</td><td>{[x.vendor, x.model].filter(Boolean).join(' ')}</td><td className="mono">{x.serial}</td><td>{x.cpu}<div className="muted">{x.sockets} sockets, {x.cores} cores</div></td>
        <td className="num">{fmtBytes(num(x.ram_bytes))}</td><td>{x.esxi}</td><td><Badge tone={STATE_TONE[String(x.health)] ?? STATE_TONE[String(x.state)]}>{x.state}</Badge></td></tr>)}</tbody></table></Card>}
    {ds.length > 0 && <Card title={`Datastores (${ds.length})`}><table><thead><tr><th>Datastore</th><th>Type</th><th className="num">Size</th><th className="num">Free</th><th className="num">Used</th></tr></thead><tbody>
      {ds.map((x, i) => <tr key={i}><td>{x.name}{x.accessible === false && <> <Badge tone="bad">not accessible</Badge></>}</td><td>{x.type}</td><td className="num">{fmtBytes(num(x.capacity_bytes))}</td><td className="num">{fmtBytes(num(x.free_bytes))}</td>
        <td className="num">{num(x.capacity_bytes) ? Math.round((1 - num(x.free_bytes) / num(x.capacity_bytes)) * 100) + '%' : ''}</td></tr>)}</tbody></table></Card>}
    {vms.length > 0 && <Card title={`Virtual machines (${vms.length}, ${vms.filter((v) => v.power === 'On').length} running)`}><table><thead><tr><th>Name</th><th>Power</th><th>Guest OS</th><th>IP</th><th className="num">CPUs</th><th className="num">Memory</th><th className="num">Disk used</th>{hosts.length > 0 && <th>Host</th>}</tr></thead><tbody>
      {vms.map((v, i) => <tr key={i}><td>{v.name}{v.guest_name && v.guest_name !== v.name ? <div className="muted">{v.guest_name}</div> : null}</td><td><Badge tone={v.power === 'On' ? 'ok' : undefined}>{v.power}</Badge></td><td>{v.guest_os}</td>
        <td className="mono">{v.ip}</td><td className="num">{v.cpus}</td><td className="num">{fmtBytes(num(v.memory_bytes))}</td><td className="num">{fmtBytes(num(v.disk_bytes))}</td>{hosts.length > 0 && <td>{v.host}</td>}</tr>)}</tbody></table></Card>}
  </>;
}
