import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
/**
 * The side menu every module uses, in the same order everywhere: Overview, the module's working groups, Reports, Settings.
 * Groups with nothing the person may open are left out.
 */
export function ModNav({ title, base, groups, section, head, children }) {
    return _jsxs("div", { className: "modshell", children: [_jsxs("aside", { className: "modnav", "aria-label": title, children: [_jsxs("div", { className: "modnav-head", children: [_jsx("strong", { children: title }), head] }), groups.filter((g) => g.items.length > 0).map((g) => _jsxs("div", { className: "modnav-group", children: [g.group && _jsx("h4", { children: g.group }), g.items.map((i) => _jsxs("a", { href: '#/' + base + (i.key ? '/' + i.key : ''), className: section === i.key ? 'on' : '', "aria-current": section === i.key ? 'page' : undefined, children: [i.label, i.badge ? _jsx("span", { className: "pill", children: i.badge }) : null] }, i.key))] }, g.group))] }), _jsx("main", { className: "modmain", children: children })] });
}
