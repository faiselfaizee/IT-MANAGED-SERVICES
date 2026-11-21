import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
/** Small line icons, drawn on a 24 grid. One stroke weight everywhere so they read as a set. */
const PATHS = {
    home: _jsx("path", { d: "M3 11.5 12 4l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z" }),
    'Service Desk': _jsxs(_Fragment, { children: [_jsx("path", { d: "M3 9a2 2 0 0 0 0 6v3a1 1 0 0 0 1 1h16a1 1 0 0 0 1-1v-3a2 2 0 0 1 0-6V6a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1z" }), _jsx("path", { d: "M14 5v3m0 3v2m0 3v3" })] }),
    Assets: _jsxs(_Fragment, { children: [_jsx("rect", { x: "4", y: "5", width: "16", height: "11", rx: "2" }), _jsx("path", { d: "M2 20h20" })] }),
    Monitoring: _jsx("path", { d: "M3 12h4l3-8 4 16 3-8h4" }),
    'AD Manager': _jsxs(_Fragment, { children: [_jsx("circle", { cx: "9.5", cy: "8", r: "3.5" }), _jsx("path", { d: "M3.5 20v-1a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v1" }), _jsx("path", { d: "M16 4.5a3.5 3.5 0 0 1 0 7M20.5 19v-1a4 4 0 0 0-3-3.9" })] }),
    Vault: _jsxs(_Fragment, { children: [_jsx("rect", { x: "5", y: "11", width: "14", height: "10", rx: "2" }), _jsx("path", { d: "M8 11V8a4 4 0 0 1 8 0v3" }), _jsx("circle", { cx: "12", cy: "16", r: "1" })] }),
    Backup: _jsxs(_Fragment, { children: [_jsx("ellipse", { cx: "12", cy: "6", rx: "8", ry: "3" }), _jsx("path", { d: "M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" })] }),
    users: _jsxs(_Fragment, { children: [_jsx("circle", { cx: "12", cy: "8", r: "3.5" }), _jsx("path", { d: "M5 20v-1a5 5 0 0 1 5-5h4a5 5 0 0 1 5 5v1" })] }),
    access: _jsxs(_Fragment, { children: [_jsx("path", { d: "M12 3 5 6v5c0 4.5 3 8 7 10 4-2 7-5.500 7-10V6z" }), _jsx("path", { d: "m9 12 2 2 4-4" })] }),
    mail: _jsxs(_Fragment, { children: [_jsx("rect", { x: "3", y: "5", width: "18", height: "14", rx: "2" }), _jsx("path", { d: "m3 7 9 6 9-6" })] }),
    updates: _jsxs(_Fragment, { children: [_jsx("path", { d: "M20 12a8 8 0 1 1-2.34-5.66" }), _jsx("path", { d: "M20 4v5h-5" })] }),
    branding: _jsxs(_Fragment, { children: [_jsx("circle", { cx: "12", cy: "12", r: "8.5" }), _jsx("circle", { cx: "8.5", cy: "10", r: "1.2" }), _jsx("circle", { cx: "12", cy: "7.5", r: "1.2" }), _jsx("circle", { cx: "15.5", cy: "10", r: "1.2" }), _jsx("path", { d: "M12 20.5c-1.6 0-2-1.4-1.2-2.4.8-1 .4-2.1-.9-2.1H8" })] }),
    server: _jsxs(_Fragment, { children: [_jsx("rect", { x: "4", y: "3.5", width: "16", height: "7", rx: "1.5" }), _jsx("rect", { x: "4", y: "13.5", width: "16", height: "7", rx: "1.5" }), _jsx("path", { d: "M8 7h.01M8 17h.01M12 7h4M12 17h4" })] }),
    network: _jsxs(_Fragment, { children: [_jsx("rect", { x: "3", y: "14", width: "18", height: "6", rx: "1.5" }), _jsx("path", { d: "M7 17h.01M11 17h.01M15 17h.01M12 14V9M8 9h8M8 9V6M16 9V6" })] }),
    printer: _jsxs(_Fragment, { children: [_jsx("path", { d: "M7 9V3.5h10V9" }), _jsx("rect", { x: "3.5", y: "9", width: "17", height: "7.5", rx: "1.5" }), _jsx("path", { d: "M7 14h10v6.5H7z" })] }),
    phone: _jsxs(_Fragment, { children: [_jsx("rect", { x: "7", y: "2.5", width: "10", height: "19", rx: "2" }), _jsx("path", { d: "M11 18.5h2" })] }),
    licence: _jsxs(_Fragment, { children: [_jsx("rect", { x: "4", y: "3", width: "16", height: "18", rx: "2" }), _jsx("path", { d: "M8 8h8M8 12h8M8 16h5" })] }),
    store: _jsxs(_Fragment, { children: [_jsx("path", { d: "M4 9.5 5.5 4h13L20 9.5" }), _jsx("path", { d: "M4 9.5a2.7 2.7 0 0 0 5.3 0 2.7 2.7 0 0 0 5.4 0 2.7 2.7 0 0 0 5.3 0" }), _jsx("path", { d: "M5.5 12v8h13v-8M10 20v-4.5h4V20" })] }),
    signout: _jsxs(_Fragment, { children: [_jsx("path", { d: "M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" }), _jsx("path", { d: "m16 17 5-5-5-5M21 12H9" })] }),
};
export function Icon({ name, size = 20 }) {
    return _jsx("svg", { className: "ico", width: size, height: size, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: "1.7", strokeLinecap: "round", strokeLinejoin: "round", "aria-hidden": "true", children: PATHS[name] ?? PATHS.home });
}
/** The DeskAsk mark: a speech bubble (the "ask") on a tile of light. */
export function Logo({ size = 34 }) {
    return _jsxs("svg", { width: size, height: size, viewBox: "0 0 34 34", "aria-hidden": "true", children: [_jsxs("defs", { children: [_jsxs("linearGradient", { id: "lg", x1: "4", y1: "2", x2: "30", y2: "32", children: [_jsx("stop", { offset: "0", stopColor: "#2fe0cf" }), _jsx("stop", { offset: "1", stopColor: "#5b6bff" })] }), _jsxs("linearGradient", { id: "lh", x1: "0", y1: "0", x2: "0", y2: "1", children: [_jsx("stop", { offset: "0", stopColor: "#fff", stopOpacity: ".55" }), _jsx("stop", { offset: "1", stopColor: "#fff", stopOpacity: "0" })] })] }), _jsx("rect", { width: "34", height: "34", rx: "10", fill: "url(#lg)" }), _jsx("rect", { width: "34", height: "17", rx: "10", fill: "url(#lh)" }), _jsx("path", { d: "M10 12.5A3.5 3.5 0 0 1 13.5 9h7a3.5 3.5 0 0 1 3.5 3.5v5a3.5 3.5 0 0 1-3.5 3.5H17l-4.2 3.4V21h-.3A3.5 3.5 0 0 1 10 17.5z", fill: "none", stroke: "#fff", strokeWidth: "1.9", strokeLinejoin: "round" }), _jsx("circle", { cx: "17", cy: "15", r: "1.25", fill: "#fff" })] });
}
