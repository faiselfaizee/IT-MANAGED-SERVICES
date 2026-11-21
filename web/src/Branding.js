import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useRef, useState } from 'react';
import { accessToken } from './auth';
import { DEFAULT_BRAND, contrast, iconUrl, inkOn, isHex, logoUrl, setBrand, useBrand } from './brand';
import { api, message } from './http';
import { Card, Err, Field } from './ui';
import PdfDesign from './PdfDesign';
import Domains from './Domains';
const TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif', 'image/svg+xml'];
const MAX = 256 * 1024;
const ICON_TYPES = ['image/png', 'image/svg+xml', 'image/x-icon', 'image/vnd.microsoft.icon'];
const ICON_MAX = 128 * 1024;
/** Administration › Branding: the logo, name, header and footer text, colours and theme of this customer's DeskAsk. */
export default function Branding() {
    const saved = useBrand();
    const [f, setF] = useState(saved);
    const [err, setErr] = useState('');
    const [ok, setOk] = useState('');
    const [busy, setBusy] = useState(false);
    const file = useRef(null);
    const icon = useRef(null);
    const loaded = useRef(false);
    useEffect(() => { if (!loaded.current) {
        loaded.current = true;
        setF(saved);
    } }, [saved]);
    const set = (p) => { setF({ ...f, ...p }); setOk(''); };
    const accentOk = (!f.accent || isHex(f.accent)) && (!f.accent2 || isHex(f.accent2));
    const navOk = !f.nav || isHex(f.nav);
    const warn = isHex(f.accent) && contrast(f.accent, '#ffffff') < 3 && contrast(f.accent, '#101820') < 3 ? 'This accent colour is hard to read on both light and dark backgrounds.' : '';
    const run = async (fn, done) => {
        setBusy(true);
        setErr('');
        setOk('');
        try {
            const b = await fn();
            setBrand({ ...DEFAULT_BRAND, ...b });
            setF({ ...DEFAULT_BRAND, ...b });
            setOk(done);
        }
        catch (e) {
            setErr(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    const save = () => run(() => api('/api/branding', { method: 'PUT', body: { name: f.name, headerText: f.headerText, footerText: f.footerText, accent: f.accent, accent2: f.accent2, nav: f.nav, theme: f.theme } }), 'Saved. Everyone sees the new look after their next page load.');
    const reset = () => { if (confirm('Go back to the standard DeskAsk look? The logo and browser-tab icon are removed too.'))
        void run(() => api('/api/branding/reset', { method: 'POST', body: {} }), 'The standard look is back.'); };
    const removeLogo = () => run(() => api('/api/branding/logo', { method: 'DELETE' }), 'The logo is removed.');
    const upload = (file) => {
        if (!file)
            return;
        if (!TYPES.includes(file.type)) {
            setErr('Use a PNG, JPEG, WebP, GIF or SVG picture.');
            return;
        }
        if (file.size > MAX) {
            setErr('The logo is larger than 256 KB. Use a smaller picture.');
            return;
        }
        void run(async () => {
            const r = await fetch('/api/branding/logo', { method: 'PUT', headers: { 'content-type': file.type, authorization: 'Bearer ' + (accessToken() ?? '') }, body: file });
            const t = await r.text();
            let j = {};
            try {
                j = t ? JSON.parse(t) : {};
            }
            catch { /* not json */ }
            if (!r.ok)
                throw new Error(message(r.status, j));
            return j;
        }, 'The logo is saved.');
    };
    const send = (url, file, type) => async () => {
        const r = await fetch(url, { method: 'PUT', headers: { 'content-type': type, authorization: 'Bearer ' + (accessToken() ?? '') }, body: file });
        const t = await r.text();
        let j = {};
        try {
            j = t ? JSON.parse(t) : {};
        }
        catch { /* not json */ }
        if (!r.ok)
            throw new Error(message(r.status, j));
        return j;
    };
    const uploadIcon = (file) => {
        if (!file)
            return;
        // some browsers give .ico files no type; the name tells
        const type = file.type || (file.name.toLowerCase().endsWith('.ico') ? 'image/x-icon' : '');
        if (!ICON_TYPES.includes(type)) {
            setErr('Use a PNG, SVG or ICO file for the browser-tab icon.');
            return;
        }
        if (file.size > ICON_MAX) {
            setErr('The browser-tab icon is larger than 128 KB. Use a smaller picture (32 x 32 or 64 x 64 pixels is enough).');
            return;
        }
        void run(send('/api/branding/favicon', file, type), 'The browser-tab icon is saved. Browsers may show the old one until the page is reloaded.');
    };
    const removeIcon = () => run(() => api('/api/branding/favicon', { method: 'DELETE' }), 'The browser-tab icon is removed.');
    const tabIcon = iconUrl(f);
    const pvAccent = isHex(f.accent) ? f.accent : '#0d8a7f';
    const pvNav = isHex(f.nav) ? f.nav : '#ffffff';
    const pvA2 = isHex(f.accent2) ? f.accent2 : '#5b6bff';
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Branding" }), _jsx("p", { className: "muted", children: "How DeskAsk looks for everyone at this customer: the sign-in page and the web app. Only administrators can change it." }), _jsx(Err, { text: err }), ok ? _jsx("p", { role: "status", children: ok }) : null, _jsxs("div", { className: "brand-page", children: [_jsxs("div", { children: [_jsx(Card, { title: "Name and logo", children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Name", children: _jsx("input", { value: f.name, maxLength: 60, onChange: (e) => set({ name: e.target.value }) }) }), _jsxs("div", { className: "logo-box", children: [_jsx("div", { className: "logo-prev", children: f.hasLogo ? _jsx("img", { src: logoUrl(f), alt: "Current logo" }) : _jsx("span", { className: "muted", children: "No logo" }) }), _jsxs("div", { children: [_jsx("input", { ref: file, type: "file", accept: TYPES.join(','), hidden: true, "aria-label": "Logo file", onChange: (e) => { upload(e.target.files?.[0]); e.target.value = ''; } }), _jsx("button", { type: "button", disabled: busy, onClick: () => file.current?.click(), children: f.hasLogo ? 'Replace logo' : 'Upload logo' }), ' ', f.hasLogo && _jsx("button", { type: "button", disabled: busy, onClick: () => void removeLogo(), children: "Remove" }), _jsx("p", { className: "muted", style: { margin: '6px 0 0' }, children: "PNG, JPEG, WebP, GIF or SVG, up to 256 KB. A wide logo on a transparent background works best." })] })] })] }) }), _jsx(Card, { title: "Browser-tab icon (favicon)", children: _jsx("div", { className: "form", children: _jsxs("div", { className: "logo-box", children: [_jsxs("div", { className: "tab-prev", "aria-label": "Preview of a browser tab", children: [_jsx("span", { className: "tab-prev-icon", children: tabIcon ? _jsx("img", { src: tabIcon, alt: "", width: 16, height: 16 }) : _jsx("span", { className: "tab-prev-std", "aria-hidden": true }) }), _jsx("span", { className: "tab-prev-title", children: f.name || 'DeskAsk' })] }), _jsxs("div", { children: [_jsx("input", { ref: icon, type: "file", accept: ".ico,.png,.svg,image/png,image/svg+xml,image/x-icon", hidden: true, "aria-label": "Browser-tab icon file", onChange: (e) => { uploadIcon(e.target.files?.[0]); e.target.value = ''; } }), _jsx("button", { type: "button", disabled: busy, onClick: () => icon.current?.click(), children: f.hasFavicon ? 'Replace icon' : 'Upload icon' }), ' ', f.hasFavicon && _jsx("button", { type: "button", disabled: busy, onClick: () => void removeIcon(), children: "Remove" }), _jsxs("p", { className: "muted", style: { margin: '6px 0 0' }, children: ["PNG, SVG or ICO, square, up to 128 KB. Shown in the browser tab, bookmarks and the sign-in page. ", f.hasFavicon ? '' : f.hasLogo ? 'Until you upload one, the logo is used.' : 'Until you upload one, the standard DeskAsk icon is used.'] })] })] }) }) }), _jsx(Card, { title: "Header and footer", children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Header text (shown at the top of every page)", children: _jsx("input", { value: f.headerText, maxLength: 120, onChange: (e) => set({ headerText: e.target.value }) }) }), _jsx(Field, { label: "Footer text (shown at the bottom of every page and on the sign-in page)", children: _jsx("textarea", { rows: 2, value: f.footerText, maxLength: 300, onChange: (e) => set({ footerText: e.target.value }) }) })] }) }), _jsx(Card, { title: "Colours and theme", children: _jsxs("div", { className: "form", children: [_jsxs("div", { className: "fld", children: [_jsx("span", { className: "cap", children: "Accent colour (buttons and links)" }), _jsxs("div", { className: "colour-row", children: [_jsx("input", { type: "color", "aria-label": "Accent colour picker", value: isHex(f.accent) ? f.accent : '#0d8a7f', onChange: (e) => set({ accent: e.target.value }) }), _jsx("input", { type: "text", "aria-label": "Accent colour", placeholder: "#0d8a7f", value: f.accent, maxLength: 7, onChange: (e) => set({ accent: e.target.value.trim() }) }), f.accent && _jsx("button", { type: "button", onClick: () => set({ accent: '' }), children: "Standard" })] })] }), !accentOk && _jsx(Err, { text: "The accent colour must be like #0d8a7f." }), warn && _jsx("p", { className: "muted", role: "note", children: warn }), _jsxs("div", { className: "fld", children: [_jsx("span", { className: "cap", children: "Second colour (the short end of the stripe at the top of the menu)" }), _jsxs("div", { className: "colour-row", children: [_jsx("input", { type: "color", "aria-label": "Second colour picker", value: isHex(f.accent2) ? f.accent2 : '#5b6bff', onChange: (e) => set({ accent2: e.target.value }) }), _jsx("input", { type: "text", "aria-label": "Second colour", placeholder: "#5b6bff", value: f.accent2, maxLength: 7, onChange: (e) => set({ accent2: e.target.value.trim() }) }), f.accent2 && _jsx("button", { type: "button", onClick: () => set({ accent2: '' }), children: "Standard" })] })] }), f.accent2 && !isHex(f.accent2) && _jsx(Err, { text: "The second colour must be like #c31528." }), _jsxs("div", { className: "fld", children: [_jsx("span", { className: "cap", children: "Menu colour (left side; empty = white)" }), _jsxs("div", { className: "colour-row", children: [_jsx("input", { type: "color", "aria-label": "Menu colour picker", value: isHex(f.nav) ? f.nav : '#ffffff', onChange: (e) => set({ nav: e.target.value }) }), _jsx("input", { type: "text", "aria-label": "Menu colour", placeholder: "white", value: f.nav, maxLength: 7, onChange: (e) => set({ nav: e.target.value.trim() }) }), f.nav && _jsx("button", { type: "button", onClick: () => set({ nav: '' }), children: "Standard" })] })] }), !navOk && _jsx(Err, { text: "The menu colour must be like #0f1c2e." }), _jsx(Field, { label: "Theme", children: _jsxs("select", { value: f.theme, onChange: (e) => set({ theme: e.target.value }), children: [_jsx("option", { value: "auto", children: "Follow each person's computer (light or dark)" }), _jsx("option", { value: "light", children: "Always light" }), _jsx("option", { value: "dark", children: "Always dark" })] }) })] }) }), _jsxs("p", { children: [_jsx("button", { type: "button", className: "primary", disabled: busy || !accentOk || !navOk || !f.name.trim(), onClick: () => void save(), children: "Save" }), ' ', _jsx("button", { type: "button", disabled: busy, onClick: reset, children: "Reset to standard" })] })] }), _jsxs("div", { className: "preview", "aria-label": "Preview", children: [_jsx("div", { style: { height: 5, background: `linear-gradient(90deg, ${pvAccent} 0 72%, ${pvA2} 72% 100%)` } }), _jsxs("div", { className: "p-nav", style: { background: pvNav, color: inkOn(pvNav) }, children: [f.hasLogo ? _jsx("span", { className: "logo-chip", children: _jsx("img", { src: logoUrl(f), alt: "" }) }) : null, _jsx("span", { children: f.name || 'DeskAsk' })] }), _jsxs("div", { className: "p-top", children: [_jsx("span", { children: "Home" }), f.headerText && _jsx("span", { className: "muted", children: f.headerText })] }), _jsxs("div", { className: "p-body", children: [_jsx("span", { children: "Preview" }), _jsx("button", { type: "button", style: { background: pvAccent, color: inkOn(pvAccent), justifySelf: 'start' }, tabIndex: -1, children: "Button" })] }), f.footerText && _jsx("div", { className: "p-foot", children: f.footerText })] })] }), _jsx(Domains, {}), _jsx(PdfDesign, { url: "/api/branding/pdf", title: "Reports and exports (PDF)", intro: "The design of the PDFs this installation makes: service and uptime reports and asset exports. Save, then download any report to see it. 'Use the standard design' is the logo's teal with black letters and borders; press Save design to keep it." })] });
}
