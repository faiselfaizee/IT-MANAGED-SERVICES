import { useEffect, useMemo, useRef, useState } from 'react';
import { Logo } from '../chrome';

/* eslint-disable @typescript-eslint/no-explicit-any */
type Rec = Record<string, any>;
type Plan = { id: number; module: string; moduleName: string; sizeLabel: string; limits: Record<string, number>;
  inr: { year: number | null; month: number | null; onprem: number | null }; usd: { year: number | null; month: number | null; onprem: number | null } };

export const STATES: [string, string][] = [['01', 'Jammu and Kashmir'], ['02', 'Himachal Pradesh'], ['03', 'Punjab'], ['04', 'Chandigarh'], ['05', 'Uttarakhand'], ['06', 'Haryana'], ['07', 'Delhi'], ['08', 'Rajasthan'],
  ['09', 'Uttar Pradesh'], ['10', 'Bihar'], ['11', 'Sikkim'], ['12', 'Arunachal Pradesh'], ['13', 'Nagaland'], ['14', 'Manipur'], ['15', 'Mizoram'], ['16', 'Tripura'], ['17', 'Meghalaya'], ['18', 'Assam'],
  ['19', 'West Bengal'], ['20', 'Jharkhand'], ['21', 'Odisha'], ['22', 'Chhattisgarh'], ['23', 'Madhya Pradesh'], ['24', 'Gujarat'], ['26', 'Dadra and Nagar Haveli and Daman and Diu'], ['27', 'Maharashtra'],
  ['29', 'Karnataka'], ['30', 'Goa'], ['31', 'Lakshadweep'], ['32', 'Kerala'], ['33', 'Tamil Nadu'], ['34', 'Puducherry'], ['35', 'Andaman and Nicobar Islands'], ['36', 'Telangana'], ['37', 'Andhra Pradesh'], ['38', 'Ladakh'], ['97', 'Other territory']];
const COUNTRIES: [string, string][] = [['IN', 'India'], ['AE', 'United Arab Emirates'], ['AU', 'Australia'], ['BD', 'Bangladesh'], ['BH', 'Bahrain'], ['CA', 'Canada'], ['DE', 'Germany'], ['FR', 'France'], ['GB', 'United Kingdom'],
  ['IE', 'Ireland'], ['KE', 'Kenya'], ['KW', 'Kuwait'], ['LK', 'Sri Lanka'], ['MY', 'Malaysia'], ['NL', 'Netherlands'], ['NP', 'Nepal'], ['NZ', 'New Zealand'], ['OM', 'Oman'], ['QA', 'Qatar'], ['SA', 'Saudi Arabia'],
  ['SG', 'Singapore'], ['US', 'United States'], ['ZA', 'South Africa']];

async function call(url: string, body?: unknown): Promise<Rec> {
  const r = await fetch(url, body === undefined ? undefined : { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(String(j.detail || j.error || 'Something went wrong (' + r.status + ').'));
  return j;
}
const money = (v: number | null | undefined, cur: string) => v == null ? '–' : (cur === 'INR' ? '₹' + v.toLocaleString('en-IN', { minimumFractionDigits: 0, maximumFractionDigits: 2 }) : '$' + v.toLocaleString('en-US', { maximumFractionDigits: 2 }));
const go = (h: string) => { location.hash = h; };

function loadCheckout(): Promise<void> {
  if ((window as any).Razorpay) return Promise.resolve();
  return new Promise((ok, fail) => { const s = document.createElement('script'); s.src = 'https://checkout.razorpay.com/v1/checkout.js'; s.onload = () => ok(); s.onerror = () => fail(new Error('The payment window could not be loaded.')); document.head.appendChild(s); });
}

/** The public store: price list, checkout with Razorpay, free trials and the order page. No sign-in. */
export default function StorePage({ path }: { path: string[] }) {
  const [info, setInfo] = useState<Rec | null>(null); const [err, setErr] = useState('');
  useEffect(() => { void call('/api/store/info').then(setInfo, (e) => setErr((e as Error).message)); }, []);
  const b = (info?.business ?? {}) as Rec;
  const tab = path[0] ?? '';
  return <main className="store">
    <header className="store-head"><a href="#/store" className="store-brand"><Logo size={30} /><strong>{b.tradeName || 'DeskAsk'}</strong></a>
      <nav><a href="#/store" className={tab === '' ? 'on' : ''}>Buy</a><a href="#/store/trial" className={tab === 'trial' ? 'on' : ''}>Free trial</a></nav></header>
    {err && <p className="err" role="alert">{err}</p>}
    {!info ? <p className="muted">Loading…</p>
      : tab === 'order' ? <OrderPage id={path[1] ?? ''} token={path[2] ?? ''} />
      : tab === 'trial' ? <Trial info={info} />
      : <Buy info={info} />}
    <footer className="store-foot">
      {b.legalName && <p><b>{b.legalName}</b> · {[b.address, b.city, b.state, b.pincode].filter(Boolean).join(', ')}{b.gstin ? ' · GSTIN ' + b.gstin : ''}{b.cin ? ' · CIN ' + b.cin : ''}</p>}
      {b.supportEmail && <p>Support: <a href={'mailto:' + b.supportEmail}>{b.supportEmail}</a>{b.supportPhone ? ' · ' + b.supportPhone : ''}{b.grievanceName ? ' · Grievance officer: ' + b.grievanceName + (b.grievanceDesignation ? ', ' + b.grievanceDesignation : '') : ''}</p>}
      <p className="links">{[['Terms of Service', b.termsUrl], ['Privacy Policy', b.privacyUrl], ['Cancellation and Refunds', b.refundUrl], ['Delivery', b.deliveryUrl]].filter(([, u]) => u).map(([l, u]) => <a key={l} href={u} target="_blank" rel="noreferrer">{l}</a>)}</p>
    </footer>
  </main>;
}

function Buy({ info }: { info: Rec }) {
  const [plans, setPlans] = useState<Plan[]>([]);
  const [kind, setKind] = useState('saas'); const [period, setPeriod] = useState('year'); const [country, setCountry] = useState('IN'); const [stateCode, setState] = useState('33'); const [gstin, setGstin] = useState('');
  const [sel, setSel] = useState<Record<string, number>>({});
  const [quote, setQuote] = useState<Rec | null>(null); const [qErr, setQErr] = useState('');
  const [buyer, setBuyer] = useState({ company: '', name: '', email: '', phone: '', address: '', city: '', pincode: '' });
  const [tenantId, setTenantId] = useState(''); const [avail, setAvail] = useState<Rec | null>(null);
  const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  useEffect(() => { void call('/api/store/plans').then((p) => setPlans(p as unknown as Plan[]), () => undefined); }, []);
  const cur = country === 'IN' ? 'INR' : 'USD';
  const modules = useMemo(() => { const m: Record<string, Plan[]> = {}; for (const p of plans) (m[p.module] ??= []).push(p); return m; }, [plans]);
  const price = (p: Plan) => { const x = cur === 'INR' ? p.inr : p.usd; return kind === 'onprem' ? x.onprem : period === 'year' ? x.year : x.month; };
  useEffect(() => { if (kind === 'onprem') setPeriod('year'); }, [kind]);
  const req = { kind, period, plans: sel, country, stateCode, gstin: gstin.trim().toUpperCase() };
  const reqKey = JSON.stringify(req);
  useEffect(() => {
    if (Object.keys(sel).length === 0) { setQuote(null); setQErr(''); return; }
    const t = setTimeout(() => { void call('/api/store/quote', req).then((q) => { setQuote(q); setQErr(''); }, (e) => { setQuote(null); setQErr((e as Error).message); }); }, 250);
    return () => clearTimeout(t);
  }, [reqKey]); // eslint-disable-line react-hooks/exhaustive-deps
  const timer = useRef<ReturnType<typeof setTimeout>>();
  const checkName = (v: string) => { setTenantId(v); setAvail(null); clearTimeout(timer.current); if (v.length >= 2) timer.current = setTimeout(() => { void call('/api/store/available?id=' + encodeURIComponent(v)).then(setAvail, () => undefined); }, 350); };
  const pay = async () => {
    setBusy(true); setErr('');
    try {
      const stateName = STATES.find(([c]) => c === stateCode)?.[1] ?? '';
      const r = await call('/api/store/checkout', { ...req, tenantId, buyer: { ...buyer, stateName: country === 'IN' ? stateName : '' } });
      await loadCheckout();
      const rz = new (window as any).Razorpay({
        ...r.razorpay, theme: { color: '#0f766e' },
        handler: async (resp: Rec) => {
          try { await call('/api/store/checkout/confirm', resp); } catch { /* the webhook completes it */ }
          go('#/store/order/' + r.orderId + '/' + r.access);
        },
        modal: { ondismiss: () => setBusy(false) },
      });
      rz.on?.('payment.failed', (f: Rec) => setErr('The payment did not go through: ' + (f?.error?.description ?? 'please try again.')));
      rz.open();
    } catch (e) { setErr((e as Error).message); setBusy(false); }
  };
  const ready = !!quote && buyer.company && buyer.name && buyer.email && buyer.address && (kind === 'onprem' || avail?.available);
  return <div className="store-body">
    <section className="store-card">
      <h1>Choose your DeskAsk</h1>
      <p className="muted">One edition per module, every feature included. Pick the size you need; prices {cur === 'INR' ? 'in rupees, GST added at checkout' : 'in US dollars'}.</p>
      <div className="store-row">
        <label>How you run it<select value={kind} onChange={(e) => setKind(e.target.value)}><option value="saas">In our cloud (SaaS)</option><option value="onprem">On your own server (on-prem)</option></select></label>
        <label>Billing<select value={period} onChange={(e) => setPeriod(e.target.value)} disabled={kind === 'onprem'}><option value="year">Yearly</option><option value="month">Monthly</option></select></label>
        <label>Country<select value={country} onChange={(e) => setCountry(e.target.value)}>{COUNTRIES.map(([c, n]) => <option key={c} value={c}>{n}</option>)}</select></label>
        {country === 'IN' && <label>State<select value={stateCode} onChange={(e) => setState(e.target.value)}>{STATES.map(([c, n]) => <option key={c} value={c}>{n}</option>)}</select></label>}
      </div>
      <table className="store-plans"><thead><tr><th>Module</th><th>Size</th><th>Price</th></tr></thead><tbody>
        {Object.entries(modules).map(([m, list]) => <tr key={m}><td><b>{list[0].moduleName}</b></td><td>
          <select aria-label={list[0].moduleName + ' size'} value={sel[m] ?? ''} onChange={(e) => { const v = e.target.value; const n = { ...sel }; if (v) n[m] = Number(v); else delete n[m]; setSel(n); }}>
            <option value="">Not needed</option>{list.map((p) => <option key={p.id} value={p.id}>{p.sizeLabel} · {money(price(p), cur)}</option>)}
          </select></td><td>{sel[m] ? money(price(list.find((p) => p.id === sel[m])!), cur) : ''}</td></tr>)}
      </tbody></table>
      <p className="muted small">4 or more modules: 25% off. Monitoring needs Asset management.</p>
      {qErr && <p className="err" role="alert">{qErr}</p>}
    </section>
    <section className="store-card">
      <h2>Your details</h2>
      <div className="store-row">
        <label>Company<input value={buyer.company} onChange={(e) => setBuyer({ ...buyer, company: e.target.value })} autoComplete="organization" /></label>
        <label>Your name<input value={buyer.name} onChange={(e) => setBuyer({ ...buyer, name: e.target.value })} autoComplete="name" /></label>
        <label>Work email<input type="email" value={buyer.email} onChange={(e) => setBuyer({ ...buyer, email: e.target.value })} autoComplete="email" /></label>
        <label>Phone<input value={buyer.phone} onChange={(e) => setBuyer({ ...buyer, phone: e.target.value })} autoComplete="tel" /></label>
      </div>
      <label>Billing address<input value={buyer.address} onChange={(e) => setBuyer({ ...buyer, address: e.target.value })} autoComplete="street-address" /></label>
      <div className="store-row">
        <label>City<input value={buyer.city} onChange={(e) => setBuyer({ ...buyer, city: e.target.value })} autoComplete="address-level2" /></label>
        <label>PIN / postcode<input value={buyer.pincode} onChange={(e) => setBuyer({ ...buyer, pincode: e.target.value })} autoComplete="postal-code" /></label>
        {country === 'IN' && <label>GSTIN (optional)<input value={gstin} onChange={(e) => setGstin(e.target.value.toUpperCase())} maxLength={15} /></label>}
      </div>
      {kind === 'saas' && <label>Your DeskAsk address
        <span className="store-addr"><input value={tenantId} onChange={(e) => checkName(e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, ''))} placeholder="yourcompany" aria-label="Your DeskAsk address" /><span>.{info.business?.tenantDomain ?? 'deskask.in'}</span></span>
        {avail && <small className={avail.available ? 'ok' : 'err'}>{avail.available ? avail.address + ' is free' : avail.reason}</small>}</label>}
    </section>
    <aside className="store-card store-sum">
      <h2>Summary</h2>
      {!quote ? <p className="muted">Choose at least one module.</p> : <>
        <table><tbody>
          {(quote.lines as Rec[]).map((l) => <tr key={l.module}><td>{l.description}</td><td className="num">{money(l.amount, quote.currency)}</td></tr>)}
          {quote.discount > 0 && <tr><td>Suite discount (25%)</td><td className="num">−{money(quote.discount, quote.currency)}</td></tr>}
          {(quote.taxes as Rec[]).map((t) => <tr key={t.name}><td>{t.name} {t.rate}%</td><td className="num">{money(t.amount, quote.currency)}</td></tr>)}
          <tr className="total"><td>Total</td><td className="num">{money(quote.total, quote.currency)}</td></tr>
        </tbody></table>
        {quote.taxNote && <p className="muted small">{quote.taxNote}</p>}
      </>}
      {err && <p className="err" role="alert">{err}</p>}
      {info.open ? <button className="primary" disabled={!ready || busy} onClick={() => void pay()}>{busy ? 'Opening payment…' : 'Pay securely'}</button>
        : <p className="muted">Online payment opens soon. Write to {info.business?.supportEmail || 'us'} for a quote.</p>}
      {info.payments === 'test' && <p className="muted small">Test mode: no real money is taken.</p>}
      <p className="muted small">By paying you accept the Terms of Service and the Cancellation and Refund Policy.</p>
    </aside>
  </div>;
}

const TRIAL_MODULES: [string, string][] = [['servicedesk', 'Service Desk'], ['assets', 'Asset management'], ['monitoring', 'Monitoring'], ['adsuite', 'AD Manager'], ['vault', 'Vault'], ['backup', 'Backup']];

function Trial({ info }: { info: Rec }) {
  const [f, setF] = useState({ company: '', name: '', email: '', tenantId: '' }); const [mods, setMods] = useState<string[]>(['servicedesk', 'assets']);
  const [avail, setAvail] = useState<Rec | null>(null); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false); const [done, setDone] = useState<Rec | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>();
  const name = (v: string) => { setF({ ...f, tenantId: v }); setAvail(null); clearTimeout(timer.current); if (v.length >= 2) timer.current = setTimeout(() => { void call('/api/store/available?id=' + encodeURIComponent(v)).then(setAvail, () => undefined); }, 350); };
  const start = async () => { setBusy(true); setErr(''); try { setDone(await call('/api/store/trial', { ...f, modules: mods })); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } };
  if (done) return <div className="store-body"><section className="store-card"><h1>Your trial is ready</h1>
    <p>We have emailed <b>{f.email}</b> a link to choose your password. Then sign in at <a href={done.address}>{done.address}</a>.</p>
    <p className="muted">The trial runs until {new Date(done.ends).toLocaleDateString()}. Buy a plan at any time to keep everything you set up.</p></section></div>;
  return <div className="store-body"><section className="store-card">
    <h1>Try DeskAsk free for {info.business?.trialDays ?? 14} days</h1>
    <p className="muted">No card needed. Your own DeskAsk is ready in a minute.</p>
    <div className="store-row">
      <label>Company<input value={f.company} onChange={(e) => setF({ ...f, company: e.target.value })} /></label>
      <label>Your name<input value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} /></label>
      <label>Work email<input type="email" value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} /></label>
    </div>
    <label>Your DeskAsk address<span className="store-addr"><input value={f.tenantId} onChange={(e) => name(e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, ''))} placeholder="yourcompany" aria-label="Trial address" /><span>.{info.business?.tenantDomain ?? 'deskask.in'}</span></span>
      {avail && <small className={avail.available ? 'ok' : 'err'}>{avail.available ? avail.address + ' is free' : avail.reason}</small>}</label>
    <fieldset className="store-mods"><legend>Modules to try</legend>
      {TRIAL_MODULES.map(([k, n]) => <label key={k} className="check"><input type="checkbox" checked={mods.includes(k)} onChange={(e) => setMods(e.target.checked ? [...mods, k] : mods.filter((x) => x !== k))} />{n}</label>)}</fieldset>
    {err && <p className="err" role="alert">{err}</p>}
    {info.trials ? <button className="primary" disabled={busy || !f.company || !f.name || !f.email || !avail?.available || mods.length === 0} onClick={() => void start()}>{busy ? 'Creating your DeskAsk…' : 'Start free trial'}</button>
      : <p className="muted">Trials open soon.</p>}
  </section></div>;
}

function OrderPage({ id, token }: { id: string; token: string }) {
  const [o, setO] = useState<Rec | null>(null); const [err, setErr] = useState('');
  useEffect(() => {
    let stop = false; let n = 0;
    const tick = async () => {
      try { const r = await call('/api/store/orders/' + encodeURIComponent(id) + '?t=' + encodeURIComponent(token)); if (stop) return; setO(r); if (r.status === 'fulfilled' || r.status === 'failed') return; }
      catch (e) { if (!stop) setErr((e as Error).message); return; }
      if (++n < 100) setTimeout(() => void tick(), 3000);
    };
    void tick();
    return () => { stop = true; };
  }, [id, token]);
  return <div className="store-body"><section className="store-card">
    {err ? <p className="err">{err}</p> : !o ? <p className="muted">Checking your order…</p>
      : o.status === 'fulfilled' ? <>
        <h1>Thank you, your order is complete</h1>
        {o.kind === 'saas' ? <p>Your DeskAsk is at <a href={o.address}>{o.address}</a>. We have emailed you a link to choose your password (if you were on a trial, sign in as before).</p>
          : <><p>Your licence key (also emailed to you):</p><p className="store-key">{o.licenceKey ?? 'See your email.'}</p><p className="muted">In your DeskAsk, open Administration › Licence and type the key under Licence key.</p></>}
        {o.invoice && <p><a href={'/api/store/orders/' + encodeURIComponent(id) + '/invoice?t=' + encodeURIComponent(token)}>Download invoice {o.invoice}</a></p>}
      </> : o.status === 'failed' ? <><h1>Almost there</h1><p>{o.error}</p></>
      : o.status === 'pending' ? <><h1>Waiting for the payment</h1><p className="muted">If you have paid, this page updates in a moment.</p></>
      : <><h1>Setting up your order…</h1><p className="muted">This takes up to a minute.</p></>}
  </section></div>;
}
