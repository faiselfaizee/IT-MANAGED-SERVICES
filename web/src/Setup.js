import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useHash } from './http';
import { useSingleCompany } from './mode';
import Companies from './assets/Companies';
import Credentials from './assets/Credentials';
import Discover from './assets/Discover';
import Duplicates from './assets/Duplicates';
import DirectoryPage from './assets/DirectoryPage';
import Fleet from './assets/Fleet';
const PAGES = [['companies', 'Customers'], ['credentials', 'Credentials'], ['directory', 'Domain controller'], ['fleet', 'Agents & collectors'], ['discover', 'Discover'], ['duplicates', 'Duplicates']];
/**
 * Administration › Devices setup: the customers, their logins, the agents and site controllers, scans and duplicate check.
 * They are shared by Asset management and Monitoring, so they live here and work with either licence.
 */
export default function Setup() {
    const [, a] = useHash();
    const one = useSingleCompany();
    const pages = one ? PAGES.filter(([k]) => k !== 'companies') : PAGES.filter(([k]) => k !== 'directory'); // with several customers the domain controller is set up on each customer   // one company: nobody adds customers
    const page = pages.some(([k]) => k === a) ? a : pages[0][0];
    return _jsxs("div", { className: "modshell", children: [_jsxs("aside", { className: "modnav", "aria-label": "Devices setup", children: [_jsx("div", { className: "modnav-head", children: _jsx("strong", { children: "Devices setup" }) }), _jsx("div", { className: "modnav-group", children: pages.map(([k, l]) => _jsx("a", { href: '#/setup/' + k, className: page === k ? 'on' : '', children: l }, k)) })] }), _jsx("main", { className: "modmain", children: page === 'companies' ? _jsx(Companies, {})
                    : page === 'directory' ? _jsx(DirectoryPage, {})
                        : page === 'credentials' ? _jsx(Credentials, {})
                            : page === 'fleet' ? _jsx(Fleet, {})
                                : page === 'discover' ? _jsx(Discover, {})
                                    : _jsx(Duplicates, {}) })] });
}
