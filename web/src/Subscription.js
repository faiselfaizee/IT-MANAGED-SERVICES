import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { saveFile, useLoad } from './http';
import { Badge, Card, Empty, Loading, fmtDay } from './ui';
const MODULE = { servicedesk: 'Service Desk', assets: 'Asset management', monitoring: 'Monitoring', adsuite: 'AD Manager', admanager: 'AD Manager', vault: 'Vault', backup: 'Backup' };
const LIMIT = { agents: 'agents', max_assets: 'assets', max_devices: 'devices', max_users: 'users', max_domains: 'domains' };
const money = (v, c) => (c === 'INR' ? '₹' + v.toLocaleString('en-IN', { maximumFractionDigits: 2 }) : '$' + v.toLocaleString('en-US', { maximumFractionDigits: 2 }));
/** What a module's line of a plan says: its size. */
function size(v) {
    if (v && typeof v === 'object')
        return Object.entries(v).filter(([, n]) => typeof n === 'number').map(([k, n]) => `${n} ${LIMIT[k] ?? k}`).join(', ');
    return typeof v === 'number' ? String(v) : '';
}
/** Administration › Subscription: the plan bought, when it renews, its invoices, and where to renew or add modules. */
export default function Subscription() {
    const lic = useLoad('/api/platform/license');
    const sub = useLoad('/api/platform/subscription');
    const l = lic.data ?? {};
    const s = sub.data;
    const subs = (s?.subscriptions ?? []);
    const invoices = (s?.invoices ?? []);
    const now = subs.find((x) => x.state === 'active' || x.state === 'trial') ?? subs[0];
    const store = String(s?.storeUrl ?? 'https://www.deskask.in/pricing');
    const link = (what) => String((what === 'renew' ? s?.renewUrl : s?.changeUrl) || store);
    const days = now ? Math.ceil((new Date(String(now.endsAt)).getTime() - Date.now()) / 86_400_000) : null;
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Subscription" }), _jsx("p", { className: "muted", children: "What this DeskAsk is licensed for, when it renews, and its invoices." }), _jsx(Loading, { loading: (lic.loading && !lic.data) || (sub.loading && !s), error: lic.error || sub.error }), s && _jsxs(_Fragment, { children: [_jsxs(Card, { title: "Your plan", children: [now ? _jsxs(_Fragment, { children: [_jsxs("p", { children: [_jsx(Badge, { tone: now.state === 'active' ? 'ok' : now.state === 'trial' ? 'warn' : 'bad', children: now.state === 'trial' ? 'Free trial' : now.state === 'active' ? 'Active' : 'Ended' }), ' ', _jsx("strong", { children: now.kind === 'saas' ? 'In our cloud' : 'On your server' }), ", billed ", now.period === 'month' ? 'monthly' : 'yearly'] }), _jsx("p", { className: "muted", children: days != null && days >= 0 ? `Renews or ends on ${fmtDay(String(now.endsAt))} (${days} ${days === 1 ? 'day' : 'days'} from now).` : `Ended on ${fmtDay(String(now.endsAt))}.` }), _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Module" }), _jsx("th", { children: "Size" })] }) }), _jsx("tbody", { children: Object.entries((now.lines ?? {})).map(([k, v]) => _jsxs("tr", { children: [_jsx("td", { children: MODULE[k] ?? k }), _jsx("td", { children: size(v) || _jsx("span", { className: "muted", children: "included" }) })] }, k)) })] })] }) : _jsxs("p", { className: "muted", children: [l.plan ? _jsxs(_Fragment, { children: ["Licence ", _jsx("strong", { children: String(l.plan) }), l.expires ? `, valid until ${fmtDay(String(l.expires))}` : '', "."] }) : 'No plan was bought for this DeskAsk through the store.', " The details are on ", _jsx("a", { href: "#/licence", children: "Administration \u203A Licence" }), "."] }), _jsxs("div", { className: "actions", style: { justifyContent: 'flex-start' }, children: [_jsx("a", { className: "button primary", href: link('renew'), target: "_blank", rel: "noopener", children: "Renew" }), _jsx("a", { className: "button", href: link('change'), target: "_blank", rel: "noopener", children: "Add or change modules" })] }), _jsx("p", { className: "muted small", children: "The store opens in a new tab. After payment the licence updates here by itself; nothing to install." })] }), _jsx(Card, { title: "Invoices", children: !s.store ? _jsx("p", { className: "muted", children: "Invoices are emailed with each payment. If one is missing, write to servicedesk@deskask.in." })
                            : invoices.length === 0 ? _jsx(Empty, { children: "No invoices yet." })
                                : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Invoice" }), _jsx("th", { children: "Date" }), _jsx("th", { className: "num", children: "Total" }), _jsx("th", {})] }) }), _jsx("tbody", { children: invoices.map((i) => _jsxs("tr", { children: [_jsx("td", { className: "mono", children: i.number }), _jsx("td", { children: fmtDay(String(i.issuedAt)) }), _jsx("td", { className: "num", children: money(Number(i.total), String(i.currency)) }), _jsx("td", { children: _jsx("button", { type: "button", onClick: () => void saveFile(`/api/platform/subscription/invoices/${i.id}/pdf`, String(i.number).replace(/\//g, '-') + '.pdf'), children: "Download PDF" }) })] }, i.id)) })] }) })] })] });
}
