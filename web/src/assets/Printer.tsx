import { useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Loading, Meter, ago } from '../ui';

const STATUS: Record<string, [string, 'ok' | 'warn' | 'bad' | undefined]> = {
  idle: ['Ready', 'ok'], printing: ['Printing', 'ok'], warmup: ['Warming up', 'ok'], other: ['Other', undefined], unknown: ['Unknown', undefined],
};
const FLAGS: Record<string, string> = {
  jammed: 'Paper jam', door_open: 'Door or cover open', no_paper: 'Out of paper', low_paper: 'Paper low', no_toner: 'Out of toner or ink', low_toner: 'Toner or ink low',
  offline: 'Offline', service_requested: 'Service requested', output_full: 'Output tray full', output_near_full: 'Output tray nearly full',
  input_tray_missing: 'Paper tray missing', output_tray_missing: 'Output tray missing', marker_supply_missing: 'Cartridge missing', input_tray_empty: 'A paper tray is empty',
  overdue_maintenance: 'Maintenance overdue',
};
const FATAL = ['jammed', 'door_open', 'no_paper', 'no_toner', 'offline', 'service_requested', 'output_full', 'marker_supply_missing', 'input_tray_missing'];
const INK: Record<string, string> = { black: '#1f2328', cyan: '#00a6d6', magenta: '#d6247f', yellow: '#e6b800', gray: '#8a8f98', red: '#d33', green: '#2a9d4a', blue: '#2b5fd9', colour: '#7a5af8' };
const KIND: Record<string, string> = { toner: 'Toner', ink: 'Ink', drum: 'Drum', fuser: 'Fuser', waste: 'Waste container', transfer: 'Transfer unit', maintenance: 'Maintenance', other: 'Supply' };

const num = (n: number) => n.toLocaleString('en-IN');

function level(s: Rec): string {
  if (s.pct >= 0) return `${Math.round(s.pct)}%${s.waste ? ' full' : ''}`;
  return s.state === 'ok' ? 'Some left (the printer does not give a level)' : 'Not reported';
}
const tone = (state: string): 'ok' | 'warn' | 'bad' | undefined => (state === 'ok' ? 'ok' : state === 'low' ? 'warn' : state === 'empty' || state === 'full' ? 'bad' : undefined);
const words = (state: string, waste: boolean) => (state === 'ok' ? 'OK' : state === 'low' ? 'Low' : state === 'empty' ? 'Empty' : state === 'full' ? 'Nearly full' : waste ? 'Not reported' : 'Unknown');

function Pages({ p, daily }: { p: Rec; daily: Rec[] }) {
  const show = (c?: Rec | null) => (c ? `${num(c.pages)}${c.partial ? ' or more' : ''}` : '–');
  const max = Math.max(1, ...daily.map((d) => d.pages as number));
  return <Card title="Pages printed">
    <Dl rows={[['Printer total', p.total != null ? num(p.total) : 'Not reported'], ['This month', show(p.thisMonth)], ['Last month', show(p.lastMonth)], ['Last 7 days', show(p.last7)], ['Last 30 days', show(p.last30)]]} />
    {daily.length > 1 ? <div className="printer-days" role="img" aria-label={`Pages per day, last ${daily.length} days with a reading`} style={{ display: 'flex', alignItems: 'flex-end', gap: 2, height: 64, marginTop: 8 }}>
      {daily.map((d) => <i key={d.day} title={`${d.day}: ${num(d.pages)} pages`} style={{ flex: 1, minWidth: 2, height: Math.max(2, (d.pages / max) * 64), background: 'var(--accent, #2b5fd9)', borderRadius: 1 }} />)}
    </div> : <p className="muted small">The daily chart fills in after a few days of readings. The printer's own counter is read every 15 minutes and kept once a day.</p>}
    <p className="muted small">Monthly figures come from the printer's lifetime counter. A month marked "or more" started before the first reading.</p>
  </Card>;
}

/** A printer as a controller sees it over SNMP: status, toner and ink, paper, pages, and the two things a technician reaches for (clear the queue, restart). */
export function PrinterPanel({ id, canAct }: { id: string; canAct: boolean }) {
  const { data, error, loading, reload } = useLoad<Rec>(`/api/assets/${id}/printer`, [id]);
  const [note, setNote] = useState('');
  useInterval(() => reload(), 60000);
  if (!data) return <Loading loading={loading} error={error} />;
  const r = data.reading as Rec | null; const flags = ((r?.flags ?? []) as string[]).filter((f) => FLAGS[f]);
  const st = STATUS[r?.status as string] ?? STATUS.unknown;
  const act = (what: 'clear-queue' | 'restart') => async () => { setNote(''); const out = await api(`/api/assets/${id}/printer/${what}`, { method: 'POST', body: {} }); setNote(String(out.message ?? 'Done.')); reload(); };
  return <div>
    {!data.controller && <Card><p className="err">Nothing is read from this printer yet. Open Overview, choose a controller and set Read as "Printer or other device (SNMP)". The controller needs an SNMP community saved under Credentials for this customer.</p></Card>}
    {data.controller && data.error && <p className="err" role="alert">The last read failed: {data.error}</p>}
    {!r && data.controller && !data.error && <Empty>Waiting for the first reading. The controller reads a printer every 15 minutes, or press Read now on Overview.</Empty>}
    {r && <>
      <Card title="Status">
        <p><Badge tone={st[1]}>{st[0]}</Badge> {flags.map((f) => <span key={f}> <Badge tone={FATAL.includes(f) ? 'bad' : 'warn'}>{FLAGS[f]}</Badge></span>)}</p>
        {r.message && <p className="muted">On the printer's display: {r.message}</p>}
        {((r.alerts ?? []) as Rec[]).length > 0 && <ul>{(r.alerts as Rec[]).map((a, i) => <li key={i}><Badge tone={a.severity === 'critical' ? 'bad' : a.severity === 'warning' ? 'warn' : undefined}>{a.severity}</Badge> {a.text}</li>)}</ul>}
        <Dl rows={[['Printer name', r.name], ['Model', r.model], ['Serial number', r.serial], ['Last read', data.at ? ago(data.at) : '']]} />
        {canAct && <div className="tools">
          <Act label="Clear the printer's queue" confirm="Delete every job waiting in this printer's own queue? Jobs on a print server are not touched." run={act('clear-queue')} />
          <Act label="Restart the printer" danger confirm="Restart this printer now? A job being printed is lost." run={act('restart')} />
        </div>}
        {note && <p role="status">{note}</p>}
        {canAct && <p className="muted small">Both run on the site controller inside the customer's network. Clearing uses the printer's IPP service (port 631); restarting needs an SNMP community that may write. Jobs queued on a Windows print server are cleared on the server (Assets › the server › Commands › Clear print queue and restart spooler).</p>}
      </Card>
      <Card title="Toner, ink and parts">
        {((r.supplies ?? []) as Rec[]).length === 0 ? <Empty>The printer does not report its supplies.</Empty> :
          <div className="table-wrap"><table><thead><tr><th>Supply</th><th>Level</th><th /></tr></thead><tbody>{(r.supplies as Rec[]).map((s, i) => <tr key={i}>
            <td>{s.color && INK[s.color] && <span aria-hidden="true" style={{ display: 'inline-block', width: 10, height: 10, borderRadius: 5, background: INK[s.color], marginRight: 6 }} />}{s.name} <span className="muted small">{KIND[s.kind] ?? ''}</span></td>
            <td style={{ minWidth: 140 }}>{s.pct >= 0 ? <><Meter pct={s.pct} /> {level(s)}</> : level(s)}</td>
            <td><Badge tone={tone(s.state)}>{words(s.state, !!s.waste)}</Badge></td></tr>)}</tbody></table></div>}
      </Card>
      {((r.trays ?? []) as Rec[]).length > 0 && <Card title="Paper">
        <div className="table-wrap"><table><thead><tr><th>Tray</th><th>Paper</th><th>Level</th></tr></thead><tbody>{(r.trays as Rec[]).map((t, i) => <tr key={i}>
          <td>{t.name}</td><td>{t.media || <span className="muted">Not reported</span>}</td>
          <td>{t.state === 'empty' ? <Badge tone="bad">Empty</Badge> : t.pct >= 0 ? <><Meter pct={t.pct} /> {Math.round(t.pct)}%</> : t.state === 'ok' ? <Badge tone="ok">Has paper</Badge> : <span className="muted">Not reported</span>}</td></tr>)}</tbody></table></div>
      </Card>}
    </>}
    {data.pages && (data.pages.total != null || r) && <Pages p={data.pages as Rec} daily={(data.daily ?? []) as Rec[]} />}
  </div>;
}
