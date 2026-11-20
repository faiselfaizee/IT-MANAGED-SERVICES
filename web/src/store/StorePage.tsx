import { useEffect, useMemo, useRef, useState } from 'react';
import { Logo } from '../chrome';
import { FAQ, MODULES, type Module } from './content';
import { Markdown } from './Markdown';
import './site.css';

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

const LEGAL: [string, string][] = [['terms', 'Terms of Service'], ['privacy', 'Privacy Policy'], ['refund', 'Cancellation and refunds'], ['delivery', 'Delivery']];

/** The DeskAsk website on the vendor platform: home, product pages, pricing with checkout, free trial, FAQ, About, Contact and the policy pages. No sign-in. */
export default function StorePage({ path }: { path: string[] }) {
  const [info, setInfo] = useState<Rec | null>(null); const [err, setErr] = useState('');
  const [menu, setMenu] = useState(false);
  // with the website linked and the platform store hidden, the store lives on the website
  useEffect(() => { void call('/api/store/info').then((i) => { if (typeof i.redirect === 'string' && i.redirect.startsWith('https://')) location.href = i.redirect; else setInfo(i); }, (e) => setErr((e as Error).message)); }, []);
  const tab = path[0] ?? '';
  useEffect(() => { setMenu(false); if (!navigator.userAgent.includes('jsdom')) window.scrollTo?.(0, 0); }, [path.join('/')]); // eslint-disable-line react-hooks/exhaustive-deps
  const b = (info?.business ?? {}) as Rec;
  const legalHref = (slug: string) => { const ext = ({ terms: b.termsUrl, privacy: b.privacyUrl, refund: b.refundUrl, delivery: b.deliveryUrl } as Rec)[slug]; return ext || '#/store/' + slug; };
  const nav: [string, string, boolean][] = [['#/store/products', 'Products', tab === 'products'], ['#/store/pricing', 'Pricing', tab === 'pricing'], ['#/store/faq', 'FAQ', tab === 'faq'], ['#/store/about', 'About', tab === 'about'], ['#/store/contact', 'Contact', tab === 'contact']];
  let page;
  if (!info) page = err ? <section className="site-wrap"><p className="err" role="alert">{err}</p></section> : <section className="site-wrap"><p className="muted">Loading…</p></section>;
  else if (tab === 'order') page = <OrderPage id={path[1] ?? ''} token={path[2] ?? ''} />;
  else if (tab === 'trial') page = <Trial info={info} />;
  else if (tab === 'pricing') page = <Buy info={info} preset={path[1]} />;
  else if (tab === 'products') page = path[1] ? <Product slug={path[1]} /> : <Products />;
  else if (tab === 'faq') page = <Faq />;
  else if (tab === 'contact') page = <Contact b={b} />;
  else if (['about', 'terms', 'privacy', 'refund', 'delivery'].includes(tab)) page = <TextPage slug={tab} />;
  else page = <Home info={info} />;
  return <div className="site">
    <a className="site-skip" href="#site-main" onClick={(e) => { e.preventDefault(); document.getElementById('site-main')?.focus(); }}>Skip to content</a>
    <header className="site-head">
      <div className="site-head-in">
        <a href="#/store" className="site-brand" aria-label="DeskAsk home"><Logo size={30} /><span>{b.tradeName || 'DeskAsk'}</span></a>
        <button className="site-menu-btn" aria-expanded={menu} aria-controls="site-nav" onClick={() => setMenu(!menu)}>Menu</button>
        <nav id="site-nav" className={menu ? 'open' : ''} aria-label="Main">
          {nav.map(([h, l, on]) => <a key={h} href={h} aria-current={on ? 'page' : undefined}>{l}</a>)}
          <a href="#/store/trial" className="site-btn ghost">Free trial</a>
          <a href="#/store/pricing" className="site-btn">Buy</a>
        </nav>
      </div>
    </header>
    <main id="site-main" tabIndex={-1}>{page}</main>
    <footer className="site-foot">
      <div className="site-foot-in">
        <div className="site-foot-brand"><a href="#/store" className="site-brand"><Logo size={26} /><span>{b.tradeName || 'DeskAsk'}</span></a>
          <p>IT management software made in Chennai.</p></div>
        <div><h2>Products</h2>{MODULES.map((m) => <a key={m.key} href={'#/store/products/' + m.slug}>{m.name}</a>)}</div>
        <div><h2>Company</h2><a href="#/store/about">About us</a><a href="#/store/contact">Contact us</a><a href="#/store/pricing">Pricing</a><a href="#/store/faq">FAQ</a></div>
        <div><h2>Policies</h2>{LEGAL.map(([s, l]) => <a key={s} href={legalHref(s)}>{l}</a>)}</div>
      </div>
      <div className="site-foot-legal">
        {b.legalName && <p><b>{b.legalName}</b>, {[b.address, b.city, b.state, b.pincode].filter(Boolean).join(', ')}</p>}
        <p>{[b.gstin && 'GSTIN ' + b.gstin, b.cin && 'CIN ' + b.cin].filter(Boolean).join(', ')}</p>
        {b.supportEmail && <p>Support: <a href={'mailto:' + b.supportEmail}>{b.supportEmail}</a>{b.supportPhone ? ', ' + b.supportPhone : ''}{b.grievanceName ? '. Grievance officer: ' + b.grievanceName + (b.grievanceDesignation ? ', ' + b.grievanceDesignation : '') : ''}</p>}
      </div>
    </footer>
  </div>;
}

/* ------------------------------------------------------------------ home */

/** The hero's picture: one real problem travelling through DeskAsk, from alert to fix. It plays once; reduced motion shows it finished. */
function Journey() {
  const steps: [string, string, string, string][] = [
    ['alert', 'Monitoring', 'SRV-DB01 disk C: at 94%', '09:12'],
    ['ticket', 'Service Desk', 'INC-0142 opened, priority P2', '09:12'],
    ['asset', 'Asset management', 'Linked to AST-000318, Dell R640, warranty to 2027', '09:13'],
    ['vault', 'Vault', 'Engineer opened the server login (logged)', '09:21'],
    ['done', 'Resolved', 'Old logs archived, disk at 61%. Customer mailed.', '09:30'],
  ];
  return <figure className="journey" aria-label="How one problem moves through DeskAsk">
    <figcaption><span className="journey-dot" aria-hidden="true" />Example: one full disk, start to finish</figcaption>
    <ol>{steps.map(([k, mod, what, at], n) => <li key={k} className={'j-' + k} style={{ ['--d' as any]: n }}>
      <span className="j-time">{at}</span><span className="j-mod">{mod}</span><span className="j-what">{what}</span></li>)}</ol>
    <p className="journey-sum">18 minutes from alert to fixed. Nobody copied a serial number or hunted for a password.</p>
  </figure>;
}

function useFrom(): Rec | null {
  const [plans, setPlans] = useState<Plan[] | null>(null);
  useEffect(() => { void call('/api/store/plans').then((p) => setPlans(p as unknown as Plan[]), () => setPlans([])); }, []);
  return useMemo(() => {
    if (!plans) return null;
    const from: Rec = {};
    for (const p of plans) { const y = p.inr.year; if (y != null && (from[p.module] == null || y < from[p.module])) from[p.module] = y; }
    return from;
  }, [plans]);
}

function Home({ info }: { info: Rec }) {
  const [pick, setPick] = useState(0);
  const from = useFrom();
  const m = MODULES[pick];
  return <>
    <section className="site-hero">
      <div className="site-hero-in">
        <div className="site-hero-copy">
          <h1>Tickets, devices, accounts, passwords and backups. One desk.</h1>
          <p className="lead">DeskAsk gives IT teams and managed service providers the six tools they open every day, built to work together: an alert opens a ticket, the ticket knows the device, the device knows its password.</p>
          <div className="site-hero-cta">
            <a className="site-btn big" href="#/store/trial">Start a {info.business?.trialDays ?? 14}-day free trial</a>
            <a className="site-btn ghost big" href="#/store/pricing">See prices</a>
          </div>
          <p className="site-hero-note">No card needed. In our cloud or on your own server. Data kept in India.</p>
        </div>
        <Journey />
      </div>
    </section>

    <section className="site-wrap modules" aria-labelledby="mods-h">
      <h2 id="mods-h">Buy only what you need</h2>
      <p className="section-lead">Each module is sold on its own and sized to your team. Add the next one when you are ready; four or more come 25% off.</p>
      <div className="mod-switch">
        <div className="mod-list" role="tablist" aria-label="Modules">
          {MODULES.map((x, n) => <button key={x.key} role="tab" id={'tab-' + x.key} aria-selected={n === pick} aria-controls="mod-panel" onClick={() => setPick(n)}
            onKeyDown={(e) => { if (e.key === 'ArrowDown' || e.key === 'ArrowRight') { e.preventDefault(); setPick((n + 1) % MODULES.length); document.getElementById('tab-' + MODULES[(n + 1) % MODULES.length].key)?.focus(); }
              if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') { e.preventDefault(); const p = (n + MODULES.length - 1) % MODULES.length; setPick(p); document.getElementById('tab-' + MODULES[p].key)?.focus(); } }}
            tabIndex={n === pick ? 0 : -1}>
            <span className="mod-name">{x.name}</span><span className="mod-line">{x.line}</span></button>)}
        </div>
        <div className="mod-panel" id="mod-panel" role="tabpanel" aria-labelledby={'tab-' + m.key}>
          <h3>{m.name}</h3>
          <p>{m.pitch}</p>
          <ul className="ticks">{m.features.slice(0, 4).map(([t, d]) => <li key={t}><b>{t}.</b> {d}</li>)}</ul>
          <div className="mod-foot">
            {from?.[m.key] != null && <p className="from">From <b>{money(from[m.key], 'INR')}</b> a year in our cloud, plus GST</p>}
            <a href={'#/store/products/' + m.slug}>All {m.name} features</a>
          </div>
        </div>
      </div>
    </section>

    <section className="flow" aria-labelledby="flow-h">
      <div className="site-wrap">
        <h2 id="flow-h">Why one desk beats six tools</h2>
        <ol className="flow-steps">
          <li><b>The agent finds the device.</b> Hardware, software, warranty and who uses it, without anyone typing it in.</li>
          <li><b>Monitoring watches it.</b> A full disk or a down uplink opens an alert, and the alert can open a ticket.</li>
          <li><b>The ticket has the context.</b> The device, its history and its passwords are one click away.</li>
          <li><b>The customer sees the result.</b> Updates by mail, a portal of their own, and a monthly report.</li>
        </ol>
      </div>
    </section>

    <section className="site-wrap run" aria-labelledby="run-h">
      <h2 id="run-h">Your cloud or ours</h2>
      <div className="run-cols">
        <div><h3>In our cloud</h3><p>Your own address, such as yourcompany.deskask.in. We install, update and back it up. Monthly or yearly billing, data stored in India.</p>
          <a href="#/store/pricing">SaaS prices</a></div>
        <div><h3>On your own server</h3><p>The same software on your Windows server with a licence key, about 20% less than SaaS, billed yearly. All data stays with you; updates install with one click.</p>
          <a href="#/store/pricing/onprem">On-prem prices</a></div>
      </div>
    </section>

    <section className="site-wrap faq-short" aria-labelledby="faq-h">
      <h2 id="faq-h">Questions people ask first</h2>
      <FaqList items={FAQ.slice(0, 4)} />
      <p><a href="#/store/faq">More questions</a></p>
    </section>

    <section className="closing">
      <div className="site-wrap">
        <h2>See it with your own devices and tickets</h2>
        <p>Your trial is ready in a minute and keeps everything you set up when you buy.</p>
        <div className="site-hero-cta"><a className="site-btn big light" href="#/store/trial">Start free trial</a><a className="site-btn ghost big light" href="#/store/contact">Talk to us</a></div>
      </div>
    </section>
  </>;
}

/* ------------------------------------------------------------------ products */

function Products() {
  const from = useFrom();
  return <section className="site-wrap page">
    <h1>Products</h1>
    <p className="section-lead">Six modules on one platform, each sold on its own.</p>
    <div className="prod-list">
      {MODULES.map((m) => <a key={m.key} className="prod-row" href={'#/store/products/' + m.slug}>
        <span className="prod-name">{m.name}</span><span className="prod-line">{m.line}</span>
        <span className="prod-from">{from?.[m.key] != null ? 'From ' + money(from[m.key], 'INR') + ' a year' : ''}</span></a>)}
    </div>
  </section>;
}

function Product({ slug }: { slug: string }) {
  const m: Module | undefined = MODULES.find((x) => x.slug === slug);
  const [plans, setPlans] = useState<Plan[]>([]);
  useEffect(() => { void call('/api/store/plans').then((p) => setPlans(p as unknown as Plan[]), () => undefined); }, []);
  if (!m) return <section className="site-wrap page"><h1>Page not found</h1><p><a href="#/store/products">See all products</a></p></section>;
  const sizes = plans.filter((p) => p.module === m.key);
  return <>
    <section className="site-wrap page prod">
      <p className="site-crumb"><a href="#/store/products">Products</a></p>
      <h1>{m.name}</h1>
      <p className="lead">{m.line}</p>
      <p className="prod-pitch">{m.pitch}</p>
      <div className="site-hero-cta"><a className="site-btn" href={'#/store/pricing/' + m.key}>Buy {m.name}</a><a className="site-btn ghost" href="#/store/trial">Try it free</a></div>
    </section>
    <section className="site-wrap feat" aria-labelledby="feat-h">
      <h2 id="feat-h">What it does</h2>
      <dl className="feat-grid">{m.features.map(([t, d]) => <div key={t}><dt>{t}</dt><dd>{d}</dd></div>)}</dl>
      <p className="fits">{m.fits}</p>
    </section>
    {sizes.length > 0 && <section className="site-wrap sizes" aria-labelledby="sizes-h">
      <h2 id="sizes-h">Sizes and prices</h2>
      <div className="md-table"><table>
        <thead><tr><th>Size</th><th className="num">Cloud, yearly</th><th className="num">Cloud, monthly</th><th className="num">On-prem, yearly</th></tr></thead>
        <tbody>{sizes.map((p) => <tr key={p.id}><th scope="row">{p.sizeLabel}</th><td className="num">{money(p.inr.year, 'INR')}</td><td className="num">{money(p.inr.month, 'INR')}</td><td className="num">{money(p.inr.onprem, 'INR')}</td></tr>)}</tbody>
      </table></div>
      <p className="muted small">Prices in rupees before GST. Outside India, prices are in US dollars on the pricing page.</p>
    </section>}
  </>;
}

/* ------------------------------------------------------------------ text pages */

function FaqList({ items }: { items: [string, string][] }) {
  return <div className="faq">{items.map(([q, a]) => <details key={q}><summary>{q}</summary><p>{a}</p></details>)}</div>;
}

function Faq() {
  return <section className="site-wrap page narrow"><h1>Frequently asked questions</h1><FaqList items={FAQ} />
    <p>Something else? <a href="#/store/contact">Contact us</a>.</p></section>;
}

function Contact({ b }: { b: Rec }) {
  return <section className="site-wrap page narrow contact">
    <h1>Contact us</h1>
    <p className="lead">Sales, support and billing questions all go to the same team.</p>
    <dl className="contact-list">
      {b.supportEmail && <div><dt>Email</dt><dd><a href={'mailto:' + b.supportEmail}>{b.supportEmail}</a></dd></div>}
      {b.supportPhone && <div><dt>Phone</dt><dd><a href={'tel:' + String(b.supportPhone).replace(/\s/g, '')}>{b.supportPhone}</a>, Monday to Friday, 9:30 am to 6:30 pm IST</dd></div>}
      {b.legalName && <div><dt>Office</dt><dd>{b.legalName}<br />{[b.address, b.city, b.state, b.pincode].filter(Boolean).join(', ')}, India</dd></div>}
      {(b.gstin || b.cin) && <div><dt>Registration</dt><dd>{b.gstin && <>GSTIN {b.gstin}<br /></>}{b.cin && <>CIN {b.cin}</>}</dd></div>}
    </dl>
    {b.grievanceName && <><h2>Grievance officer</h2>
      <p>If you are not satisfied with our answer, write to our grievance officer. We acknowledge every complaint within 48 hours and resolve it within one month.</p>
      <dl className="contact-list"><div><dt>Name</dt><dd>{b.grievanceName}{b.grievanceDesignation ? ', ' + b.grievanceDesignation : ''}</dd></div>
        <div><dt>Email</dt><dd><a href={'mailto:' + (b.grievanceEmail || b.supportEmail)}>{b.grievanceEmail || b.supportEmail}</a> (subject: Grievance)</dd></div></dl></>}
  </section>;
}

function TextPage({ slug }: { slug: string }) {
  const [p, setP] = useState<Rec | null>(null); const [err, setErr] = useState('');
  useEffect(() => { setP(null); void call('/api/store/pages/' + slug).then(setP, (e) => setErr((e as Error).message)); }, [slug]);
  return <section className="site-wrap page narrow text">
    {err ? <p className="err" role="alert">{err}</p> : !p ? <p className="muted">Loading…</p> : <><h1>{p.title}</h1><Markdown text={String(p.markdown ?? '')} /></>}
  </section>;
}

/* ------------------------------------------------------------------ pricing and checkout */

function Buy({ info, preset }: { info: Rec; preset?: string }) {
  const [plans, setPlans] = useState<Plan[]>([]);
  const [kind, setKind] = useState(preset === 'onprem' ? 'onprem' : 'saas'); const [period, setPeriod] = useState('year'); const [country, setCountry] = useState('IN'); const [stateCode, setState] = useState('33'); const [gstin, setGstin] = useState('');
  const [sel, setSel] = useState<Record<string, number>>({});
  const [quote, setQuote] = useState<Rec | null>(null); const [qErr, setQErr] = useState('');
  const [buyer, setBuyer] = useState({ company: '', name: '', email: '', phone: '', address: '', city: '', pincode: '' });
  const [tenantId, setTenantId] = useState(''); const [avail, setAvail] = useState<Rec | null>(null);
  const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  useEffect(() => { void call('/api/store/plans').then((p) => {
    const list = p as unknown as Plan[]; setPlans(list);
    const first = Array.isArray(list) ? list.find((x) => x.module === preset) : undefined;   // arriving from a product page: its smallest size is chosen
    if (first) setSel({ [first.module]: first.id });
  }, () => undefined); }, []); // eslint-disable-line react-hooks/exhaustive-deps
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
  const checkName = (v: string) => { setTenantId(v); setAvail(null); clearTimeout(timer.current); if (v.length >= 2) timer.current = setTimeout(() => { void call('/api/store/available?id=' + encodeURIComponent(v)).then(setAvail, (e) => setAvail({ available: false, reason: (e as Error).message || 'The address could not be checked. Try again.' })); }, 350); };
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
  return <section className="site-wrap page"><h1>Pricing</h1>
    <p className="section-lead">Pick a size for each module you need. Every feature is included in every size.</p>
    <div className="store-body">
    <section className="store-card">
      <h2>Choose your DeskAsk</h2>
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
      <p className="muted small">By paying you accept the <a href="#/store/terms">Terms of Service</a> and the <a href="#/store/refund">Cancellation and Refund Policy</a>.</p>
    </aside>
  </div></section>;
}

const TRIAL_MODULES: [string, string][] = [['servicedesk', 'Service Desk'], ['assets', 'Asset management'], ['monitoring', 'Monitoring'], ['adsuite', 'AD Manager'], ['vault', 'Vault'], ['backup', 'Backup']];

function Trial({ info }: { info: Rec }) {
  const [f, setF] = useState({ company: '', name: '', email: '', tenantId: '' }); const [mods, setMods] = useState<string[]>(['servicedesk', 'assets']);
  const [avail, setAvail] = useState<Rec | null>(null); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false); const [done, setDone] = useState<Rec | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>();
  const name = (v: string) => { setF({ ...f, tenantId: v }); setAvail(null); clearTimeout(timer.current); if (v.length >= 2) timer.current = setTimeout(() => { void call('/api/store/available?id=' + encodeURIComponent(v)).then(setAvail, (e) => setAvail({ available: false, reason: (e as Error).message || 'The address could not be checked. Try again.' })); }, 350); };
  const missing = [!f.company && 'company', !f.name && 'your name', !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(f.email) && 'a valid work email',
    !f.tenantId ? 'your DeskAsk address' : avail === null ? 'the address check (a moment)' : !avail.available && 'a free DeskAsk address',
    mods.length === 0 && 'at least one module'].filter(Boolean) as string[];
  const start = async () => { setBusy(true); setErr(''); try { setDone(await call('/api/store/trial', { ...f, modules: mods })); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } };
  if (done) return <div className="site-wrap page"><div className="store-body"><section className="store-card"><h1>Your trial is ready</h1>
    <p>We have emailed <b>{f.email}</b> a link to choose your password. Then sign in at <a href={done.address}>{done.address}</a>.</p>
    <p className="muted">The trial runs until {new Date(done.ends).toLocaleDateString()}. Buy a plan at any time to keep everything you set up.</p></section></div></div>;
  return <div className="site-wrap page"><div className="store-body"><section className="store-card">
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
    {info.trials ? <>
      <button className="primary" disabled={busy || missing.length > 0} onClick={() => void start()}>{busy ? 'Creating your DeskAsk…' : 'Start free trial'}</button>
      {!busy && missing.length > 0 && <p className="store-missing" role="status">Still needed: {missing.join(', ')}.</p>}
    </> : <p className="muted">Trials open soon.</p>}
  </section></div></div>;
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
  return <div className="site-wrap page"><div className="store-body"><section className="store-card">
    {err ? <p className="err">{err}</p> : !o ? <p className="muted">Checking your order…</p>
      : o.status === 'fulfilled' ? <>
        <h1>Thank you, your order is complete</h1>
        {o.kind === 'saas' ? <p>Your DeskAsk is at <a href={o.address}>{o.address}</a>. We have emailed you a link to choose your password (if you were on a trial, sign in as before).</p>
          : <><p>Your licence key (also emailed to you):</p><p className="store-key">{o.licenceKey ?? 'See your email.'}</p><p className="muted">In your DeskAsk, open Administration › Licence and type the key under Licence key.</p></>}
        {o.invoice && <p><a href={'/api/store/orders/' + encodeURIComponent(id) + '/invoice?t=' + encodeURIComponent(token)}>Download invoice {o.invoice}</a></p>}
      </> : o.status === 'failed' ? <><h1>Almost there</h1><p>{o.error}</p></>
      : o.status === 'pending' ? <><h1>Waiting for the payment</h1><p className="muted">If you have paid, this page updates in a moment.</p></>
      : <><h1>Setting up your order…</h1><p className="muted">This takes up to a minute.</p></>}
  </section></div></div>;
}
