import { useEffect, useMemo, useState } from 'react';
import { api, go, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Copy, Dl, Empty, Err, Field, Loading, Modal, copyText, fmtDate, fmtDay } from '../ui';

/** Module names and the one size limit each is sold by. */
export const MODS: { key: string; name: string; limit: string; unit: string }[] = [
  { key: 'servicedesk', name: 'Service Desk', limit: 'agents', unit: 'agents' },
  { key: 'assets', name: 'Asset management', limit: 'max_assets', unit: 'assets' },
  { key: 'monitoring', name: 'Monitoring', limit: 'max_devices', unit: 'devices' },
  { key: 'adsuite', name: 'AD Manager', limit: 'max_domains', unit: 'domains' },
  { key: 'vault', name: 'Vault', limit: 'max_users', unit: 'users' },
  { key: 'backup', name: 'Backup', limit: 'max_devices', unit: 'devices' },
];
type Lines = Record<string, Record<string, number>>;
const STATE: Record<string, ['ok' | 'warn' | 'bad' | undefined, string]> = { trial: ['warn', 'Trial'], active: ['ok', 'Active'], cancelled: ['warn', 'Cancelled (runs to its end)'], ended: ['bad', 'Ended'] };
const day = (d: number) => new Date(Date.now() + d * 86400000).toISOString().slice(0, 10);
const linesText = (l: Lines) => MODS.filter((m) => l?.[m.key]).map((m) => { const v = l[m.key]?.[m.limit]; return m.name + (v != null ? ' ' + v : ' (no limit)'); }).join(', ');

/** The vendor console: subscriptions, licences, licence keys, customers licensed before the store, the price list and the signing key. */
export default function VendorApp({ path }: { path: string[] }) {
  const status = useLoad<Rec>('/api/commerce/status');
  const tab = path[0] ?? '';
  const tabs: [string, string][] = [['', 'Subscriptions'], ['orders', 'Orders'], ['invoices', 'Invoices'], ['new', 'New subscription'], ['unlinked', 'Not linked yet'], ['prices', 'Price list'], ['business', 'Business details'], ['payments', 'Payments'], ['key', 'Signing key']];
  const ready = !!status.data?.signing?.ready;
  return <>
    <h1>Vendor console</h1>
    <nav className="sub" aria-label="Vendor console">{tabs.map(([k, label]) => <a key={k} href={'#/vendor' + (k ? '/' + k : '')} className={tab === k || (k === '' && tab === 'sub') ? 'on' : ''}>{label}</a>)}</nav>
    {status.data && !ready && tab !== 'key' && <p role="alert" className="err">Licences cannot be issued yet: import the licence signing key first (<a href="#/vendor/key">Signing key</a>).</p>}
    {tab === 'sub' && path[1] ? <SubDetail id={path[1]} />
      : tab === 'new' ? <NewSub />
      : tab === 'unlinked' ? <Unlinked />
      : tab === 'prices' ? <Prices />
      : tab === 'orders' ? <Orders />
      : tab === 'invoices' ? <InvoiceList />
      : tab === 'business' ? <BusinessDetails />
      : tab === 'payments' ? <Payments />
      : tab === 'key' ? <SigningKey status={status.data} reload={status.reload} />
      : <SubList />}
  </>;
}

function SubList() {
  const [q, setQ] = useState('');
  const list = useLoad<Rec[]>('/api/commerce/subscriptions?q=' + encodeURIComponent(q), [q]);
  return <Card>
    <div className="form"><Field label="Find"><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Company, email or customer id" /></Field></div>
    <Loading loading={list.loading && !list.data} error={list.error} />
    {list.data && list.data.length === 0 ? <Empty>No subscriptions yet. Create one, or link customers who already have a licence (Not linked yet).</Empty> : null}
    {list.data && list.data.length > 0 && <table><thead><tr><th>Customer</th><th>Type</th><th>Customer id</th><th>Modules</th><th>Ends</th><th>State</th></tr></thead><tbody>
      {list.data.map((x) => { const s = x.sub as Rec; const [tone, label] = STATE[s.state] ?? [undefined, s.state];
        return <tr key={s.id} onClick={() => go('vendor/sub/' + s.id)} style={{ cursor: 'pointer' }}>
          <td><b>{s.account}</b><div className="small muted">{x.email}</div></td><td>{s.kind === 'saas' ? 'SaaS' : 'On-prem'}</td><td>{s.tenantId || <span className="muted">set at activation</span>}</td>
          <td className="small">{linesText(s.lines)}</td><td>{fmtDay(s.endsAt)}</td><td><Badge tone={tone}>{label}</Badge></td></tr>; })}
    </tbody></table>}
  </Card>;
}

/** Choose modules and sizes from the price list, or a custom number. */
function ModulePicker({ lines, onChange }: { lines: Lines; onChange: (l: Lines) => void }) {
  const plans = useLoad<Rec[]>('/api/commerce/plans');
  return <table><thead><tr><th>Module</th><th>Size</th></tr></thead><tbody>
    {MODS.map((m) => {
      const cur = lines[m.key]; const v = cur?.[m.limit];
      const sizes = (plans.data ?? []).filter((p) => p.module === m.key && p.active);
      const sel = !cur ? '' : v == null ? 'unl' : sizes.some((p) => p.limits?.[m.limit] === v) ? 'n:' + v : 'custom';
      const set = (val: string, n?: number) => {
        const next = { ...lines };
        if (val === '') delete next[m.key];
        else if (val === 'unl') next[m.key] = {};
        else next[m.key] = { [m.limit]: n ?? Number(val.slice(2)) };
        onChange(next);
      };
      return <tr key={m.key}><td>{m.name}</td><td>
        <select aria-label={m.name + ' size'} value={sel} onChange={(e) => set(e.target.value === 'custom' ? 'custom' : e.target.value, e.target.value === 'custom' ? (v ?? 10) : undefined)}>
          <option value="">Not included</option>
          {sizes.filter((p) => p.limits?.[m.limit] != null).map((p) => <option key={p.id} value={'n:' + p.limits[m.limit]}>{p.sizeLabel}</option>)}
          <option value="unl">No limit</option>
          <option value="custom">Custom number…</option>
        </select>
        {sel === 'custom' && <input type="number" min={0} style={{ width: 110, marginLeft: 8 }} aria-label={m.name + ' ' + m.unit} value={v ?? 0} onChange={(e) => set('custom', Math.max(0, Number(e.target.value) || 0))} />}
        {sel === 'custom' && <span className="muted small"> {m.unit}</span>}
      </td></tr>;
    })}
  </tbody></table>;
}

function NewSub() {
  const accounts = useLoad<Rec[]>('/api/commerce/accounts');
  const [kind, setKind] = useState('saas'); const [accountId, setAccountId] = useState('');
  const [acc, setAcc] = useState({ name: '', email: '', gstin: '', country: 'IN', address: '' });
  const [tenantId, setTenantId] = useState(''); const [plan, setPlan] = useState('business'); const [period, setPeriod] = useState('year');
  const [lines, setLines] = useState<Lines>({}); const [startsAt, setStarts] = useState(day(0)); const [endsAt, setEnds] = useState(day(365));
  const [grace, setGrace] = useState(''); const [notes, setNotes] = useState(''); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true); setErr('');
    try {
      const r = await api('/api/commerce/subscriptions', { body: { kind, accountId: accountId || undefined, account: accountId ? undefined : acc, tenantId, plan, period, lines, startsAt, endsAt, graceDays: grace === '' ? undefined : Number(grace), notes } });
      go('vendor/sub/' + r.id);
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return <Card title="New subscription">
    <div className="form">
      <Field label="Type"><select value={kind} onChange={(e) => setKind(e.target.value)}><option value="saas">SaaS (on this platform)</option><option value="onprem">On-prem (licence key)</option></select></Field>
      <Field label="Customer"><select value={accountId} onChange={(e) => setAccountId(e.target.value)}><option value="">New customer…</option>{(accounts.data ?? []).map((a) => <option key={a.id} value={a.id}>{a.name} ({a.email})</option>)}</select></Field>
      {!accountId && <div className="row">
        <Field label="Company name"><input value={acc.name} onChange={(e) => setAcc({ ...acc, name: e.target.value })} /></Field>
        <Field label="Email"><input type="email" value={acc.email} onChange={(e) => setAcc({ ...acc, email: e.target.value })} /></Field>
        <Field label="GSTIN (optional)"><input value={acc.gstin} onChange={(e) => setAcc({ ...acc, gstin: e.target.value.toUpperCase() })} /></Field>
        <Field label="Country"><input value={acc.country} maxLength={2} style={{ width: 70 }} onChange={(e) => setAcc({ ...acc, country: e.target.value.toUpperCase() })} /></Field>
      </div>}
      <div className="row">
        <Field label={kind === 'saas' ? 'Customer id on this platform' : 'Customer id (optional: taken from the first activation)'}><input value={tenantId} onChange={(e) => setTenantId(e.target.value.toLowerCase())} placeholder="acme" /></Field>
        <Field label="Plan name"><input value={plan} onChange={(e) => setPlan(e.target.value)} /></Field>
        <Field label="Billing"><select value={period} onChange={(e) => setPeriod(e.target.value)}><option value="year">Yearly</option><option value="month">Monthly</option><option value="manual">Manual / special deal</option></select></Field>
      </div>
      <ModulePicker lines={lines} onChange={setLines} />
      <div className="row">
        <Field label="Starts"><input type="date" value={startsAt} onChange={(e) => setStarts(e.target.value)} /></Field>
        <Field label="Ends"><input type="date" value={endsAt} onChange={(e) => setEnds(e.target.value)} /></Field>
        <Field label={'Grace days (default ' + (kind === 'saas' ? 7 : 14) + ')'}><input type="number" min={0} max={90} value={grace} onChange={(e) => setGrace(e.target.value)} /></Field>
      </div>
      <Field label="Notes"><textarea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} /></Field>
      <Err text={err} />
      <p className="muted small">{kind === 'saas' ? 'The customer is licensed as soon as you save.' : 'After saving, create a licence key for the customer, or a licence file for an offline site.'}</p>
      <div className="actions"><button className="primary" disabled={busy || Object.keys(lines).length === 0} onClick={save}>Create subscription</button></div>
    </div>
  </Card>;
}

function SubDetail({ id }: { id: string }) {
  const d = useLoad<Rec>('/api/commerce/subscriptions/' + id, [id]);
  const [editing, setEditing] = useState(false); const [shown, setShown] = useState<{ title: string; text: string; note: string } | null>(null);
  const [fileFor, setFileFor] = useState<{ tenantId: string; installation: string } | null>(null);
  if (!d.data) return <Loading loading={d.loading} error={d.error} />;
  const s = d.data.sub as Rec; const a = (d.data.account ?? {}) as Rec; const [tone, label] = STATE[s.state] ?? [undefined, s.state];
  const keys = (d.data.keys ?? []) as Rec[]; const licences = (d.data.licences ?? []) as Rec[];
  return <>
    <p><a href="#/vendor">‹ All subscriptions</a></p>
    <Card title={(a.name ?? '') + ' · ' + (s.kind === 'saas' ? 'SaaS' : 'On-prem')}>
      <Dl rows={[['State', <Badge key="s" tone={tone}>{label}</Badge>], ['Email', a.email], ['GSTIN', a.gstin], ['Customer id', s.tenantId || 'set at the first activation'], ['Plan', s.plan],
        ['Modules', linesText(s.lines)], ['Period', fmtDay(s.startsAt) + ' to ' + fmtDay(s.endsAt) + ' (+' + s.graceDays + ' grace days)'], ['Billing', s.period], ['Notes', s.notes]]} />
      <div className="actions">
        <button onClick={() => setEditing(true)}>Change</button>
        {s.kind === 'saas'
          ? <Act label="Issue the licence again" run={async () => { await api('/api/commerce/subscriptions/' + id + '/licence', { body: {} }); d.reload(); }} />
          : <button onClick={() => setFileFor({ tenantId: s.tenantId ?? '', installation: '' })}>Licence file (offline site)</button>}
        {s.state !== 'ended' && <Act label="Extend 7 days" run={async () => { await api('/api/commerce/subscriptions/' + id + '/extend', { body: { days: 7 } }); d.reload(); }} />}
        {s.state !== 'ended' && <Act label="Extend 14 days" run={async () => { await api('/api/commerce/subscriptions/' + id + '/extend', { body: { days: 14 } }); d.reload(); }} />}
        {s.kind === 'saas' && (s.state === 'trial' || s.state === 'ended') && s.tenantId && <Act label="Delete customer and data" danger run={async () => {
          const typed = window.prompt('This deletes ' + s.tenantId + ' and its database. It cannot be undone. Type the customer id to confirm:');
          if (typed !== s.tenantId) return;
          await api('/api/commerce/subscriptions/' + id + '/delete-customer', { body: { confirm: typed } }); d.reload();
        }} />}
        {s.state !== 'ended' && <Act label="End now" danger confirm="End this subscription now? SaaS: the customer's modules turn read-only at once. On-prem: renewals stop and the licence ends when the installation next checks." run={async () => { await api('/api/commerce/subscriptions/' + id + '/end', { method: 'POST' }); d.reload(); }} />}
      </div>
    </Card>
    {s.kind === 'onprem' && <Card title="Licence keys">
      {keys.length === 0 ? <Empty>No key yet. Create one and send it to the customer; they type it on their Licence page.</Empty> : null}
      {keys.map((k) => <div key={k.id} className="card" style={{ marginBottom: 8 }}>
        <p><b>Key ending {k.hint}</b> {k.revoked ? <Badge tone="bad">Withdrawn</Badge> : <Badge tone="ok">In use</Badge>} <span className="muted small">for {k.maxActivations} installation(s), made {fmtDay(k.createdAt)}</span>
          {!k.revoked && <span style={{ marginLeft: 8 }}><Act label="Withdraw" danger confirm="Withdraw this key? Installations using it stop getting renewals and it cannot be activated again." run={async () => { await api('/api/commerce/keys/' + k.id + '/revoke', { method: 'POST' }); d.reload(); }} /></span>}</p>
        {(k.activations as Rec[]).length > 0 && <table><thead><tr><th>Installation</th><th>Customer id</th><th>Address</th><th>Version</th><th>Activated</th><th>Last check</th><th></th></tr></thead><tbody>
          {(k.activations as Rec[]).map((x) => <tr key={x.id}><td className="small"><code>{x.installation}</code></td><td>{x.tenantId}</td><td className="small">{x.host}</td><td className="small">{x.version}</td>
            <td>{fmtDay(x.activatedAt)}</td><td>{x.releasedAt ? <span className="muted">released {fmtDay(x.releasedAt)}</span> : x.lastCheck ? fmtDate(x.lastCheck) : '–'}</td>
            <td>{!x.releasedAt && <Act label="Release" confirm="Free this installation's place so the key can be used elsewhere?" run={async () => { await api('/api/commerce/activations/' + x.id + '/release', { method: 'POST' }); d.reload(); }} />}</td></tr>)}
        </tbody></table>}
      </div>)}
      <div className="actions"><Act label="Create a licence key" primary run={async () => {
        const r = await api('/api/commerce/subscriptions/' + id + '/keys', { body: { maxActivations: 1 } }); d.reload();
        setShown({ title: 'New licence key', text: String(r.key), note: 'This is the only time the whole key is shown. Send it to the customer: they type it on their Licence page (Administration › Licence).' });
      }} /></div>
    </Card>}
    <Card title="Licences issued">
      {licences.length === 0 ? <Empty>None yet.</Empty> : <table><thead><tr><th>Issued</th><th>Customer id</th><th>Installation</th><th>Ends</th><th>Why</th><th>By</th><th></th></tr></thead><tbody>
        {licences.map((l) => <tr key={l.id}><td>{fmtDate(l.issuedAt)}</td><td>{l.tenantId}</td><td className="small">{l.installation ? <code>{l.installation.slice(0, 8)}…</code> : '–'}</td><td>{fmtDay(l.expiresAt)}</td><td className="small">{l.reason}</td><td className="small">{l.issuedBy}</td>
          <td><Act label="Show" run={async () => { const r = await api('/api/commerce/licences/' + l.id); setShown({ title: 'Licence', text: String(r.token), note: 'Paste this on the customer\'s Licence page (Install a new licence).' }); }} /></td></tr>)}
      </tbody></table>}
    </Card>
    {editing && <EditSub sub={s} onClose={() => setEditing(false)} onSaved={() => { setEditing(false); d.reload(); }} />}
    {fileFor && <Modal title="Licence file for an offline site" onClose={() => setFileFor(null)}>
      <div className="form">
        <p className="muted">The customer sees both ids on their Licence page. The licence works only on that installation.</p>
        <Field label="Customer id"><input value={fileFor.tenantId} onChange={(e) => setFileFor({ ...fileFor, tenantId: e.target.value.toLowerCase() })} /></Field>
        <Field label="Installation id"><input value={fileFor.installation} onChange={(e) => setFileFor({ ...fileFor, installation: e.target.value.trim().toLowerCase() })} /></Field>
        <div className="actions"><Act label="Issue licence" primary run={async () => {
          const r = await api('/api/commerce/subscriptions/' + id + '/licence', { body: fileFor }); setFileFor(null); d.reload();
          setShown({ title: 'Licence file', text: String(r.token), note: 'Send this text to the customer; they paste it on their Licence page.' });
        }} /><button onClick={() => setFileFor(null)}>Cancel</button></div>
      </div>
    </Modal>}
    {shown && <Modal title={shown.title} onClose={() => setShown(null)}>
      <p className="muted">{shown.note}</p>
      <textarea readOnly rows={shown.text.length > 60 ? 6 : 1} value={shown.text} style={{ width: '100%', fontFamily: 'monospace' }} onFocus={(e) => e.target.select()} />
      <div className="actions"><button className="primary" onClick={() => copyText(shown.text)}>Copy</button><button onClick={() => setShown(null)}>Close</button></div>
    </Modal>}
  </>;
}

function EditSub({ sub, onClose, onSaved }: { sub: Rec; onClose: () => void; onSaved: () => void }) {
  const [lines, setLines] = useState<Lines>(sub.lines ?? {}); const [endsAt, setEnds] = useState(fmtDay(sub.endsAt)); const [grace, setGrace] = useState(String(sub.graceDays));
  const [plan, setPlan] = useState(sub.plan); const [state, setState] = useState(sub.state); const [notes, setNotes] = useState(sub.notes ?? '');
  const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true); setErr('');
    try { await api('/api/commerce/subscriptions/' + sub.id, { method: 'PUT', body: { lines, endsAt, graceDays: Number(grace), plan, state, notes } }); onSaved(); }
    catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return <Modal title="Change subscription" onClose={onClose}>
    <div className="form">
      <ModulePicker lines={lines} onChange={setLines} />
      <div className="row">
        <Field label="Ends"><input type="date" value={endsAt} onChange={(e) => setEnds(e.target.value)} /></Field>
        <Field label="Grace days"><input type="number" min={0} max={90} value={grace} onChange={(e) => setGrace(e.target.value)} /></Field>
        <Field label="Plan name"><input value={plan} onChange={(e) => setPlan(e.target.value)} /></Field>
        <Field label="State"><select value={state} onChange={(e) => setState(e.target.value)}><option value="active">Active</option><option value="cancelled">Cancelled (runs to its end)</option><option value="ended">Ended</option></select></Field>
      </div>
      <Field label="Notes"><textarea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} /></Field>
      <p className="muted small">{sub.kind === 'saas' ? 'The customer gets the new licence as soon as you save.' : 'Activated installations get the new licence at their next daily check (or at once with Check now on their Licence page).'}</p>
      <Err text={err} />
      <div className="actions"><button className="primary" disabled={busy || Object.keys(lines).length === 0} onClick={save}>Save</button><button onClick={onClose}>Cancel</button></div>
    </div>
  </Modal>;
}

function Unlinked() {
  const list = useLoad<Rec[]>('/api/commerce/unlinked');
  const [linking, setLinking] = useState<{ tenantId?: string; token?: string } | null>(null);
  const [name, setName] = useState(''); const [email, setEmail] = useState(''); const [token, setToken] = useState('');
  const [err, setErr] = useState('');
  const link = async () => {
    setErr('');
    try { const r = await api('/api/commerce/link', { body: { ...linking, accountName: name, email } }); setLinking(null); go('vendor/sub/' + r.id); }
    catch (e) { setErr((e as Error).message); }
  };
  return <>
    <Card title="SaaS customers without a subscription">
      <p className="muted">These customers were licensed before the store existed. Linking makes a subscription from the licence they already have; nothing changes for them, and from then on changes and renewals are made here.</p>
      <Loading loading={list.loading && !list.data} error={list.error} />
      {list.data && list.data.length === 0 ? <Empty>Every customer on this platform is linked.</Empty> : null}
      {list.data && list.data.length > 0 && <table><thead><tr><th>Customer id</th><th>Name</th><th>Address</th><th>Licence</th><th></th></tr></thead><tbody>
        {list.data.map((t) => <tr key={t.tenantId}><td>{t.tenantId}</td><td>{t.name}</td><td className="small">{t.host}</td>
          <td className="small">{t.licence ? <>{t.licence.plan}, {(t.licence.modules as string[]).join(', ')}, ends {fmtDay(t.licence.expires)} ({t.licence.state})</> : <span className="muted">no licence</span>}</td>
          <td>{t.licence ? <button onClick={() => { setName(t.name); setEmail(''); setErr(''); setLinking({ tenantId: t.tenantId }); }}>Link</button> : null}</td></tr>)}
      </tbody></table>}
    </Card>
    <Card title="Link an on-prem customer's licence">
      <div className="form">
        <p className="muted">Paste a licence you issued earlier with the licence tool. A subscription is made from it, so you can renew it and give the customer a licence key.</p>
        <Field label="Licence"><textarea rows={4} value={token} onChange={(e) => setToken(e.target.value)} spellCheck={false} /></Field>
        <div className="actions"><button disabled={token.trim().length < 50} onClick={() => { setName(''); setEmail(''); setErr(''); setLinking({ token: token.trim() }); }}>Link…</button></div>
      </div>
    </Card>
    {linking && <Modal title="Link to a subscription" onClose={() => setLinking(null)}>
      <div className="form">
        <Field label="Company name"><input value={name} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Email"><input type="email" value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
        <Err text={err} />
        <div className="actions"><button className="primary" onClick={link}>Link</button><button onClick={() => setLinking(null)}>Cancel</button></div>
      </div>
    </Modal>}
  </>;
}

function Prices() {
  const plans = useLoad<Rec[]>('/api/commerce/plans');
  const byModule = useMemo(() => MODS.map((m) => ({ m, rows: (plans.data ?? []).filter((p) => p.module === m.key) })), [plans.data]);
  return <Card title="Price list">
    <p className="muted">Yearly and monthly prices for SaaS, the yearly on-prem price, and the India price in rupees before GST. The store (release 6.1) sells from this list.</p>
    <Loading loading={plans.loading && !plans.data} error={plans.error} />
    {plans.data && <table><thead><tr><th>Module</th><th>Size</th><th>SaaS yearly (USD)</th><th>SaaS monthly (USD)</th><th>On-prem yearly (USD)</th><th>India yearly (₹)</th><th>On sale</th><th></th></tr></thead><tbody>
      {byModule.flatMap(({ m, rows }) => rows.map((p) => <PriceRow key={p.id} name={m.name} plan={p} saved={plans.reload} />))}
    </tbody></table>}
  </Card>;
}

function PriceRow({ name, plan, saved }: { name: string; plan: Rec; saved: () => void }) {
  const pr = plan.prices ?? {};
  const [v, setV] = useState({ year: pr.saas?.usd?.year ?? '', month: pr.saas?.usd?.month ?? '', onprem: pr.onprem?.usd?.year ?? '', inr: pr.saas?.inr?.year ?? '', active: !!plan.active, label: plan.sizeLabel });
  const n = (x: unknown) => (x === '' || x == null ? undefined : Number(x));
  const save = () => api('/api/commerce/plans/' + plan.id, { method: 'PUT', body: { sizeLabel: v.label, limits: plan.limits, active: v.active,
    prices: { saas: { usd: { year: n(v.year), month: n(v.month) }, inr: { year: n(v.inr) } }, onprem: { usd: { year: n(v.onprem) } } } } }).then(saved);
  const num = (k: 'year' | 'month' | 'onprem' | 'inr') => <input type="number" min={0} style={{ width: 100 }} aria-label={name + ' ' + v.label + ' ' + k} value={v[k]} onChange={(e) => setV({ ...v, [k]: e.target.value })} />;
  return <tr><td>{name}</td><td><input value={v.label} style={{ width: 140 }} aria-label={name + ' size name'} onChange={(e) => setV({ ...v, label: e.target.value })} /></td>
    <td>{num('year')}</td><td>{num('month')}</td><td>{num('onprem')}</td><td>{num('inr')}</td>
    <td><input type="checkbox" checked={v.active} aria-label={name + ' ' + v.label + ' on sale'} onChange={(e) => setV({ ...v, active: e.target.checked })} /></td>
    <td><Act label="Save" run={save} /></td></tr>;
}

function SigningKey({ status, reload }: { status: Rec | null; reload: () => void }) {
  const [pem, setPem] = useState(''); const [err, setErr] = useState(''); const [ok, setOk] = useState('');
  const sg = (status?.signing ?? {}) as Rec;
  const save = async () => {
    setErr(''); setOk('');
    try { await api('/api/commerce/signing-key', { method: 'PUT', body: { pem } }); setPem(''); setOk('The signing key is stored. It will not be shown again.'); reload(); }
    catch (e) { setErr((e as Error).message); }
  };
  return <Card title="Licence signing key">
    <Dl rows={[['State', sg.ready ? <Badge key="r" tone="ok">Ready</Badge> : <Badge key="r" tone="bad">Not set up</Badge>], ['Kept in', sg.source === 'configuration' ? 'the server configuration (Commerce:SigningKeyPem)' : sg.source === 'stored' ? 'this platform, sealed' : sg.source],
      ['Key id', sg.kid ? <Copy key="k" text={String(sg.kid)} /> : ''], ['Vendor public key configured', status?.vendorKey ? 'Yes' : 'No (Licensing:VendorPublicKeyPem is missing)']]} />
    {sg.source !== 'configuration' && <div className="form">
      <p className="muted">Paste the private key made with the licence tool (vendor-private.pem). It must belong to the vendor public key every installation trusts. It is sealed on this server and never shown again; keep your own offline copy in a safe place.</p>
      <Field label="Private key"><textarea rows={6} value={pem} onChange={(e) => setPem(e.target.value)} spellCheck={false} placeholder="-----BEGIN PRIVATE KEY-----" autoComplete="off" /></Field>
      <Err text={err} />{ok && <p role="status">{ok}</p>}
      <div className="actions"><button className="primary" disabled={pem.trim().length < 50} onClick={save}>{sg.ready ? 'Replace the key' : 'Store the key'}</button></div>
    </div>}
  </Card>;
}

const ORDER: Record<string, ['ok' | 'warn' | 'bad' | undefined, string]> = { pending: [undefined, 'Waiting for payment'], paid: ['warn', 'Paid'], fulfilling: ['warn', 'Setting up'], fulfilled: ['ok', 'Done'], failed: ['bad', 'Needs attention'] };
const cash = (v: number, c: string) => (c === 'INR' ? '₹' + v.toLocaleString('en-IN', { maximumFractionDigits: 2 }) : '$' + v.toLocaleString('en-US', { maximumFractionDigits: 2 }));

function Orders() {
  const [status, setStatus] = useState('');
  const list = useLoad<Rec[]>('/api/commerce/orders?status=' + status, [status]);
  return <Card>
    <div className="form"><Field label="Show"><select value={status} onChange={(e) => setStatus(e.target.value)}><option value="">All orders</option><option value="failed">Needs attention</option><option value="fulfilled">Done</option><option value="pending">Waiting for payment</option></select></Field></div>
    <Loading loading={list.loading && !list.data} error={list.error} />
    {list.data && list.data.length === 0 ? <Empty>No orders yet. Orders from the store (#/store) appear here.</Empty> : null}
    {list.data && list.data.length > 0 && <table><thead><tr><th>Date</th><th>Customer</th><th>Type</th><th>Total</th><th>State</th><th>Invoice</th><th></th></tr></thead><tbody>
      {list.data.map((o) => { const [tone, label] = ORDER[o.status] ?? [undefined, o.status];
        return <tr key={o.id}><td>{fmtDate(o.createdAt)}</td><td><b>{o.company}</b><div className="small muted">{o.email}</div></td><td>{o.kind === 'saas' ? 'SaaS ' + o.tenantId : 'On-prem'}</td>
          <td>{cash(o.total, o.currency)}</td><td><Badge tone={tone}>{label}</Badge>{o.error && <div className="small err">{o.error}</div>}</td>
          <td>{o.invoiceId ? <a href={'/api/commerce/invoices/' + o.invoiceId + '/pdf'} onClick={(e) => { e.preventDefault(); void download(o.invoiceId, o.invoice); }}>{o.invoice}</a> : ''}</td>
          <td>{o.subscriptionId && <a href={'#/vendor/sub/' + o.subscriptionId}>Subscription</a>}{o.status === 'failed' && <Act label="Try again" run={async () => { await api('/api/commerce/orders/' + o.id + '/retry', { method: 'POST' }); list.reload(); }} />}</td></tr>; })}
    </tbody></table>}
  </Card>;
}

async function download(id: number, number: string) {
  const { accessToken } = await import('../auth');
  const r = await fetch('/api/commerce/invoices/' + id + '/pdf', { headers: { Authorization: 'Bearer ' + (accessToken() ?? '') } });
  if (!r.ok) return;
  const url = URL.createObjectURL(await r.blob()); const a = document.createElement('a'); a.href = url; a.download = 'Invoice-' + number.replaceAll('/', '-') + '.pdf'; a.click(); URL.revokeObjectURL(url);
}

function InvoiceList() {
  const list = useLoad<Rec[]>('/api/commerce/invoices');
  return <Card title="Invoices">
    <Loading loading={list.loading && !list.data} error={list.error} />
    {list.data && list.data.length === 0 ? <Empty>No invoices yet.</Empty> : null}
    {list.data && list.data.length > 0 && <table><thead><tr><th>Number</th><th>Date</th><th>Customer</th><th>GSTIN</th><th>Taxable</th><th>Tax</th><th>Total</th></tr></thead><tbody>
      {list.data.map((i) => <tr key={i.id}><td><a href="#" onClick={(e) => { e.preventDefault(); void download(i.id, i.number); }}>{i.number}</a></td><td>{fmtDay(i.issuedAt)}</td><td>{i.company}</td><td className="small">{i.gstin}</td>
        <td>{cash(i.subtotal, i.currency)}</td><td>{cash(i.tax, i.currency)}</td><td><b>{cash(i.total, i.currency)}</b></td></tr>)}
    </tbody></table>}
  </Card>;
}

const BIZ_FIELDS: [string, string, string?][] = [['legalName', 'Legal name'], ['tradeName', 'Brand name'], ['address', 'Address'], ['city', 'City'], ['state', 'State'], ['pincode', 'PIN code'], ['country', 'Country'],
  ['gstin', 'GSTIN'], ['cin', 'CIN'], ['pan', 'PAN'], ['sac', 'SAC code', 'number'], ['lut', 'LUT reference (exports)'], ['supportEmail', 'Support email'], ['supportPhone', 'Support phone'],
  ['grievanceName', 'Grievance officer'], ['grievanceDesignation', 'Designation'], ['grievanceEmail', 'Grievance email'], ['invoicePrefix', 'Invoice prefix'],
  ['termsUrl', 'Terms of Service link'], ['privacyUrl', 'Privacy Policy link'], ['refundUrl', 'Cancellation and Refund link'], ['deliveryUrl', 'Delivery policy link'], ['tenantDomain', 'Domain for customer addresses']];

function BusinessDetails() {
  const d = useLoad<Rec>('/api/commerce/business');
  const [v, setV] = useState<Rec | null>(null); const [err, setErr] = useState(''); const [ok, setOk] = useState('');
  useEffect(() => { if (d.data && !v) setV(d.data); }, [d.data, v]);
  if (!v) return <Loading loading={d.loading} error={d.error} />;
  const save = async () => { setErr(''); setOk(''); try { setV(await api('/api/commerce/business', { method: 'PUT', body: v })); setOk('Saved. New invoices and the store pages use these details.'); } catch (e) { setErr((e as Error).message); } };
  return <Card title="Business details">
    <p className="muted">Shown on invoices and in the store. Change the address here once your registrations are updated; invoices already issued keep the details they were made with.</p>
    <div className="form"><div className="row">
      {BIZ_FIELDS.map(([k, label]) => <Field key={k} label={label}><input value={v[k] ?? ''} onChange={(e) => setV({ ...v, [k]: e.target.value })} /></Field>)}
      <Field label="Trial days"><input type="number" min={1} max={90} value={v.trialDays ?? 14} onChange={(e) => setV({ ...v, trialDays: Number(e.target.value) })} /></Field>
      <Field label="Keep unconverted trials (days)"><input type="number" min={8} max={365} value={v.trialKeepDays ?? 45} onChange={(e) => setV({ ...v, trialKeepDays: Number(e.target.value) })} /></Field>
    </div>
    <Err text={err} />{ok && <p role="status">{ok}</p>}
    <div className="actions"><button className="primary" onClick={() => void save()}>Save</button></div></div>
  </Card>;
}

function Payments() {
  const d = useLoad<Rec>('/api/commerce/payments');
  const [keyId, setKeyId] = useState(''); const [secret, setSecret] = useState(''); const [hook, setHook] = useState(''); const [err, setErr] = useState(''); const [ok, setOk] = useState('');
  const rz = (d.data?.razorpay ?? {}) as Rec;
  useEffect(() => { if (rz.keyId) setKeyId(String(rz.keyId)); }, [rz.keyId]);
  const save = async () => { setErr(''); setOk(''); try { await api('/api/commerce/payments', { method: 'PUT', body: { keyId: keyId.trim(), keySecret: secret, webhookSecret: hook } }); setSecret(''); setHook(''); setOk('Saved.'); d.reload(); } catch (e) { setErr((e as Error).message); } };
  return <Card title="Razorpay">
    <Dl rows={[['Mode', rz.mode === 'live' ? <Badge key="m" tone="ok">Live</Badge> : rz.mode === 'test' ? <Badge key="m" tone="warn">Test (no real money)</Badge> : <Badge key="m">Off</Badge>],
      ['Key secret', rz.hasSecret ? 'Saved' : 'Not saved'], ['Webhook secret', rz.hasWebhookSecret ? 'Saved' : 'Not saved'], ['Webhook address', location.origin + (rz.webhookPath ?? '/api/store/webhooks/razorpay')]]} />
    <div className="form">
      <p className="muted">In the Razorpay dashboard: Account &amp; Settings › API keys for the key id and secret; Webhooks › Add new webhook with the address above, events payment.captured and order.paid, and a secret you choose. Secrets are sealed and never shown again; leave a box empty to keep the saved one.</p>
      <div className="row">
        <Field label="Key id"><input value={keyId} onChange={(e) => setKeyId(e.target.value)} placeholder="rzp_test_…" spellCheck={false} /></Field>
        <Field label="Key secret"><input type="password" value={secret} onChange={(e) => setSecret(e.target.value)} autoComplete="off" placeholder={rz.hasSecret ? 'saved' : ''} /></Field>
        <Field label="Webhook secret"><input type="password" value={hook} onChange={(e) => setHook(e.target.value)} autoComplete="off" placeholder={rz.hasWebhookSecret ? 'saved' : ''} /></Field>
      </div>
      <Err text={err} />{ok && <p role="status">{ok}</p>}
      <div className="actions"><button className="primary" onClick={() => void save()}>Save</button><a href="#/store" target="_blank" rel="noreferrer">Open the store</a></div>
    </div>
  </Card>;
}
