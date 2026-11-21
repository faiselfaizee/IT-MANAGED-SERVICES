import { useEffect, useState } from 'react';
export const DEFAULT_BRAND = { name: 'DeskAsk', headerText: '', footerText: '', accent: '', accent2: '', nav: '', theme: 'auto', hasLogo: false, logoVersion: 0, hasFavicon: false, faviconVersion: 0 };
let current = DEFAULT_BRAND;
const subs = new Set();
export const isHex = (c) => /^#[0-9a-fA-F]{6}$/.test(c);
const rgb = (h) => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
const hex = (r, g, b) => '#' + [r, g, b].map((v) => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0')).join('');
export const luminance = (h) => { const [r, g, b] = rgb(h).map((v) => { const c = v / 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
export const contrast = (a, b) => { const x = luminance(a), y = luminance(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
/** Black or white text, whichever reads better on this colour. */
export const inkOn = (bg) => (contrast(bg, '#ffffff') >= contrast(bg, '#101820') ? '#ffffff' : '#101820');
export const mix = (a, b, t) => { const [r1, g1, b1] = rgb(a), [r2, g2, b2] = rgb(b); return hex(r1 + (r2 - r1) * t, g1 + (g2 - g1) * t, b1 + (b2 - b1) * t); };
const VARS = ['--accent-2', '--side-bg', '--side-ink', '--side-hi', '--side-line', '--side-hover', '--side-grp', '--accent', '--accent-ink', '--accent-soft', '--nav', '--nav-2', '--nav-ink', '--nav-hi', '--nav-line', '--nav-bar', '--nav-bar-ico'];
/** Puts a brand on the page: colours as CSS variables, the theme as an attribute, the name in the tab title. */
export function applyBrand(b, root = document.documentElement) {
    const st = root.style;
    VARS.forEach((v) => st.removeProperty(v));
    if (isHex(b.accent)) {
        const [r, g, bl] = rgb(b.accent);
        st.setProperty('--accent', b.accent);
        st.setProperty('--accent-ink', inkOn(b.accent));
        st.setProperty('--accent-soft', `rgba(${r}, ${g}, ${bl}, .13)`);
    }
    if (isHex(b.accent2))
        st.setProperty('--accent-2', b.accent2);
    if (isHex(b.nav)) {
        const light = luminance(b.nav) > 0.45;
        // the left menu takes this colour; its text turns light or dark to stay readable
        st.setProperty('--side-bg', b.nav);
        st.setProperty('--side-ink', light ? '#33495a' : '#c3cfdc');
        st.setProperty('--side-hi', light ? '#0e2433' : '#ffffff');
        st.setProperty('--side-line', light ? 'rgba(0, 0, 0, .08)' : 'rgba(255, 255, 255, .08)');
        st.setProperty('--side-grp', light ? '#8597a6' : '#8296aa');
        st.setProperty('--side-hover', light ? 'rgba(0, 0, 0, .05)' : 'rgba(255, 255, 255, .08)');
        st.setProperty('--nav', b.nav);
        st.setProperty('--nav-2', mix(b.nav, light ? '#000000' : '#ffffff', light ? 0.07 : 0.09));
        st.setProperty('--nav-ink', light ? '#33415a' : '#b9c5d6');
        st.setProperty('--nav-hi', light ? '#0b1220' : '#ffffff');
        st.setProperty('--nav-line', light ? 'rgba(0, 0, 0, .1)' : 'rgba(255, 255, 255, .08)');
    }
    if (isHex(b.accent) || isHex(b.nav)) {
        // the marker beside the open menu entry must be seen on the menu colour
        const nav = isHex(b.nav) ? b.nav : '#0f1c2e';
        const a = isHex(b.accent) ? b.accent : '#3fcfc0';
        const bar = contrast(a, nav) >= 3 ? a : (luminance(nav) > 0.45 ? '#0b1220' : '#ffffff');
        st.setProperty('--nav-bar', bar);
        st.setProperty('--nav-bar-ico', bar);
    }
    if (b.theme === 'light' || b.theme === 'dark')
        root.setAttribute('data-theme', b.theme);
    else
        root.removeAttribute('data-theme');
    if (root === document.documentElement) {
        document.title = b.name || 'DeskAsk';
        applyIcon(b);
    }
}
let standardIcon = null;
/** The browser-tab icon: the uploaded favicon, else the logo, else the standard DeskAsk icon. */
export const iconUrl = (b) => b.hasFavicon ? `/api/branding/favicon?v=${b.faviconVersion ?? 0}` : b.hasLogo ? logoUrl(b) : null;
function applyIcon(b) {
    let link = document.querySelector('link[rel~="icon"]');
    if (!link) {
        link = document.createElement('link');
        link.rel = 'icon';
        document.head.appendChild(link);
    }
    if (standardIcon == null)
        standardIcon = link.getAttribute('href') ?? '';
    const url = iconUrl(b);
    link.href = url ?? standardIcon;
    if (url)
        link.removeAttribute('type');
}
export function getBrand() { return current; }
export function setBrand(b) { current = b; applyBrand(b); subs.forEach((f) => f(b)); }
/** Asks the server for this customer's brand (no sign-in needed: the sign-in page uses it too). A failure keeps the standard look. */
export async function loadBrand() {
    try {
        const r = await fetch('/api/branding');
        if (!r.ok)
            return;
        const j = (await r.json());
        setBrand({ ...DEFAULT_BRAND, ...j, name: j.name || 'DeskAsk' });
    }
    catch { /* keep the standard look */ }
}
export function useBrand() {
    const [b, setB] = useState(current);
    useEffect(() => { subs.add(setB); setB(current); return () => { subs.delete(setB); }; }, []);
    return b;
}
export const logoUrl = (b) => `/api/branding/logo?v=${b.logoVersion}`;
