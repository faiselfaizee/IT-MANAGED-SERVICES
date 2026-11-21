import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Suspense, lazy, useEffect, useMemo, useState } from 'react';
import { whoAmI } from './api';
import { signOut } from './auth';
import { LOOKS, currentLook, setLook } from './look';
import { Icon, Logo } from './chrome';
import { logoUrl, useBrand } from './brand';
import { Ring } from './Rings';
import { useSingleCompany } from './mode';
import { useHash, useLoad } from './http';
export const MODULES = ['Service Desk', 'Assets', 'Monitoring', 'AD Manager', 'Vault', 'Backup'];
/** Modules that are announced but not built. A built module that the licence does not include is simply not shown. */
const PLANNED = [];
const ABOUT = {
    'Service Desk': 'Tickets, requests and SLAs',
    Monitoring: 'Uptime and service checks',
    'AD Manager': 'Users, groups and password resets',
    Vault: 'Shared secrets and credentials',
    Backup: 'Results of the backup software customers already run',
};
const AssetsApp = lazy(() => import('./assets/AssetsApp'));
const Users = lazy(() => import('./Users'));
const Access = lazy(() => import('./Access'));
const Licence = lazy(() => import('./Licence'));
const Branding = lazy(() => import('./Branding'));
const Subscription = lazy(() => import('./Subscription'));
const Mail = lazy(() => import('./Mail'));
const Updates = lazy(() => import('./Updates'));
const VendorApp = lazy(() => import('./vendor/VendorApp'));
const SetupApp = lazy(() => import('./Setup'));
const ServiceDeskApp = lazy(() => import('./servicedesk/ServiceDeskApp'));
const MonitoringApp = lazy(() => import('./monitoring/MonitoringApp'));
const VaultApp = lazy(() => import('./vault/VaultApp'));
const AdApp = lazy(() => import('./admanager/AdApp'));
const BackupApp = lazy(() => import('./backup/BackupApp'));
/** Users, security groups and module permissions on one page. */
function UsersArea({ meId, tab }) {
    const t = tab === 'groups' || tab === 'permissions' ? tab : 'people';
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Users" }), _jsxs("nav", { className: "sub", "aria-label": "Users", children: [_jsx("a", { href: "#/users", className: t === 'people' ? 'on' : '', children: "Users" }), _jsx("a", { href: "#/users/groups", className: t === 'groups' ? 'on' : '', children: "Groups" }), _jsx("a", { href: "#/users/permissions", className: t === 'permissions' ? 'on' : '', children: "Permissions" })] }), t === 'people' ? _jsx(Users, { meId: meId, embedded: true }) : _jsx(Access, { tab: t })] });
}
const SECTION = { '': 'Home', servicedesk: 'Service Desk', assets: 'Assets', monitoring: 'Monitoring', setup: 'Devices setup', admanager: 'AD Manager', vault: 'Vault', backup: 'Backup',
    users: 'Administration › Users', access: 'Administration › Users', mail: 'Administration › Mail', updates: 'Administration › Updates', licence: 'Administration › Licence', subscription: 'Administration › Subscription', branding: 'Administration › Branding', vendor: 'Administration › Vendor console' };
const ROLE = { admin: 'Administrator', engineer: 'Engineer', viewer: 'Viewer', cadmin: 'Customer administrator', customer: 'Customer' };
/** The signed-in person: name and role, appearance, two-step sign-in and signing out. */
function UserMenu({ name, role, look, onLook, live }) {
    const [open, setOpen] = useState(false);
    const initials = name.split(/[\s@.]+/).filter(Boolean).slice(0, 2).map((x) => x[0]?.toUpperCase()).join('') || '?';
    return _jsxs("div", { className: "usermenu", onBlur: (e) => { if (!e.currentTarget.contains(e.relatedTarget))
            setOpen(false); }, children: [_jsxs("button", { className: "userbtn", "aria-haspopup": "menu", "aria-expanded": open, onClick: () => setOpen(!open), children: [_jsx("span", { className: "avatar", "aria-hidden": true, children: initials }), _jsxs("span", { className: "who", children: [_jsx("strong", { children: name || 'Signed in' }), _jsx("small", { children: ROLE[role] ?? role })] }), _jsx("i", { "aria-hidden": true, children: "\u25BE" })] }), _jsxs("div", { className: 'menu' + (open ? ' open' : ''), role: "menu", children: [_jsxs("label", { className: "look-pick", children: ["Appearance", _jsx("select", { value: look, onChange: (e) => onLook(e.target.value), children: LOOKS.map(([k, n]) => _jsx("option", { value: k, children: n }, k)) })] }), live ? _jsxs("a", { role: "menuitem", href: "/account/security?returnUrl=%2F", children: [_jsx(Icon, { name: "access" }), "Two-step sign-in"] }) : null, live ? _jsxs("a", { role: "menuitem", className: "out", href: "#/", onClick: (e) => { e.preventDefault(); signOut(); }, children: [_jsx(Icon, { name: "signout" }), "Sign out"] }) : null] })] });
}
export function Home({ who, live, assets, monitoring = false, vault = false, adm = false, backup = false, customer = false, deskOn = true }) {
    const bk = useLoad(live && backup ? '/api/backup/dashboard' : null);
    const b = (bk.data?.all ?? null);
    const mon = useLoad(live && monitoring ? '/api/monitoring/dashboard' : null);
    const mo = (mon.data?.all ?? null);
    const sum = useLoad(live && assets ? '/api/assets/summary' : null);
    const s = sum.data;
    const desk = useLoad(live && deskOn ? '/api/servicedesk/summary' : null);
    const d = desk.data;
    const ag = (s?.agents ?? {}), co = (s?.compliance ?? {}), wa = (s?.warranty ?? {});
    const n = (v) => Number(v ?? 0);
    const openAll = d ? n(d.open) + n(d.progress) + n(d.hold) : 0;
    return _jsxs(_Fragment, { children: [_jsxs("header", { className: "hero", children: [who ? _jsxs("h1", { children: ["Welcome, ", who.name] }) : _jsx("h1", { children: "\u00A0" }), _jsx("p", { children: "Your IT operations in one place." })] }), _jsxs("div", { className: "tiles", children: [deskOn && _jsxs("section", { className: "tile tile-wide", "aria-labelledby": "t-desk", children: [_jsxs("div", { className: "tile-head", children: [_jsx("span", { className: "chip", children: _jsx(Icon, { name: "Service Desk", size: 22 }) }), _jsxs("div", { children: [_jsx("h2", { id: "t-desk", children: _jsx("a", { href: "#/servicedesk", children: "Service Desk" }) }), _jsx("p", { className: "muted", children: d ? `${openAll} open tickets, ${n(d.unassigned)} unassigned` : 'Tickets with service levels and customer sign-in' })] }), _jsx("a", { className: "open", href: "#/servicedesk", children: "Open Service Desk" })] }), d && _jsxs("div", { className: "rings", children: [_jsx(Ring, { title: "Open tickets", unit: "open", empty: "No open tickets.", parts: [
                                            { key: 'o', label: 'New', value: n(d.open), tone: 'info', href: '#/servicedesk' },
                                            { key: 'p', label: 'In progress', value: n(d.progress), tone: 'ok', href: '#/servicedesk' },
                                            { key: 'h', label: 'Awaiting customer', value: n(d.hold), tone: 'none', href: '#/servicedesk' }
                                        ] }), _jsx(Ring, { title: "Service level", unit: "open", empty: "No open tickets.", parts: [
                                            { key: 'in', label: 'On time', value: Math.max(openAll - n(d.overdue), 0), tone: 'ok' },
                                            { key: 'late', label: 'Overdue', value: n(d.overdue), tone: 'bad' }
                                        ] }), _jsx(Ring, { title: "Closed", unit: "tickets", empty: "No tickets solved yet.", parts: [
                                            { key: 'r', label: 'Resolved', value: n(d.resolved), tone: 'ok' },
                                            { key: 'c', label: 'Closed', value: n(d.closed), tone: 'none' }
                                        ] })] })] }), assets && _jsxs("section", { className: "tile tile-wide", "aria-labelledby": "t-assets", children: [_jsxs("div", { className: "tile-head", children: [_jsx("span", { className: "chip", children: _jsx(Icon, { name: "Assets", size: 22 }) }), _jsxs("div", { children: [_jsx("h2", { id: "t-assets", children: _jsx("a", { href: "#/assets", children: "Assets" }) }), _jsx("p", { className: "muted", children: s ? `${n(s.total)} assets, ${n(s.total) - n(s.byStatus?.retired) - n(s.byStatus?.lost)} in service` : 'Every device, licence and contract' })] }), _jsx("a", { className: "open", href: "#/assets", children: "Open Assets" })] }), s && _jsxs("div", { className: "rings", children: [_jsx(Ring, { title: "Agents", unit: "devices", empty: "No devices in service yet.", parts: [
                                            { key: 'on', label: 'Live', value: n(ag.online), tone: 'ok', href: '#/assets/list?agent=online' },
                                            { key: 'due', label: 'Live, update due', value: n(ag.updateDue), tone: 'warn', href: '#/assets/list?agent=update' },
                                            { key: 'off', label: 'Not reporting', value: n(ag.offline), tone: 'bad', href: '#/assets/list?agent=offline' },
                                            { key: 'none', label: 'No agent', value: n(ag.none), tone: 'none', href: '#/assets/list?agent=none' }
                                        ] }), _jsx(Ring, { title: "Compliance", unit: "with agent", empty: "No device reports through an agent yet.", parts: [
                                            { key: 'c', label: 'Compliant', value: n(co.compliant), tone: 'ok' },
                                            { key: 'nc', label: 'Not compliant', value: n(co.nonCompliant), tone: 'bad' },
                                            { key: 'u', label: 'No data yet', value: n(co.unknown), tone: 'none' }
                                        ] }), _jsx(Ring, { title: "Warranty", unit: "devices", empty: "No devices in service yet.", parts: [
                                            { key: 'ok', label: 'Covered', value: n(wa.covered), tone: 'ok' },
                                            { key: 'soon', label: 'Ends within 30 days', value: n(wa.endingSoon), tone: 'warn' },
                                            { key: 'x', label: 'Expired', value: n(wa.expired), tone: 'bad' },
                                            { key: 'u', label: 'Not recorded', value: n(wa.unknown), tone: 'none' }
                                        ] })] }), s && n(co.nonCompliant) > 0 && _jsxs("p", { className: "muted tile-foot", children: ["Compliant means antivirus on and patches from the last 30 days. ", n(co.antivirusOff), " with antivirus off, ", n(co.patchesOlderThan30Days), " with older patches."] })] }), monitoring && _jsxs("section", { className: "tile tile-wide", "aria-labelledby": "t-mon", children: [_jsxs("div", { className: "tile-head", children: [_jsx("span", { className: "chip", children: _jsx(Icon, { name: "Monitoring", size: 22 }) }), _jsxs("div", { children: [_jsx("h2", { id: "t-mon", children: _jsx("a", { href: "#/monitoring", children: "Monitoring" }) }), _jsx("p", { className: "muted", children: mo ? `${n(mo.watched)} of ${n(mo.devices)} devices watched, ${n(mo.alertsCritical) + n(mo.alertsWarning)} open alerts` : 'Alerts for devices that are down, busy, full or failing' })] }), _jsx("a", { className: "open", href: "#/monitoring", children: "Open Monitoring" })] }), mo && _jsxs("div", { className: "rings", children: [_jsx(Ring, { title: "Device status", unit: "devices", empty: "No devices yet.", parts: [
                                            { key: 'ok', label: 'OK', value: n(mo.ok), tone: 'ok', href: '#/monitoring/devices?status=ok' },
                                            { key: 'w', label: 'Warning', value: n(mo.warning), tone: 'warn', href: '#/monitoring/devices?status=warning' },
                                            { key: 'c', label: 'Critical', value: n(mo.critical), tone: 'bad', href: '#/monitoring/devices?status=critical' },
                                            { key: 'm', label: 'Muted', value: n(mo.muted), tone: 'info', href: '#/monitoring/devices?status=muted' },
                                            { key: 'u', label: 'Not watched', value: n(mo.unwatched), tone: 'none', href: '#/monitoring/devices?status=unwatched' }
                                        ] }), _jsx(Ring, { title: "Open alerts", unit: "alerts", empty: "No open alerts. All clear.", parts: [
                                            { key: 'c', label: 'Critical', value: n(mo.alertsCritical), tone: 'bad', href: '#/monitoring/alerts?severity=critical' },
                                            { key: 'w', label: 'Warning', value: n(mo.alertsWarning), tone: 'warn', href: '#/monitoring/alerts?severity=warning' }
                                        ] }), _jsx(Ring, { title: "Disk space", unit: "devices", empty: "No disk data yet.", parts: [
                                            { key: 'ok', label: '20% or more free', value: n(mo.diskOk), tone: 'ok', href: '#/monitoring/devices?disk=ok' },
                                            { key: 'l', label: '10 to 20% free', value: n(mo.diskLow), tone: 'warn', href: '#/monitoring/devices?disk=low' },
                                            { key: 'c', label: 'Under 10% free', value: n(mo.diskCritical), tone: 'bad', href: '#/monitoring/devices?disk=critical' }
                                        ] })] })] }), backup && _jsxs("section", { className: "tile tile-wide", "aria-labelledby": "t-backup", children: [_jsxs("div", { className: "tile-head", children: [_jsx("span", { className: "chip", children: _jsx(Icon, { name: "Backup", size: 22 }) }), _jsxs("div", { children: [_jsx("h2", { id: "t-backup", children: _jsx("a", { href: "#/backup", children: "Backup" }) }), _jsx("p", { className: "muted", children: b ? `${n(b.jobs)} jobs, ${n(b.failed) + n(b.missed)} failed or missed` : 'Results of the backup software your customers run' })] }), _jsx("a", { className: "open", href: "#/backup", children: "Open Backup" })] }), b && n(b.jobs) > 0 && _jsxs("div", { className: "rings", children: [_jsx(Ring, { title: "Last result", unit: "jobs", empty: "No jobs yet.", parts: [
                                            { key: 's', label: 'Success', value: n(b.success), tone: 'ok', href: '#/backup/jobs?status=success' },
                                            { key: 'w', label: 'Warning', value: n(b.warning), tone: 'warn', href: '#/backup/jobs?status=warning' },
                                            { key: 'f', label: 'Failed', value: n(b.failed), tone: 'bad', href: '#/backup/jobs?status=failed' },
                                            { key: 'm', label: 'Missed', value: n(b.missed), tone: 'bad', href: '#/backup/jobs?status=missed' }
                                        ] }), _jsx(Ring, { title: "Last good backup", unit: "jobs", empty: "No watched jobs.", parts: [
                                            { key: 'd', label: 'Within a day', value: n(b.ageDay), tone: 'ok', href: '#/backup/jobs?age=day' },
                                            { key: 'w', label: '1 to 7 days ago', value: n(b.ageWeek), tone: 'warn', href: '#/backup/jobs?age=week' },
                                            { key: 'o', label: 'Over a week ago', value: n(b.ageOlder), tone: 'bad', href: '#/backup/jobs?age=older' },
                                            { key: 'n', label: 'Never', value: n(b.ageNever), tone: 'none', href: '#/backup/jobs?age=never' }
                                        ] })] })] }), vault && _jsx("section", { className: "tile tile-wide", "aria-labelledby": "t-vault", children: _jsxs("div", { className: "tile-head", children: [_jsx("span", { className: "chip", children: _jsx(Icon, { name: "Vault", size: 22 }) }), _jsxs("div", { children: [_jsx("h2", { id: "t-vault", children: _jsx("a", { href: "#/vault", children: "Vault" }) }), _jsx("p", { className: "muted", children: "Customer passwords, encrypted on your computer before they are saved" })] }), _jsx("a", { className: "open", href: "#/vault", children: "Open the Vault" })] }) }), adm && _jsx("section", { className: "tile tile-wide", "aria-labelledby": "t-ad", children: _jsxs("div", { className: "tile-head", children: [_jsx("span", { className: "chip", children: _jsx(Icon, { name: "AD Manager", size: 22 }) }), _jsxs("div", { children: [_jsx("h2", { id: "t-ad", children: _jsx("a", { href: "#/admanager", children: "AD Manager" }) }), _jsx("p", { className: "muted", children: "Look up and manage your customers' Active Directory accounts and groups" })] }), _jsx("a", { className: "open", href: "#/admanager", children: "Open AD Manager" })] }) }), !customer && PLANNED.filter((m) => m !== 'Assets' && m !== 'Service Desk' && (m !== 'Monitoring' || !monitoring) && (m !== 'Vault' || !vault) && (m !== 'AD Manager' || !adm) && (m !== 'Backup' || !backup)).map((m) => _jsxs("div", { className: "tile tile-soon", "aria-disabled": "true", children: [_jsx("span", { className: "chip", children: _jsx(Icon, { name: m, size: 22 }) }), _jsx("h2", { children: m }), _jsx("p", { className: "muted", children: ABOUT[m] }), _jsx("span", { className: "soon", children: "Planned" })] }, m))] })] });
}
export default function App({ fetcher }) {
    const [who, setWho] = useState(null);
    const [err, setErr] = useState('');
    const [look, setLookState] = useState(currentLook());
    const parts = useHash();
    useEffect(() => { whoAmI(fetcher).then(setWho, (e) => setErr(e.message)); }, [fetcher]);
    const singleCompany = useSingleCompany(); // one company: the pickers and filters for customers are hidden everywhere (styles.css)
    useEffect(() => { document.documentElement.classList.toggle('single-company', singleCompany && !fetcher); }, [singleCompany, fetcher]);
    const me = useLoad(fetcher ? null : '/api/platform/me');
    const isAdmin = me.data?.role === 'admin';
    const isCustomer = me.data?.role === 'customer';
    // every time DeskAsk shows comes from the server's clock; tell an administrator when it disagrees with this computer's
    const skewMin = useMemo(() => (me.data?.serverTime ? Math.round((new Date(me.data.serverTime).getTime() - Date.now()) / 60000) : 0), [me.data]);
    const mine = useLoad(fetcher ? null : '/api/platform/access/mine');
    const mods = useLoad(fetcher ? null : '/api/platform/modules');
    const lic = (k) => !!fetcher || (Array.isArray(mods.data) && mods.data.some((m) => m.key === k && (m.licensed || m.readOnly))); // in the licence (or kept read-only after it ended)
    const readsAssets = !!fetcher || (!!mine.data?.assets?.read && mine.data.assets.read !== 'none');
    const canAssets = lic('assets') && readsAssets; // the Assets menu entry
    const inAssets = parts[0] === 'assets' && (canAssets || (lic('monitoring') && readsAssets)); // Monitoring alone still opens the shared device list
    const canSetup = isAdmin && (lic('assets') || lic('monitoring')); // customers, logins, agents: shared by Assets and Monitoring
    const inSetup = parts[0] === 'setup' && canSetup;
    const canDesk = lic('servicedesk');
    const inDesk = parts[0] === 'servicedesk' && canDesk;
    const canMon = lic('monitoring') && (!!fetcher || (!!mine.data?.monitoring?.read && mine.data.monitoring.read !== 'none'));
    const inMon = parts[0] === 'monitoring' && canMon;
    const canVault = !fetcher && !!me.data && me.data.role !== 'customer' && !!(Array.isArray(mods.data) && (mods.data.find((m) => m.key === 'vault')?.licensed || mods.data.find((m) => m.key === 'vault')?.readOnly));
    const inVault = parts[0] === 'vault' && canVault;
    const canAd = !fetcher && !!me.data && me.data.role !== 'customer' && !!(Array.isArray(mods.data) && (mods.data.find((m) => m.key === 'adsuite')?.licensed || mods.data.find((m) => m.key === 'adsuite')?.readOnly));
    const inAd = parts[0] === 'admanager' && canAd;
    const canBackup = !fetcher && !!(Array.isArray(mods.data) && (mods.data.find((m) => m.key === 'backup')?.licensed || mods.data.find((m) => m.key === 'backup')?.readOnly)) && !!mine.data?.backup?.read && mine.data.backup.read !== 'none';
    const inBackup = parts[0] === 'backup' && canBackup;
    const inUsers = (parts[0] === 'users' || parts[0] === 'access') && isAdmin;
    const inLicence = parts[0] === 'licence' && isAdmin;
    const inBranding = parts[0] === 'branding' && isAdmin;
    const inSub = parts[0] === 'subscription' && isAdmin;
    const brand = useBrand();
    const inMail = parts[0] === 'mail' && isAdmin;
    const commerce = useLoad(fetcher || !isAdmin ? null : '/api/commerce/status', [isAdmin]);
    const upd = useLoad(fetcher || !isAdmin ? null : '/api/platform/updates/access', [isAdmin]);
    const canUpdate = isAdmin && upd.data?.allowed !== false; // the shared server is updated by the vendor only
    const inUpdates = parts[0] === 'updates' && canUpdate;
    const isVendor = isAdmin && !!commerce.data?.enabled;
    const inVendor = parts[0] === 'vendor' && isVendor;
    const readOnly = Array.isArray(mods.data) ? mods.data.filter((m) => m.readOnly).length > 0 : false;
    return (_jsxs("div", { className: "shell", children: [_jsxs("nav", { "aria-label": "Modules", className: "side", children: [_jsxs("a", { className: "brand", href: "#/", "aria-label": brand.name + ' home', children: [brand.hasLogo ? _jsx("span", { className: "logo-chip", children: _jsx("img", { src: logoUrl(brand), alt: "" }) }) : _jsx(Logo, { size: 38 }), _jsxs("span", { className: "brand-text", children: [_jsx("strong", { children: brand.name }), _jsx("small", { children: brand.headerText || 'IT managed services' })] })] }), _jsxs("div", { className: "side-group", children: [_jsxs("a", { href: "#/", className: !inAssets && !inSetup && !inDesk && !inMon && !inVault && !inAd && !inBackup && !inUsers && !inLicence && !inSub && !inBranding && !inMail && !inUpdates && !inVendor ? 'on' : '', children: [_jsx(Icon, { name: "home" }), "Home"] }), MODULES.filter((m) => m !== 'Assets' || canAssets).map((m) => m === 'Assets'
                                ? _jsxs("a", { href: "#/assets", className: inAssets ? 'on' : '', children: [_jsx(Icon, { name: m }), m] }, m)
                                : m === 'Service Desk' && canDesk
                                    ? _jsxs("a", { href: "#/servicedesk", className: inDesk ? 'on' : '', children: [_jsx(Icon, { name: m }), m] }, m)
                                    : m === 'Monitoring' && canMon
                                        ? _jsxs("a", { href: "#/monitoring", className: inMon ? 'on' : '', children: [_jsx(Icon, { name: m }), m] }, m)
                                        : m === 'AD Manager' && canAd
                                            ? _jsxs("a", { href: "#/admanager", className: inAd ? 'on' : '', children: [_jsx(Icon, { name: m }), m] }, m)
                                            : m === 'Backup' && canBackup
                                                ? _jsxs("a", { href: "#/backup", className: inBackup ? 'on' : '', children: [_jsx(Icon, { name: m }), m] }, m)
                                                : m === 'Vault' && canVault
                                                    ? _jsxs("a", { href: "#/vault", className: inVault ? 'on' : '', children: [_jsx(Icon, { name: m }), m] }, m)
                                                    : null), "   "] }), isAdmin ? _jsxs("div", { className: "side-group", children: [_jsx("h4", { children: "Administration" }), _jsxs("a", { href: "#/users", className: inUsers ? 'on' : '', children: [_jsx(Icon, { name: "users" }), "Users"] }), canSetup ? _jsxs("a", { href: "#/setup", className: inSetup ? 'on' : '', children: [_jsx(Icon, { name: "Assets" }), "Devices setup"] }) : null, _jsxs("a", { href: "#/mail", className: inMail ? 'on' : '', children: [_jsx(Icon, { name: "mail" }), "Mail"] }), canUpdate ? _jsxs("a", { href: "#/updates", className: inUpdates ? 'on' : '', children: [_jsx(Icon, { name: "updates" }), "Updates"] }) : null, _jsxs("a", { href: "#/licence", className: inLicence ? 'on' : '', children: [_jsx(Icon, { name: "licence" }), "Licence"] }), _jsxs("a", { href: "#/subscription", className: inSub ? 'on' : '', children: [_jsx(Icon, { name: "licence" }), "Subscription"] }), _jsxs("a", { href: "#/branding", className: inBranding ? 'on' : '', children: [_jsx(Icon, { name: "branding" }), "Branding"] }), isVendor ? _jsxs("a", { href: "#/vendor", className: inVendor ? 'on' : '', children: [_jsx(Icon, { name: "store" }), "Vendor console"] }) : null] }) : null] }), _jsxs("header", { className: "topbar", children: [_jsxs("nav", { className: "crumb", "aria-label": "You are here", children: [who?.name ? _jsxs(_Fragment, { children: [_jsx("span", { children: who.name }), _jsx("b", { "aria-hidden": true, children: "\u203A" })] }) : null, (SECTION[parts[0] ?? ''] ?? 'Home').split(' › ').map((x, i, all) => _jsxs("span", { className: i === all.length - 1 ? 'here' : '', children: [x, i < all.length - 1 ? _jsx("b", { "aria-hidden": true, children: " \u203A " }) : null] }, x))] }), _jsx(UserMenu, { name: String(me.data?.name ?? who?.name ?? ''), role: String(me.data?.role ?? ''), look: look, onLook: (v) => { setLook(v); setLookState(v); }, live: !fetcher })] }), _jsxs("main", { className: "main", children: [err ? _jsx("p", { role: "alert", className: "err", children: err }) : null, readOnly ? _jsxs("p", { role: "alert", className: "err", children: ["The licence has ended, so DeskAsk is read-only: everything stays visible, but nothing can be changed.", isAdmin ? _jsxs(_Fragment, { children: [" ", _jsx("a", { href: "#/licence", children: "Renew or install a licence" }), "."] }) : ' Ask your administrator to renew the licence.'] }) : null, isAdmin && Math.abs(skewMin) >= 3 ? _jsxs("p", { role: "alert", className: "err", children: ["The server's clock is about ", Math.floor(Math.abs(skewMin) / 60), " h ", Math.abs(skewMin) % 60, " min ", skewMin > 0 ? 'ahead of' : 'behind', " this computer, so times shown here and written to logs are wrong. On the server, set the correct time zone first (Windows: ", _jsx("code", { children: "Set-TimeZone -Id \"India Standard Time\"" }), " for India), then correct the time (", _jsx("code", { children: "w32tm /resync" }), ")."] }) : null, inSetup ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(SetupApp, {}) })
                        : inAssets
                            ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(AssetsApp, {}) })
                            : inDesk ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(ServiceDeskApp, {}) })
                                : inMon ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(MonitoringApp, {}) })
                                    : inVault ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(VaultApp, {}) })
                                        : inAd ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(AdApp, {}) })
                                            : inBackup ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(BackupApp, {}) })
                                                : inUpdates ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(Updates, {}) })
                                                    : inMail ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(Mail, {}) })
                                                        : inSub ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(Subscription, {}) })
                                                            : inBranding ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(Branding, {}) })
                                                                : inLicence ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(Licence, {}) })
                                                                    : inVendor ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(VendorApp, { path: parts.slice(1) }) })
                                                                        : inUsers ? _jsx(Suspense, { fallback: _jsx("p", { className: "muted", children: "Loading\u2026" }), children: _jsx(UsersArea, { meId: me.data?.sub, tab: parts[0] === 'access' ? 'groups' : (parts[1] ?? '') }) })
                                                                            : !err ? _jsx(Home, { who: who, live: !fetcher, assets: canAssets, monitoring: canMon && !fetcher, vault: canVault, adm: canAd, backup: canBackup, deskOn: canDesk, customer: isCustomer }) : null, brand.footerText ? _jsx("footer", { className: "site-foot", children: brand.footerText }) : null] })] }));
}
