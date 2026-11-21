import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useMemo, useRef, useState } from 'react';
import { Logo } from '../chrome';
import { FAQ, MODULES } from './content';
import { Markdown } from './Markdown';
import './site.css';
export const STATES = [['01', 'Jammu and Kashmir'], ['02', 'Himachal Pradesh'], ['03', 'Punjab'], ['04', 'Chandigarh'], ['05', 'Uttarakhand'], ['06', 'Haryana'], ['07', 'Delhi'], ['08', 'Rajasthan'],
    ['09', 'Uttar Pradesh'], ['10', 'Bihar'], ['11', 'Sikkim'], ['12', 'Arunachal Pradesh'], ['13', 'Nagaland'], ['14', 'Manipur'], ['15', 'Mizoram'], ['16', 'Tripura'], ['17', 'Meghalaya'], ['18', 'Assam'],
    ['19', 'West Bengal'], ['20', 'Jharkhand'], ['21', 'Odisha'], ['22', 'Chhattisgarh'], ['23', 'Madhya Pradesh'], ['24', 'Gujarat'], ['26', 'Dadra and Nagar Haveli and Daman and Diu'], ['27', 'Maharashtra'],
    ['29', 'Karnataka'], ['30', 'Goa'], ['31', 'Lakshadweep'], ['32', 'Kerala'], ['33', 'Tamil Nadu'], ['34', 'Puducherry'], ['35', 'Andaman and Nicobar Islands'], ['36', 'Telangana'], ['37', 'Andhra Pradesh'], ['38', 'Ladakh'], ['97', 'Other territory']];
const COUNTRIES = [['IN', 'India'], ['AE', 'United Arab Emirates'], ['AU', 'Australia'], ['BD', 'Bangladesh'], ['BH', 'Bahrain'], ['CA', 'Canada'], ['DE', 'Germany'], ['FR', 'France'], ['GB', 'United Kingdom'],
    ['IE', 'Ireland'], ['KE', 'Kenya'], ['KW', 'Kuwait'], ['LK', 'Sri Lanka'], ['MY', 'Malaysia'], ['NL', 'Netherlands'], ['NP', 'Nepal'], ['NZ', 'New Zealand'], ['OM', 'Oman'], ['QA', 'Qatar'], ['SA', 'Saudi Arabia'],
    ['SG', 'Singapore'], ['US', 'United States'], ['ZA', 'South Africa']];
async function call(url, body) {
    const r = await fetch(url, body === undefined ? undefined : { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
    const j = await r.json().catch(() => ({}));
    if (!r.ok)
        throw new Error(String(j.detail || j.error || 'Something went wrong (' + r.status + ').'));
    return j;
}
const money = (v, cur) => v == null ? '–' : (cur === 'INR' ? '₹' + v.toLocaleString('en-IN', { minimumFractionDigits: 0, maximumFractionDigits: 2 }) : '$' + v.toLocaleString('en-US', { maximumFractionDigits: 2 }));
const go = (h) => { location.hash = h; };
function loadCheckout() {
    if (window.Razorpay)
        return Promise.resolve();
    return new Promise((ok, fail) => { const s = document.createElement('script'); s.src = 'https://checkout.razorpay.com/v1/checkout.js'; s.onload = () => ok(); s.onerror = () => fail(new Error('The payment window could not be loaded.')); document.head.appendChild(s); });
}
const LEGAL = [['terms', 'Terms of Service'], ['privacy', 'Privacy Policy'], ['refund', 'Cancellation and refunds'], ['delivery', 'Delivery']];
/** The DeskAsk website on the vendor platform: home, product pages, pricing with checkout, free trial, FAQ, About, Contact and the policy pages. No sign-in. */
export default function StorePage({ path }) {
    const [info, setInfo] = useState(null);
    const [err, setErr] = useState('');
    const [menu, setMenu] = useState(false);
    // with the website linked and the platform store hidden, the store lives on the website
    useEffect(() => { void call('/api/store/info').then((i) => { if (typeof i.redirect === 'string' && i.redirect.startsWith('https://'))
        location.href = i.redirect;
    else
        setInfo(i); }, (e) => setErr(e.message)); }, []);
    const tab = path[0] ?? '';
    useEffect(() => { setMenu(false); if (!navigator.userAgent.includes('jsdom'))
        window.scrollTo?.(0, 0); }, [path.join('/')]); // eslint-disable-line react-hooks/exhaustive-deps
    const b = (info?.business ?? {});
    const legalHref = (slug) => { const ext = { terms: b.termsUrl, privacy: b.privacyUrl, refund: b.refundUrl, delivery: b.deliveryUrl }[slug]; return ext || '#/store/' + slug; };
    const nav = [['#/store/products', 'Products', tab === 'products'], ['#/store/pricing', 'Pricing', tab === 'pricing'], ['#/store/faq', 'FAQ', tab === 'faq'], ['#/store/about', 'About', tab === 'about'], ['#/store/contact', 'Contact', tab === 'contact']];
    let page;
    if (!info)
        page = err ? _jsx("section", { className: "site-wrap", children: _jsx("p", { className: "err", role: "alert", children: err }) }) : _jsx("section", { className: "site-wrap", children: _jsx("p", { className: "muted", children: "Loading\u2026" }) });
    else if (tab === 'order')
        page = _jsx(OrderPage, { id: path[1] ?? '', token: path[2] ?? '' });
    else if (tab === 'trial')
        page = _jsx(Trial, { info: info });
    else if (tab === 'pricing')
        page = _jsx(Buy, { info: info, preset: path[1] });
    else if (tab === 'products')
        page = path[1] ? _jsx(Product, { slug: path[1] }) : _jsx(Products, {});
    else if (tab === 'faq')
        page = _jsx(Faq, {});
    else if (tab === 'contact')
        page = _jsx(Contact, { b: b });
    else if (['about', 'terms', 'privacy', 'refund', 'delivery'].includes(tab))
        page = _jsx(TextPage, { slug: tab });
    else
        page = _jsx(Home, { info: info });
    return _jsxs("div", { className: "site", children: [_jsx("a", { className: "site-skip", href: "#site-main", onClick: (e) => { e.preventDefault(); document.getElementById('site-main')?.focus(); }, children: "Skip to content" }), _jsx("header", { className: "site-head", children: _jsxs("div", { className: "site-head-in", children: [_jsxs("a", { href: "#/store", className: "site-brand", "aria-label": "DeskAsk home", children: [_jsx(Logo, { size: 30 }), _jsx("span", { children: b.tradeName || 'DeskAsk' })] }), _jsx("button", { className: "site-menu-btn", "aria-expanded": menu, "aria-controls": "site-nav", onClick: () => setMenu(!menu), children: "Menu" }), _jsxs("nav", { id: "site-nav", className: menu ? 'open' : '', "aria-label": "Main", children: [nav.map(([h, l, on]) => _jsx("a", { href: h, "aria-current": on ? 'page' : undefined, children: l }, h)), _jsx("a", { href: "#/store/trial", className: "site-btn ghost", children: "Free trial" }), _jsx("a", { href: "#/store/pricing", className: "site-btn", children: "Buy" })] })] }) }), _jsx("main", { id: "site-main", tabIndex: -1, children: page }), _jsxs("footer", { className: "site-foot", children: [_jsxs("div", { className: "site-foot-in", children: [_jsxs("div", { className: "site-foot-brand", children: [_jsxs("a", { href: "#/store", className: "site-brand", children: [_jsx(Logo, { size: 26 }), _jsx("span", { children: b.tradeName || 'DeskAsk' })] }), _jsx("p", { children: "IT management software made in Chennai." })] }), _jsxs("div", { children: [_jsx("h2", { children: "Products" }), MODULES.map((m) => _jsx("a", { href: '#/store/products/' + m.slug, children: m.name }, m.key))] }), _jsxs("div", { children: [_jsx("h2", { children: "Company" }), _jsx("a", { href: "#/store/about", children: "About us" }), _jsx("a", { href: "#/store/contact", children: "Contact us" }), _jsx("a", { href: "#/store/pricing", children: "Pricing" }), _jsx("a", { href: "#/store/faq", children: "FAQ" })] }), _jsxs("div", { children: [_jsx("h2", { children: "Policies" }), LEGAL.map(([s, l]) => _jsx("a", { href: legalHref(s), children: l }, s))] })] }), _jsxs("div", { className: "site-foot-legal", children: [b.legalName && _jsxs("p", { children: [_jsx("b", { children: b.legalName }), ", ", [b.address, b.city, b.state, b.pincode].filter(Boolean).join(', ')] }), _jsx("p", { children: [b.gstin && 'GSTIN ' + b.gstin, b.cin && 'CIN ' + b.cin].filter(Boolean).join(', ') }), b.supportEmail && _jsxs("p", { children: ["Support: ", _jsx("a", { href: 'mailto:' + b.supportEmail, children: b.supportEmail }), b.supportPhone ? ', ' + b.supportPhone : '', b.grievanceName ? '. Grievance officer: ' + b.grievanceName + (b.grievanceDesignation ? ', ' + b.grievanceDesignation : '') : ''] })] })] })] });
}
/* ------------------------------------------------------------------ home */
/** The hero's picture: one real problem travelling through DeskAsk, from alert to fix. It plays once; reduced motion shows it finished. */
function Journey() {
    const steps = [
        ['alert', 'Monitoring', 'SRV-DB01 disk C: at 94%', '09:12'],
        ['ticket', 'Service Desk', 'INC-0142 opened, priority P2', '09:12'],
        ['asset', 'Asset management', 'Linked to AST-000318, Dell R640, warranty to 2027', '09:13'],
        ['vault', 'Vault', 'Engineer opened the server login (logged)', '09:21'],
        ['done', 'Resolved', 'Old logs archived, disk at 61%. Customer mailed.', '09:30'],
    ];
    return _jsxs("figure", { className: "journey", "aria-label": "How one problem moves through DeskAsk", children: [_jsxs("figcaption", { children: [_jsx("span", { className: "journey-dot", "aria-hidden": "true" }), "Example: one full disk, start to finish"] }), _jsx("ol", { children: steps.map(([k, mod, what, at], n) => _jsxs("li", { className: 'j-' + k, style: { ['--d']: n }, children: [_jsx("span", { className: "j-time", children: at }), _jsx("span", { className: "j-mod", children: mod }), _jsx("span", { className: "j-what", children: what })] }, k)) }), _jsx("p", { className: "journey-sum", children: "18 minutes from alert to fixed. Nobody copied a serial number or hunted for a password." })] });
}
function useFrom() {
    const [plans, setPlans] = useState(null);
    useEffect(() => { void call('/api/store/plans').then((p) => setPlans(p), () => setPlans([])); }, []);
    return useMemo(() => {
        if (!plans)
            return null;
        const from = {};
        for (const p of plans) {
            const y = p.inr.year;
            if (y != null && (from[p.module] == null || y < from[p.module]))
                from[p.module] = y;
        }
        return from;
    }, [plans]);
}
function Home({ info }) {
    const [pick, setPick] = useState(0);
    const from = useFrom();
    const m = MODULES[pick];
    return _jsxs(_Fragment, { children: [_jsx("section", { className: "site-hero", children: _jsxs("div", { className: "site-hero-in", children: [_jsxs("div", { className: "site-hero-copy", children: [_jsx("h1", { children: "Tickets, devices, accounts, passwords and backups. One desk." }), _jsx("p", { className: "lead", children: "DeskAsk gives IT teams and managed service providers the six tools they open every day, built to work together: an alert opens a ticket, the ticket knows the device, the device knows its password." }), _jsxs("div", { className: "site-hero-cta", children: [_jsxs("a", { className: "site-btn big", href: "#/store/trial", children: ["Start a ", info.business?.trialDays ?? 14, "-day free trial"] }), _jsx("a", { className: "site-btn ghost big", href: "#/store/pricing", children: "See prices" })] }), _jsx("p", { className: "site-hero-note", children: "No card needed. In our cloud or on your own server. Data kept in India." })] }), _jsx(Journey, {})] }) }), _jsxs("section", { className: "site-wrap modules", "aria-labelledby": "mods-h", children: [_jsx("h2", { id: "mods-h", children: "Buy only what you need" }), _jsx("p", { className: "section-lead", children: "Each module is sold on its own and sized to your team. Add the next one when you are ready; four or more come 25% off." }), _jsxs("div", { className: "mod-switch", children: [_jsx("div", { className: "mod-list", role: "tablist", "aria-label": "Modules", children: MODULES.map((x, n) => _jsxs("button", { role: "tab", id: 'tab-' + x.key, "aria-selected": n === pick, "aria-controls": "mod-panel", onClick: () => setPick(n), onKeyDown: (e) => {
                                        if (e.key === 'ArrowDown' || e.key === 'ArrowRight') {
                                            e.preventDefault();
                                            setPick((n + 1) % MODULES.length);
                                            document.getElementById('tab-' + MODULES[(n + 1) % MODULES.length].key)?.focus();
                                        }
                                        if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') {
                                            e.preventDefault();
                                            const p = (n + MODULES.length - 1) % MODULES.length;
                                            setPick(p);
                                            document.getElementById('tab-' + MODULES[p].key)?.focus();
                                        }
                                    }, tabIndex: n === pick ? 0 : -1, children: [_jsx("span", { className: "mod-name", children: x.name }), _jsx("span", { className: "mod-line", children: x.line })] }, x.key)) }), _jsxs("div", { className: "mod-panel", id: "mod-panel", role: "tabpanel", "aria-labelledby": 'tab-' + m.key, children: [_jsx("h3", { children: m.name }), _jsx("p", { children: m.pitch }), _jsx("ul", { className: "ticks", children: m.features.slice(0, 4).map(([t, d]) => _jsxs("li", { children: [_jsxs("b", { children: [t, "."] }), " ", d] }, t)) }), _jsxs("div", { className: "mod-foot", children: [from?.[m.key] != null && _jsxs("p", { className: "from", children: ["From ", _jsx("b", { children: money(from[m.key], 'INR') }), " a year in our cloud, plus GST"] }), _jsxs("a", { href: '#/store/products/' + m.slug, children: ["All ", m.name, " features"] })] })] })] })] }), _jsx("section", { className: "flow", "aria-labelledby": "flow-h", children: _jsxs("div", { className: "site-wrap", children: [_jsx("h2", { id: "flow-h", children: "Why one desk beats six tools" }), _jsxs("ol", { className: "flow-steps", children: [_jsxs("li", { children: [_jsx("b", { children: "The agent finds the device." }), " Hardware, software, warranty and who uses it, without anyone typing it in."] }), _jsxs("li", { children: [_jsx("b", { children: "Monitoring watches it." }), " A full disk or a down uplink opens an alert, and the alert can open a ticket."] }), _jsxs("li", { children: [_jsx("b", { children: "The ticket has the context." }), " The device, its history and its passwords are one click away."] }), _jsxs("li", { children: [_jsx("b", { children: "The customer sees the result." }), " Updates by mail, a portal of their own, and a monthly report."] })] })] }) }), _jsxs("section", { className: "site-wrap run", "aria-labelledby": "run-h", children: [_jsx("h2", { id: "run-h", children: "Your cloud or ours" }), _jsxs("div", { className: "run-cols", children: [_jsxs("div", { children: [_jsx("h3", { children: "In our cloud" }), _jsx("p", { children: "Your own address, such as yourcompany.deskask.in. We install, update and back it up. Monthly or yearly billing, data stored in India." }), _jsx("a", { href: "#/store/pricing", children: "SaaS prices" })] }), _jsxs("div", { children: [_jsx("h3", { children: "On your own server" }), _jsx("p", { children: "The same software on your Windows server with a licence key, about 20% less than SaaS, billed yearly. All data stays with you; updates install with one click." }), _jsx("a", { href: "#/store/pricing/onprem", children: "On-prem prices" })] })] })] }), _jsxs("section", { className: "site-wrap faq-short", "aria-labelledby": "faq-h", children: [_jsx("h2", { id: "faq-h", children: "Questions people ask first" }), _jsx(FaqList, { items: FAQ.slice(0, 4) }), _jsx("p", { children: _jsx("a", { href: "#/store/faq", children: "More questions" }) })] }), _jsx("section", { className: "closing", children: _jsxs("div", { className: "site-wrap", children: [_jsx("h2", { children: "See it with your own devices and tickets" }), _jsx("p", { children: "Your trial is ready in a minute and keeps everything you set up when you buy." }), _jsxs("div", { className: "site-hero-cta", children: [_jsx("a", { className: "site-btn big light", href: "#/store/trial", children: "Start free trial" }), _jsx("a", { className: "site-btn ghost big light", href: "#/store/contact", children: "Talk to us" })] })] }) })] });
}
/* ------------------------------------------------------------------ products */
function Products() {
    const from = useFrom();
    return _jsxs("section", { className: "site-wrap page", children: [_jsx("h1", { children: "Products" }), _jsx("p", { className: "section-lead", children: "Six modules on one platform, each sold on its own." }), _jsx("div", { className: "prod-list", children: MODULES.map((m) => _jsxs("a", { className: "prod-row", href: '#/store/products/' + m.slug, children: [_jsx("span", { className: "prod-name", children: m.name }), _jsx("span", { className: "prod-line", children: m.line }), _jsx("span", { className: "prod-from", children: from?.[m.key] != null ? 'From ' + money(from[m.key], 'INR') + ' a year' : '' })] }, m.key)) })] });
}
function Product({ slug }) {
    const m = MODULES.find((x) => x.slug === slug);
    const [plans, setPlans] = useState([]);
    useEffect(() => { void call('/api/store/plans').then((p) => setPlans(p), () => undefined); }, []);
    if (!m)
        return _jsxs("section", { className: "site-wrap page", children: [_jsx("h1", { children: "Page not found" }), _jsx("p", { children: _jsx("a", { href: "#/store/products", children: "See all products" }) })] });
    const sizes = plans.filter((p) => p.module === m.key);
    return _jsxs(_Fragment, { children: [_jsxs("section", { className: "site-wrap page prod", children: [_jsx("p", { className: "site-crumb", children: _jsx("a", { href: "#/store/products", children: "Products" }) }), _jsx("h1", { children: m.name }), _jsx("p", { className: "lead", children: m.line }), _jsx("p", { className: "prod-pitch", children: m.pitch }), _jsxs("div", { className: "site-hero-cta", children: [_jsxs("a", { className: "site-btn", href: '#/store/pricing/' + m.key, children: ["Buy ", m.name] }), _jsx("a", { className: "site-btn ghost", href: "#/store/trial", children: "Try it free" })] })] }), _jsxs("section", { className: "site-wrap feat", "aria-labelledby": "feat-h", children: [_jsx("h2", { id: "feat-h", children: "What it does" }), _jsx("dl", { className: "feat-grid", children: m.features.map(([t, d]) => _jsxs("div", { children: [_jsx("dt", { children: t }), _jsx("dd", { children: d })] }, t)) }), _jsx("p", { className: "fits", children: m.fits })] }), sizes.length > 0 && _jsxs("section", { className: "site-wrap sizes", "aria-labelledby": "sizes-h", children: [_jsx("h2", { id: "sizes-h", children: "Sizes and prices" }), _jsx("div", { className: "md-table", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Size" }), _jsx("th", { className: "num", children: "Cloud, yearly" }), _jsx("th", { className: "num", children: "Cloud, monthly" }), _jsx("th", { className: "num", children: "On-prem, yearly" })] }) }), _jsx("tbody", { children: sizes.map((p) => _jsxs("tr", { children: [_jsx("th", { scope: "row", children: p.sizeLabel }), _jsx("td", { className: "num", children: money(p.inr.year, 'INR') }), _jsx("td", { className: "num", children: money(p.inr.month, 'INR') }), _jsx("td", { className: "num", children: money(p.inr.onprem, 'INR') })] }, p.id)) })] }) }), _jsx("p", { className: "muted small", children: "Prices in rupees before GST. Outside India, prices are in US dollars on the pricing page." })] })] });
}
/* ------------------------------------------------------------------ text pages */
function FaqList({ items }) {
    return _jsx("div", { className: "faq", children: items.map(([q, a]) => _jsxs("details", { children: [_jsx("summary", { children: q }), _jsx("p", { children: a })] }, q)) });
}
function Faq() {
    return _jsxs("section", { className: "site-wrap page narrow", children: [_jsx("h1", { children: "Frequently asked questions" }), _jsx(FaqList, { items: FAQ }), _jsxs("p", { children: ["Something else? ", _jsx("a", { href: "#/store/contact", children: "Contact us" }), "."] })] });
}
function Contact({ b }) {
    return _jsxs("section", { className: "site-wrap page narrow contact", children: [_jsx("h1", { children: "Contact us" }), _jsx("p", { className: "lead", children: "Sales, support and billing questions all go to the same team." }), _jsxs("dl", { className: "contact-list", children: [b.supportEmail && _jsxs("div", { children: [_jsx("dt", { children: "Email" }), _jsx("dd", { children: _jsx("a", { href: 'mailto:' + b.supportEmail, children: b.supportEmail }) })] }), b.supportPhone && _jsxs("div", { children: [_jsx("dt", { children: "Phone" }), _jsxs("dd", { children: [_jsx("a", { href: 'tel:' + String(b.supportPhone).replace(/\s/g, ''), children: b.supportPhone }), ", Monday to Friday, 9:30 am to 6:30 pm IST"] })] }), b.legalName && _jsxs("div", { children: [_jsx("dt", { children: "Office" }), _jsxs("dd", { children: [b.legalName, _jsx("br", {}), [b.address, b.city, b.state, b.pincode].filter(Boolean).join(', '), ", India"] })] }), (b.gstin || b.cin) && _jsxs("div", { children: [_jsx("dt", { children: "Registration" }), _jsxs("dd", { children: [b.gstin && _jsxs(_Fragment, { children: ["GSTIN ", b.gstin, _jsx("br", {})] }), b.cin && _jsxs(_Fragment, { children: ["CIN ", b.cin] })] })] })] }), b.grievanceName && _jsxs(_Fragment, { children: [_jsx("h2", { children: "Grievance officer" }), _jsx("p", { children: "If you are not satisfied with our answer, write to our grievance officer. We acknowledge every complaint within 48 hours and resolve it within one month." }), _jsxs("dl", { className: "contact-list", children: [_jsxs("div", { children: [_jsx("dt", { children: "Name" }), _jsxs("dd", { children: [b.grievanceName, b.grievanceDesignation ? ', ' + b.grievanceDesignation : ''] })] }), _jsxs("div", { children: [_jsx("dt", { children: "Email" }), _jsxs("dd", { children: [_jsx("a", { href: 'mailto:' + (b.grievanceEmail || b.supportEmail), children: b.grievanceEmail || b.supportEmail }), " (subject: Grievance)"] })] })] })] })] });
}
function TextPage({ slug }) {
    const [p, setP] = useState(null);
    const [err, setErr] = useState('');
    useEffect(() => { setP(null); void call('/api/store/pages/' + slug).then(setP, (e) => setErr(e.message)); }, [slug]);
    return _jsx("section", { className: "site-wrap page narrow text", children: err ? _jsx("p", { className: "err", role: "alert", children: err }) : !p ? _jsx("p", { className: "muted", children: "Loading\u2026" }) : _jsxs(_Fragment, { children: [_jsx("h1", { children: p.title }), _jsx(Markdown, { text: String(p.markdown ?? '') })] }) });
}
/* ------------------------------------------------------------------ pricing and checkout */
function Buy({ info, preset }) {
    const [plans, setPlans] = useState([]);
    const [kind, setKind] = useState(preset === 'onprem' ? 'onprem' : 'saas');
    const [period, setPeriod] = useState('year');
    const [country, setCountry] = useState('IN');
    const [stateCode, setState] = useState('33');
    const [gstin, setGstin] = useState('');
    const [sel, setSel] = useState({});
    const [quote, setQuote] = useState(null);
    const [qErr, setQErr] = useState('');
    const [buyer, setBuyer] = useState({ company: '', name: '', email: '', phone: '', address: '', city: '', pincode: '' });
    const [tenantId, setTenantId] = useState('');
    const [avail, setAvail] = useState(null);
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    useEffect(() => {
        void call('/api/store/plans').then((p) => {
            const list = p;
            setPlans(list);
            const first = Array.isArray(list) ? list.find((x) => x.module === preset) : undefined; // arriving from a product page: its smallest size is chosen
            if (first)
                setSel({ [first.module]: first.id });
        }, () => undefined);
    }, []); // eslint-disable-line react-hooks/exhaustive-deps
    const cur = country === 'IN' ? 'INR' : 'USD';
    const modules = useMemo(() => { const m = {}; for (const p of plans)
        (m[p.module] ??= []).push(p); return m; }, [plans]);
    const price = (p) => { const x = cur === 'INR' ? p.inr : p.usd; return kind === 'onprem' ? x.onprem : period === 'year' ? x.year : x.month; };
    useEffect(() => { if (kind === 'onprem')
        setPeriod('year'); }, [kind]);
    const req = { kind, period, plans: sel, country, stateCode, gstin: gstin.trim().toUpperCase() };
    const reqKey = JSON.stringify(req);
    useEffect(() => {
        if (Object.keys(sel).length === 0) {
            setQuote(null);
            setQErr('');
            return;
        }
        const t = setTimeout(() => { void call('/api/store/quote', req).then((q) => { setQuote(q); setQErr(''); }, (e) => { setQuote(null); setQErr(e.message); }); }, 250);
        return () => clearTimeout(t);
    }, [reqKey]); // eslint-disable-line react-hooks/exhaustive-deps
    const timer = useRef();
    const checkName = (v) => { setTenantId(v); setAvail(null); clearTimeout(timer.current); if (v.length >= 2)
        timer.current = setTimeout(() => { void call('/api/store/available?id=' + encodeURIComponent(v)).then(setAvail, (e) => setAvail({ available: false, reason: e.message || 'The address could not be checked. Try again.' })); }, 350); };
    const pay = async () => {
        setBusy(true);
        setErr('');
        try {
            const stateName = STATES.find(([c]) => c === stateCode)?.[1] ?? '';
            const r = await call('/api/store/checkout', { ...req, tenantId, buyer: { ...buyer, stateName: country === 'IN' ? stateName : '' } });
            await loadCheckout();
            const rz = new window.Razorpay({
                ...r.razorpay, theme: { color: '#0f766e' },
                handler: async (resp) => {
                    try {
                        await call('/api/store/checkout/confirm', resp);
                    }
                    catch { /* the webhook completes it */ }
                    go('#/store/order/' + r.orderId + '/' + r.access);
                },
                modal: { ondismiss: () => setBusy(false) },
            });
            rz.on?.('payment.failed', (f) => setErr('The payment did not go through: ' + (f?.error?.description ?? 'please try again.')));
            rz.open();
        }
        catch (e) {
            setErr(e.message);
            setBusy(false);
        }
    };
    const ready = !!quote && buyer.company && buyer.name && buyer.email && buyer.address && (kind === 'onprem' || avail?.available);
    return _jsxs("section", { className: "site-wrap page", children: [_jsx("h1", { children: "Pricing" }), _jsx("p", { className: "section-lead", children: "Pick a size for each module you need. Every feature is included in every size." }), _jsxs("div", { className: "store-body", children: [_jsxs("section", { className: "store-card", children: [_jsx("h2", { children: "Choose your DeskAsk" }), _jsxs("p", { className: "muted", children: ["One edition per module, every feature included. Pick the size you need; prices ", cur === 'INR' ? 'in rupees, GST added at checkout' : 'in US dollars', "."] }), _jsxs("div", { className: "store-row", children: [_jsxs("label", { children: ["How you run it", _jsxs("select", { value: kind, onChange: (e) => setKind(e.target.value), children: [_jsx("option", { value: "saas", children: "In our cloud (SaaS)" }), _jsx("option", { value: "onprem", children: "On your own server (on-prem)" })] })] }), _jsxs("label", { children: ["Billing", _jsxs("select", { value: period, onChange: (e) => setPeriod(e.target.value), disabled: kind === 'onprem', children: [_jsx("option", { value: "year", children: "Yearly" }), _jsx("option", { value: "month", children: "Monthly" })] })] }), _jsxs("label", { children: ["Country", _jsx("select", { value: country, onChange: (e) => setCountry(e.target.value), children: COUNTRIES.map(([c, n]) => _jsx("option", { value: c, children: n }, c)) })] }), country === 'IN' && _jsxs("label", { children: ["State", _jsx("select", { value: stateCode, onChange: (e) => setState(e.target.value), children: STATES.map(([c, n]) => _jsx("option", { value: c, children: n }, c)) })] })] }), _jsxs("table", { className: "store-plans", children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Module" }), _jsx("th", { children: "Size" }), _jsx("th", { children: "Price" })] }) }), _jsx("tbody", { children: Object.entries(modules).map(([m, list]) => _jsxs("tr", { children: [_jsx("td", { children: _jsx("b", { children: list[0].moduleName }) }), _jsx("td", { children: _jsxs("select", { "aria-label": list[0].moduleName + ' size', value: sel[m] ?? '', onChange: (e) => { const v = e.target.value; const n = { ...sel }; if (v)
                                                            n[m] = Number(v);
                                                        else
                                                            delete n[m]; setSel(n); }, children: [_jsx("option", { value: "", children: "Not needed" }), list.map((p) => _jsxs("option", { value: p.id, children: [p.sizeLabel, " \u00B7 ", money(price(p), cur)] }, p.id))] }) }), _jsx("td", { children: sel[m] ? money(price(list.find((p) => p.id === sel[m])), cur) : '' })] }, m)) })] }), _jsx("p", { className: "muted small", children: "4 or more modules: 25% off. Monitoring can be bought on its own; Asset management adds warranty, contracts, software and reports." }), qErr && _jsx("p", { className: "err", role: "alert", children: qErr })] }), _jsxs("section", { className: "store-card", children: [_jsx("h2", { children: "Your details" }), _jsxs("div", { className: "store-row", children: [_jsxs("label", { children: ["Company", _jsx("input", { value: buyer.company, onChange: (e) => setBuyer({ ...buyer, company: e.target.value }), autoComplete: "organization" })] }), _jsxs("label", { children: ["Your name", _jsx("input", { value: buyer.name, onChange: (e) => setBuyer({ ...buyer, name: e.target.value }), autoComplete: "name" })] }), _jsxs("label", { children: ["Work email", _jsx("input", { type: "email", value: buyer.email, onChange: (e) => setBuyer({ ...buyer, email: e.target.value }), autoComplete: "email" })] }), _jsxs("label", { children: ["Phone", _jsx("input", { value: buyer.phone, onChange: (e) => setBuyer({ ...buyer, phone: e.target.value }), autoComplete: "tel" })] })] }), _jsxs("label", { children: ["Billing address", _jsx("input", { value: buyer.address, onChange: (e) => setBuyer({ ...buyer, address: e.target.value }), autoComplete: "street-address" })] }), _jsxs("div", { className: "store-row", children: [_jsxs("label", { children: ["City", _jsx("input", { value: buyer.city, onChange: (e) => setBuyer({ ...buyer, city: e.target.value }), autoComplete: "address-level2" })] }), _jsxs("label", { children: ["PIN / postcode", _jsx("input", { value: buyer.pincode, onChange: (e) => setBuyer({ ...buyer, pincode: e.target.value }), autoComplete: "postal-code" })] }), country === 'IN' && _jsxs("label", { children: ["GSTIN (optional)", _jsx("input", { value: gstin, onChange: (e) => setGstin(e.target.value.toUpperCase()), maxLength: 15 })] })] }), kind === 'saas' && _jsxs("label", { children: ["Your DeskAsk address", _jsxs("span", { className: "store-addr", children: [_jsx("input", { value: tenantId, onChange: (e) => checkName(e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, '')), placeholder: "yourcompany", "aria-label": "Your DeskAsk address" }), _jsxs("span", { children: [".", info.business?.tenantDomain ?? 'deskask.in'] })] }), avail && _jsx("small", { className: avail.available ? 'ok' : 'err', children: avail.available ? avail.address + ' is free' : avail.reason })] })] }), _jsxs("aside", { className: "store-card store-sum", children: [_jsx("h2", { children: "Summary" }), !quote ? _jsx("p", { className: "muted", children: "Choose at least one module." }) : _jsxs(_Fragment, { children: [_jsx("table", { children: _jsxs("tbody", { children: [quote.lines.map((l) => _jsxs("tr", { children: [_jsx("td", { children: l.description }), _jsx("td", { className: "num", children: money(l.amount, quote.currency) })] }, l.module)), quote.discount > 0 && _jsxs("tr", { children: [_jsx("td", { children: "Suite discount (25%)" }), _jsxs("td", { className: "num", children: ["\u2212", money(quote.discount, quote.currency)] })] }), quote.taxes.map((t) => _jsxs("tr", { children: [_jsxs("td", { children: [t.name, " ", t.rate, "%"] }), _jsx("td", { className: "num", children: money(t.amount, quote.currency) })] }, t.name)), _jsxs("tr", { className: "total", children: [_jsx("td", { children: "Total" }), _jsx("td", { className: "num", children: money(quote.total, quote.currency) })] })] }) }), quote.taxNote && _jsx("p", { className: "muted small", children: quote.taxNote })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), info.open ? _jsx("button", { className: "primary", disabled: !ready || busy, onClick: () => void pay(), children: busy ? 'Opening payment…' : 'Pay securely' })
                                : _jsxs("p", { className: "muted", children: ["Online payment opens soon. Write to ", info.business?.supportEmail || 'us', " for a quote."] }), info.payments === 'test' && _jsx("p", { className: "muted small", children: "Test mode: no real money is taken." }), _jsxs("p", { className: "muted small", children: ["By paying you accept the ", _jsx("a", { href: "#/store/terms", children: "Terms of Service" }), " and the ", _jsx("a", { href: "#/store/refund", children: "Cancellation and Refund Policy" }), "."] })] })] })] });
}
const TRIAL_MODULES = [['servicedesk', 'Service Desk'], ['assets', 'Asset management'], ['monitoring', 'Monitoring'], ['adsuite', 'AD Manager'], ['vault', 'Vault'], ['backup', 'Backup']];
function Trial({ info }) {
    const [f, setF] = useState({ company: '', name: '', email: '', tenantId: '' });
    const [mods, setMods] = useState(['servicedesk', 'assets']);
    const [avail, setAvail] = useState(null);
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const [done, setDone] = useState(null);
    const timer = useRef();
    const name = (v) => { setF({ ...f, tenantId: v }); setAvail(null); clearTimeout(timer.current); if (v.length >= 2)
        timer.current = setTimeout(() => { void call('/api/store/available?id=' + encodeURIComponent(v)).then(setAvail, (e) => setAvail({ available: false, reason: e.message || 'The address could not be checked. Try again.' })); }, 350); };
    const missing = [!f.company && 'company', !f.name && 'your name', !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(f.email) && 'a valid work email',
        !f.tenantId ? 'your DeskAsk address' : avail === null ? 'the address check (a moment)' : !avail.available && 'a free DeskAsk address',
        mods.length === 0 && 'at least one module'].filter(Boolean);
    const start = async () => { setBusy(true); setErr(''); try {
        setDone(await call('/api/store/trial', { ...f, modules: mods }));
    }
    catch (e) {
        setErr(e.message);
    }
    finally {
        setBusy(false);
    } };
    if (done)
        return _jsx("div", { className: "site-wrap page", children: _jsx("div", { className: "store-body", children: _jsxs("section", { className: "store-card", children: [_jsx("h1", { children: "Your trial is ready" }), _jsxs("p", { children: ["We have emailed ", _jsx("b", { children: f.email }), " a link to choose your password. Then sign in at ", _jsx("a", { href: done.address, children: done.address }), "."] }), _jsxs("p", { className: "muted", children: ["The trial runs until ", new Date(done.ends).toLocaleDateString(), ". Buy a plan at any time to keep everything you set up."] })] }) }) });
    return _jsx("div", { className: "site-wrap page", children: _jsx("div", { className: "store-body", children: _jsxs("section", { className: "store-card", children: [_jsxs("h1", { children: ["Try DeskAsk free for ", info.business?.trialDays ?? 14, " days"] }), _jsx("p", { className: "muted", children: "No card needed. Your own DeskAsk is ready in a minute." }), _jsxs("div", { className: "store-row", children: [_jsxs("label", { children: ["Company", _jsx("input", { value: f.company, onChange: (e) => setF({ ...f, company: e.target.value }) })] }), _jsxs("label", { children: ["Your name", _jsx("input", { value: f.name, onChange: (e) => setF({ ...f, name: e.target.value }) })] }), _jsxs("label", { children: ["Work email", _jsx("input", { type: "email", value: f.email, onChange: (e) => setF({ ...f, email: e.target.value }) })] })] }), _jsxs("label", { children: ["Your DeskAsk address", _jsxs("span", { className: "store-addr", children: [_jsx("input", { value: f.tenantId, onChange: (e) => name(e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, '')), placeholder: "yourcompany", "aria-label": "Trial address" }), _jsxs("span", { children: [".", info.business?.tenantDomain ?? 'deskask.in'] })] }), avail && _jsx("small", { className: avail.available ? 'ok' : 'err', children: avail.available ? avail.address + ' is free' : avail.reason })] }), _jsxs("fieldset", { className: "store-mods", children: [_jsx("legend", { children: "Modules to try" }), TRIAL_MODULES.map(([k, n]) => _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: mods.includes(k), onChange: (e) => setMods(e.target.checked ? [...mods, k] : mods.filter((x) => x !== k)) }), n] }, k))] }), err && _jsx("p", { className: "err", role: "alert", children: err }), info.trials ? _jsxs(_Fragment, { children: [_jsx("button", { className: "primary", disabled: busy || missing.length > 0, onClick: () => void start(), children: busy ? 'Creating your DeskAsk…' : 'Start free trial' }), !busy && missing.length > 0 && _jsxs("p", { className: "store-missing", role: "status", children: ["Still needed: ", missing.join(', '), "."] })] }) : _jsx("p", { className: "muted", children: "Trials open soon." })] }) }) });
}
function OrderPage({ id, token }) {
    const [o, setO] = useState(null);
    const [err, setErr] = useState('');
    useEffect(() => {
        let stop = false;
        let n = 0;
        const tick = async () => {
            try {
                const r = await call('/api/store/orders/' + encodeURIComponent(id) + '?t=' + encodeURIComponent(token));
                if (stop)
                    return;
                setO(r);
                if (r.status === 'fulfilled' || r.status === 'failed')
                    return;
            }
            catch (e) {
                if (!stop)
                    setErr(e.message);
                return;
            }
            if (++n < 100)
                setTimeout(() => void tick(), 3000);
        };
        void tick();
        return () => { stop = true; };
    }, [id, token]);
    return _jsx("div", { className: "site-wrap page", children: _jsx("div", { className: "store-body", children: _jsx("section", { className: "store-card", children: err ? _jsx("p", { className: "err", children: err }) : !o ? _jsx("p", { className: "muted", children: "Checking your order\u2026" })
                    : o.status === 'fulfilled' ? _jsxs(_Fragment, { children: [_jsx("h1", { children: "Thank you, your order is complete" }), o.kind === 'saas' ? _jsxs("p", { children: ["Your DeskAsk is at ", _jsx("a", { href: o.address, children: o.address }), ". We have emailed you a link to choose your password (if you were on a trial, sign in as before)."] })
                                : _jsxs(_Fragment, { children: [_jsx("p", { children: "Your licence key (also emailed to you):" }), _jsx("p", { className: "store-key", children: o.licenceKey ?? 'See your email.' }), _jsx("p", { className: "muted", children: "In your DeskAsk, open Administration \u203A Licence and type the key under Licence key." })] }), o.invoice && _jsx("p", { children: _jsxs("a", { href: '/api/store/orders/' + encodeURIComponent(id) + '/invoice?t=' + encodeURIComponent(token), children: ["Download invoice ", o.invoice] }) })] }) : o.status === 'failed' ? _jsxs(_Fragment, { children: [_jsx("h1", { children: "Almost there" }), _jsx("p", { children: o.error })] })
                        : o.status === 'pending' ? _jsxs(_Fragment, { children: [_jsx("h1", { children: "Waiting for the payment" }), _jsx("p", { className: "muted", children: "If you have paid, this page updates in a moment." })] })
                            : _jsxs(_Fragment, { children: [_jsx("h1", { children: "Setting up your order\u2026" }), _jsx("p", { className: "muted", children: "This takes up to a minute." })] }) }) }) });
}
