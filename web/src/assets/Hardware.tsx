import { useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, ago, fmtBytes, fmtDate } from '../ui';

/** How a controller reads a device: only whether it answers, or its details too. */
export const MODES: [string, string][] = [['', 'Not watched'], ['ping', 'Ping only'], ['device', 'Ping and ports (no login)'], ['windows', 'Windows or Hyper-V host (WinRM)'],
  ['ssh', 'Linux, KVM or Proxmox host (SSH)'], ['snmp', 'Printer, switch or firewall (SNMP)'], ['vmware', 'VMware ESXi or vCenter'], ['nutanix', 'Nutanix (Prism Element)'], ['proxmox', 'Proxmox VE (API)'],
  ['xen', 'XCP-ng or Citrix Hypervisor'], ['bmc', 'Server management (iLO, iDRAC, XClarity)']];
const READING = ['windows', 'ssh', 'snmp', 'vmware', 'nutanix', 'proxmox', 'xen', 'bmc'];
/** Modes that read a hypervisor or a server in depth, so the Hardware tab is shown. */
export const DEEP = ['vmware', 'nutanix', 'proxmox', 'xen', 'bmc'];
const LOGIN_HINT: Record<string, string> = {
  vmware: 'Uses the VMware logins saved under Credentials for this customer (hardware category Hypervisors). A read-only account is enough.',
  nutanix: 'Use the Prism Element (cluster) address, port 9440. Uses the Nutanix logins saved under Credentials for this customer; a viewer account is enough.',
  proxmox: 'Port 8006. Uses the Proxmox logins saved under Credentials: an API token (user name like audit@pve!deskask, the token secret as password, PVEAuditor role) or a user and password.',
  xen: "Use the pool master's address. Uses the XCP-ng logins saved under Credentials for this customer (a read-only role is enough).",
  bmc: 'For an iLO, iDRAC or XClarity address added as its own asset. Uses the server management logins saved under Credentials for this customer (hardware category Physical servers).',
  snmp: 'Uses the SNMP communities saved under Credentials for this customer.', windows: 'Uses the Windows logins saved under Credentials for this customer. A Hyper-V host also lists its virtual machines.', ssh: 'Uses the Linux logins saved under Credentials for this customer. A KVM (libvirt) or Proxmox host also lists its virtual machines.',
};

/** Which controller watches this device and how; "Read now" asks it to read the details at once. */
export function Watch({ id, d, onChange }: { id: string; d: Rec; onChange: () => void }) {
  const w = (d.watch ?? {}) as Rec;
  const [bmc, setBmc] = useState<string>(String(w.bmc ?? ''));
  const cols = useLoad<Rec>('/api/assets/collectors');
  const [mode, setMode] = useState<string>(String(w.mode ?? '')); const [col, setCol] = useState<string>(String(w.collectorId ?? ''));
  const [err, setErr] = useState('');
  const items = ((cols.data?.items ?? []) as Rec[]);
  const reading = !!w.readRequested;
  useInterval(() => onChange(), reading ? 3000 : null);
  const changed = mode !== String(w.mode ?? '') || col !== String(w.collectorId ?? '') || bmc.trim() !== String(w.bmc ?? '');
  const hasAgent = !!d.agent?.id;
  return <Card title="Read by a controller">
    <div className="row">
      <Field label="Controller"><select value={col} onChange={(e) => setCol(e.target.value)}><option value="">None</option>{items.map((c) => <option key={c.id} value={c.id}>{c.name}{c.company ? ` (${c.company})` : ''}{c.online ? '' : ' - offline'}</option>)}</select></Field>
      <Field label="Read as"><select value={mode} onChange={(e) => setMode(e.target.value)}>{MODES.map(([k, l]) => <option key={k} value={k}>{l}{hasAgent && k === '' ? ' (the agent reports)' : ''}</option>)}</select></Field>
    </div>
    {LOGIN_HINT[mode] && <p className="muted">{LOGIN_HINT[mode]}</p>}
    {mode !== 'bmc' && <Field label="Management address (iLO, iDRAC, XClarity)"><input value={bmc} onChange={(e) => setBmc(e.target.value)} placeholder="192.168.31.50" /></Field>}
    {mode !== 'bmc' && <p className="muted">Optional, for a physical server. The controller also reads its management controller, so power supplies, memory modules, physical drives and fans are added to this asset. Uses the server management logins saved under Credentials.</p>}
    <p>
      <Act label="Save" primary={changed} run={async () => {
        setErr(''); try { await api(`/api/assets/${id}/monitor`, { body: { mode, collectorId: mode || bmc.trim() ? col : '', bmc: mode === 'bmc' ? '' : bmc.trim() } }); onChange(); } catch (e) { setErr((e as Error).message); }
      }} />{' '}
      {!changed && (READING.includes(String(w.mode)) || w.bmc) && w.collectorId && <Act label="Read now" run={async () => { setErr(''); try { await api(`/api/assets/${id}/read-now`, { body: {} }); onChange(); } catch (e) { setErr((e as Error).message); } }} />}{' '}
      {reading && <Badge tone="warn">Reading…</Badge>}
    </p>
    <Dl rows={[['Answers', w.up == null ? '' : w.up ? 'Yes' : 'No'], ['Checked', w.checkedAt ? ago(String(w.checkedAt)) : ''], ['Details read', d.agent?.inventoryAt && d.agent?.source === 'collector' ? fmtDate(String(d.agent.inventoryAt)) : ''], ['Management controller read', w.bmcReadAt ? fmtDate(String(w.bmcReadAt)) : '']]} />
    <p className="muted">A failed read is written to the Logs tab with the reason (for example a refused login).</p>
    <Err text={err} />
  </Card>;
}

const STATE_TONE: Record<string, 'ok' | 'warn' | 'bad' | undefined> = { OK: 'ok', Warning: 'warn', Critical: 'bad', green: 'ok', yellow: 'warn', red: 'bad', connected: 'ok', disconnected: 'bad' };
const num = (v: unknown) => (typeof v === 'number' ? v : Number(v ?? 0));

/** Hardware read by a controller: power supplies, sensors, graphics cards, and for VMware the hosts, datastores and virtual machines. */
export function Hardware({ d }: { d: Rec }) {
  const h = (d.hardware ?? {}) as Rec;
  const procs = (h.processors ?? []) as Rec[], dimms = (h.memory_modules ?? []) as Rec[], drives = (h.drives ?? []) as Rec[], ctrls = (h.storage_controllers ?? []) as Rec[];
  const psu = (h.power_supplies ?? []) as Rec[], sensors = (h.sensors ?? []) as Rec[], gpus = (h.gpus ?? []) as Rec[], hosts = (h.hosts ?? []) as Rec[], ds = (h.datastores ?? []) as Rec[], vms = (h.vms ?? []) as Rec[];
  const [all, setAll] = useState(false);
  if (![psu, sensors, gpus, hosts, ds, vms, procs, dimms, drives].some((x) => x.length)) return <Empty>No hardware details yet. Under Overview, choose a controller and how to read this device (a hypervisor, or server management), or enter its management address, then press Read now.</Empty>;
  const bmc = (h.bmc ?? null) as Rec | null;
  const bad = sensors.filter((s) => s.state !== 'OK');
  const st = (h.host_state ?? {}) as Rec;
  return <>
    {h.api && <p className="muted">Read from {String(h.api)}{st.overall ? <> · overall health <Badge tone={STATE_TONE[String(st.overall)]}>{String(st.overall)}</Badge></> : null}</p>}
    {(bmc || h.bmc_api) && <p className="muted">Management controller: {String(h.bmc_api ?? h.api ?? '')}{bmc?.state && bmc.state !== 'OK' ? <> <Badge tone={STATE_TONE[String(bmc.state)]}>{String(bmc.state)}</Badge></> : null}</p>}
    {procs.length > 0 && <Card title={`Processors (${procs.length})`}><table><thead><tr><th>Socket</th><th>Model</th><th className="num">Cores</th><th className="num">Threads</th><th className="num">Max speed</th><th>State</th></tr></thead><tbody>
      {procs.map((p, i) => <tr key={i}><td>{p.socket}</td><td>{p.model}</td><td className="num">{p.cores || ''}</td><td className="num">{p.threads || ''}</td><td className="num">{p.max_mhz ? `${p.max_mhz} MHz` : ''}</td><td><Badge tone={STATE_TONE[p.state]}>{p.state}</Badge></td></tr>)}</tbody></table></Card>}
    {dimms.length > 0 && <Card title={`Memory modules (${dimms.length}, ${fmtBytes(dimms.reduce((t, m) => t + num(m.size_bytes), 0))})`}><table><thead><tr><th>Slot</th><th className="num">Size</th><th>Type</th><th className="num">Speed</th><th>Maker</th><th>Part</th><th>State</th></tr></thead><tbody>
      {dimms.map((m, i) => <tr key={i}><td>{m.slot}</td><td className="num">{fmtBytes(num(m.size_bytes))}</td><td>{m.type}</td><td className="num">{m.speed_mhz ? `${m.speed_mhz} MHz` : ''}</td><td>{m.manufacturer}</td><td className="mono">{m.part}</td><td><Badge tone={STATE_TONE[m.state]}>{m.state}</Badge></td></tr>)}</tbody></table></Card>}
    {(drives.length > 0 || ctrls.length > 0) && <Card title={`Physical drives (${drives.length})`}>
      {ctrls.length > 0 && <p className="muted">Controllers: {ctrls.map((c) => `${c.name}${c.firmware ? ` (firmware ${c.firmware})` : ''}`).join(', ')}</p>}
      <table><thead><tr><th>Drive</th><th>Model</th><th>Type</th><th className="num">Size</th><th>Serial</th><th>State</th></tr></thead><tbody>
      {drives.map((x, i) => <tr key={i}><td>{x.name}<div className="muted">{x.controller}</div></td><td>{[x.manufacturer, x.model].filter(Boolean).join(' ')}</td><td>{[x.media, x.protocol].filter(Boolean).join(' ')}</td><td className="num">{fmtBytes(num(x.size_bytes))}</td><td className="mono">{x.serial}</td><td><Badge tone={STATE_TONE[x.state]}>{x.state}</Badge></td></tr>)}</tbody></table></Card>}
    {psu.length > 0 && <Card title="Power supplies"><table><thead><tr><th>Power supply</th><th>State</th><th>Reading</th></tr></thead><tbody>
      {psu.map((p, i) => <tr key={i}><td>{p.name}{p.model ? <div className="muted">{p.model}{p.serial ? ` · ${p.serial}` : ''}</div> : null}</td><td><Badge tone={STATE_TONE[p.state]}>{p.state}</Badge></td><td>{p.reading}</td></tr>)}</tbody></table></Card>}
    {gpus.length > 0 && <Card title="Graphics cards"><table><thead><tr><th>Card</th><th>Vendor</th><th>Slot</th></tr></thead><tbody>
      {gpus.map((g, i) => <tr key={i}><td>{g.name}</td><td>{g.vendor}</td><td className="mono">{g.slot}</td></tr>)}</tbody></table></Card>}
    {sensors.length > 0 && <Card title={`Hardware sensors (${bad.length ? `${bad.length} not OK` : 'all OK'})`}>
      <table><thead><tr><th>Sensor</th><th>Type</th><th>State</th><th className="num">Reading</th></tr></thead><tbody>
        {(all ? sensors : sensors.slice(0, 12)).map((s, i) => <tr key={i}><td>{s.name}</td><td>{s.type}</td><td><Badge tone={STATE_TONE[s.state]}>{s.state}</Badge></td><td className="num">{s.reading}</td></tr>)}</tbody></table>
      {sensors.length > 12 && <button onClick={() => setAll(!all)}>{all ? 'Show fewer' : `Show all ${sensors.length}`}</button>}</Card>}
    {hosts.length > 0 && <Card title={`Hosts (${hosts.length})`}><table><thead><tr><th>Host</th><th>Hardware</th><th>Serial</th><th>Processor</th><th className="num">Memory</th><th>Software</th><th>State</th></tr></thead><tbody>
      {hosts.map((x, i) => <tr key={i}><td>{x.name}{x.address || x.ipmi ? <div className="muted">{[x.address, x.ipmi ? `IPMI ${x.ipmi}` : ''].filter(Boolean).join(' · ')}</div> : null}</td><td>{[x.vendor, x.model].filter(Boolean).join(' ')}</td><td className="mono">{x.serial}</td><td>{x.cpu}<div className="muted">{x.sockets} sockets, {x.cores} cores</div></td>
        <td className="num">{fmtBytes(num(x.ram_bytes))}</td><td>{x.esxi}</td><td><Badge tone={STATE_TONE[String(x.health)] ?? STATE_TONE[String(x.state)]}>{x.state}</Badge></td></tr>)}</tbody></table></Card>}
    {ds.length > 0 && <Card title={`Datastores (${ds.length})`}><table><thead><tr><th>Datastore</th><th>Type</th><th className="num">Size</th><th className="num">Free</th><th className="num">Used</th></tr></thead><tbody>
      {ds.map((x, i) => <tr key={i}><td>{x.name}{x.accessible === false && <> <Badge tone="bad">not accessible</Badge></>}</td><td>{x.type}</td><td className="num">{fmtBytes(num(x.capacity_bytes))}</td><td className="num">{fmtBytes(num(x.free_bytes))}</td>
        <td className="num">{num(x.capacity_bytes) ? Math.round((1 - num(x.free_bytes) / num(x.capacity_bytes)) * 100) + '%' : ''}</td></tr>)}</tbody></table></Card>}
    {vms.length > 0 && <Card title={`Virtual machines (${vms.length}, ${vms.filter((v) => v.power === 'On').length} running)`}><table><thead><tr><th>Name</th><th>Power</th><th>Guest OS</th><th>IP</th><th className="num">CPUs</th><th className="num">Memory</th><th className="num">Disk used</th>{hosts.length > 0 && <th>Host</th>}</tr></thead><tbody>
      {vms.map((v, i) => <tr key={i}><td>{v.name}{v.guest_name && v.guest_name !== v.name ? <div className="muted">{v.guest_name}</div> : null}</td><td><Badge tone={v.power === 'On' ? 'ok' : undefined}>{v.power}</Badge>{v.kind === 'Container' ? <> <Badge>container</Badge></> : null}</td><td>{v.guest_os}</td>
        <td className="mono">{v.ip}</td><td className="num">{v.cpus}</td><td className="num">{fmtBytes(num(v.memory_bytes))}</td><td className="num">{fmtBytes(num(v.disk_bytes))}</td>{hosts.length > 0 && <td>{v.host}</td>}</tr>)}</tbody></table></Card>}
  </>;
}
