import { saveFile, useLoad, type Rec } from './http';
import { Badge, Card, Empty, Loading, fmtDay } from './ui';

const MODULE: Record<string, string> = { servicedesk: 'Service Desk', assets: 'Asset management', monitoring: 'Monitoring', adsuite: 'AD Manager', admanager: 'AD Manager', vault: 'Vault', backup: 'Backup' };
const LIMIT: Record<string, string> = { agents: 'agents', max_assets: 'assets', max_devices: 'devices', max_users: 'users', max_domains: 'domains' };
const money = (v: number, c: string) => (c === 'INR' ? '₹' + v.toLocaleString('en-IN', { maximumFractionDigits: 2 }) : '$' + v.toLocaleString('en-US', { maximumFractionDigits: 2 }));

/** What a module's line of a plan says: its size. */
function size(v: unknown): string {
  if (v && typeof v === 'object') return Object.entries(v as Record<string, unknown>).filter(([, n]) => typeof n === 'number').map(([k, n]) => `${n} ${LIMIT[k] ?? k}`).join(', ');
  return typeof v === 'number' ? String(v) : '';
}

/** Administration › Subscription: the plan bought, when it renews, its invoices, and where to renew or add modules. */
export default function Subscription() {
  const lic = useLoad<Rec>('/api/platform/license');
  const sub = useLoad<Rec>('/api/platform/subscription');
  const l = lic.data ?? {}; const s = sub.data;
  const subs = (s?.subscriptions ?? []) as Rec[]; const invoices = (s?.invoices ?? []) as Rec[];
  const now = subs.find((x) => x.state === 'active' || x.state === 'trial') ?? subs[0];
  const store = String(s?.storeUrl ?? 'https://www.deskask.in/pricing');
  const link = (what: string) => String((what === 'renew' ? s?.renewUrl : s?.changeUrl) || store);
  const days = now ? Math.ceil((new Date(String(now.endsAt)).getTime() - Date.now()) / 86_400_000) : null;
  return <>
    <h1>Subscription</h1>
    <p className="muted">What this DeskAsk is licensed for, when it renews, and its invoices.</p>
    <Loading loading={(lic.loading && !lic.data) || (sub.loading && !s)} error={lic.error || sub.error} />
    {s && <>
      <Card title="Your plan">
        {now ? <>
          <p><Badge tone={now.state === 'active' ? 'ok' : now.state === 'trial' ? 'warn' : 'bad'}>{now.state === 'trial' ? 'Free trial' : now.state === 'active' ? 'Active' : 'Ended'}</Badge>{' '}
            <strong>{now.kind === 'saas' ? 'In our cloud' : 'On your server'}</strong>, billed {now.period === 'month' ? 'monthly' : 'yearly'}</p>
          <p className="muted">{days != null && days >= 0 ? `Renews or ends on ${fmtDay(String(now.endsAt))} (${days} ${days === 1 ? 'day' : 'days'} from now).` : `Ended on ${fmtDay(String(now.endsAt))}.`}</p>
          <table><thead><tr><th>Module</th><th>Size</th></tr></thead><tbody>
            {Object.entries((now.lines ?? {}) as Record<string, unknown>).map(([k, v]) => <tr key={k}><td>{MODULE[k] ?? k}</td><td>{size(v) || <span className="muted">included</span>}</td></tr>)}</tbody></table>
        </> : <p className="muted">{l.plan ? <>Licence <strong>{String(l.plan)}</strong>{l.expires ? `, valid until ${fmtDay(String(l.expires))}` : ''}.</> : 'No plan was bought for this DeskAsk through the store.'} The details are on <a href="#/licence">Administration › Licence</a>.</p>}
        <div className="actions" style={{ justifyContent: 'flex-start' }}>
          <a className="button primary" href={link('renew')} target="_blank" rel="noopener">Renew</a>
          <a className="button" href={link('change')} target="_blank" rel="noopener">Add or change modules</a>
        </div>
        <p className="muted small">The store opens in a new tab. After payment the licence updates here by itself; nothing to install.</p>
      </Card>
      <Card title="Invoices">
        {!s.store ? <p className="muted">Invoices are emailed with each payment. If one is missing, write to servicedesk@deskask.in.</p>
          : invoices.length === 0 ? <Empty>No invoices yet.</Empty>
          : <table><thead><tr><th>Invoice</th><th>Date</th><th className="num">Total</th><th /></tr></thead><tbody>
            {invoices.map((i) => <tr key={i.id}><td className="mono">{i.number}</td><td>{fmtDay(String(i.issuedAt))}</td><td className="num">{money(Number(i.total), String(i.currency))}</td>
              <td><button type="button" onClick={() => void saveFile(`/api/platform/subscription/invoices/${i.id}/pdf`, String(i.number).replace(/\//g, '-') + '.pdf')}>Download PDF</button></td></tr>)}</tbody></table>}
      </Card>
    </>}
  </>;
}
