import { useEffect, useRef, useState } from 'react';
import { accessToken } from './auth';
import { DEFAULT_BRAND, contrast, iconUrl, inkOn, isHex, logoUrl, setBrand, useBrand, type Brand } from './brand';
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
  const [f, setF] = useState<Brand>(saved); const [err, setErr] = useState(''); const [ok, setOk] = useState(''); const [busy, setBusy] = useState(false);
  const file = useRef<HTMLInputElement>(null); const icon = useRef<HTMLInputElement>(null); const loaded = useRef(false);
  useEffect(() => { if (!loaded.current) { loaded.current = true; setF(saved); } }, [saved]);
  const set = (p: Partial<Brand>) => { setF({ ...f, ...p }); setOk(''); };

  const accentOk = (!f.accent || isHex(f.accent)) && (!f.accent2 || isHex(f.accent2)); const navOk = !f.nav || isHex(f.nav);
  const warn = isHex(f.accent) && contrast(f.accent, '#ffffff') < 3 && contrast(f.accent, '#101820') < 3 ? 'This accent colour is hard to read on both light and dark backgrounds.' : '';

  const run = async (fn: () => Promise<Brand>, done: string) => {
    setBusy(true); setErr(''); setOk('');
    try { const b = await fn(); setBrand({ ...DEFAULT_BRAND, ...b }); setF({ ...DEFAULT_BRAND, ...b }); setOk(done); }
    catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const save = () => run(() => api<Brand>('/api/branding', { method: 'PUT', body: { name: f.name, headerText: f.headerText, footerText: f.footerText, accent: f.accent, accent2: f.accent2, nav: f.nav, theme: f.theme } }), 'Saved. Everyone sees the new look after their next page load.');
  const reset = () => { if (confirm('Go back to the standard DeskAsk look? The logo and browser-tab icon are removed too.')) void run(() => api<Brand>('/api/branding/reset', { method: 'POST', body: {} }), 'The standard look is back.'); };
  const removeLogo = () => run(() => api<Brand>('/api/branding/logo', { method: 'DELETE' }), 'The logo is removed.');
  const upload = (file: File | undefined) => {
    if (!file) return;
    if (!TYPES.includes(file.type)) { setErr('Use a PNG, JPEG, WebP, GIF or SVG picture.'); return; }
    if (file.size > MAX) { setErr('The logo is larger than 256 KB. Use a smaller picture.'); return; }
    void run(async () => {
      const r = await fetch('/api/branding/logo', { method: 'PUT', headers: { 'content-type': file.type, authorization: 'Bearer ' + (accessToken() ?? '') }, body: file });
      const t = await r.text(); let j: Record<string, unknown> = {}; try { j = t ? JSON.parse(t) : {}; } catch { /* not json */ }
      if (!r.ok) throw new Error(message(r.status, j));
      return j as unknown as Brand;
    }, 'The logo is saved.');
  };

  const send = (url: string, file: File, type: string) => async () => {
    const r = await fetch(url, { method: 'PUT', headers: { 'content-type': type, authorization: 'Bearer ' + (accessToken() ?? '') }, body: file });
    const t = await r.text(); let j: Record<string, unknown> = {}; try { j = t ? JSON.parse(t) : {}; } catch { /* not json */ }
    if (!r.ok) throw new Error(message(r.status, j));
    return j as unknown as Brand;
  };
  const uploadIcon = (file: File | undefined) => {
    if (!file) return;
    // some browsers give .ico files no type; the name tells
    const type = file.type || (file.name.toLowerCase().endsWith('.ico') ? 'image/x-icon' : '');
    if (!ICON_TYPES.includes(type)) { setErr('Use a PNG, SVG or ICO file for the browser-tab icon.'); return; }
    if (file.size > ICON_MAX) { setErr('The browser-tab icon is larger than 128 KB. Use a smaller picture (32 x 32 or 64 x 64 pixels is enough).'); return; }
    void run(send('/api/branding/favicon', file, type), 'The browser-tab icon is saved. Browsers may show the old one until the page is reloaded.');
  };
  const removeIcon = () => run(() => api<Brand>('/api/branding/favicon', { method: 'DELETE' }), 'The browser-tab icon is removed.');
  const tabIcon = iconUrl(f);

  const pvAccent = isHex(f.accent) ? f.accent : '#0d8a7f'; const pvNav = isHex(f.nav) ? f.nav : '#ffffff'; const pvA2 = isHex(f.accent2) ? f.accent2 : '#5b6bff';
  return <>
    <h1>Branding</h1>
    <p className="muted">How DeskAsk looks for everyone at this customer: the sign-in page and the web app. Only administrators can change it.</p>
    <Err text={err} />{ok ? <p role="status">{ok}</p> : null}
    <div className="brand-page">
      <div>
        <Card title="Name and logo"><div className="form">
          <Field label="Name"><input value={f.name} maxLength={60} onChange={(e) => set({ name: e.target.value })} /></Field>
          <div className="logo-box">
            <div className="logo-prev">{f.hasLogo ? <img src={logoUrl(f)} alt="Current logo" /> : <span className="muted">No logo</span>}</div>
            <div>
              <input ref={file} type="file" accept={TYPES.join(',')} hidden aria-label="Logo file" onChange={(e) => { upload(e.target.files?.[0]); e.target.value = ''; }} />
              <button type="button" disabled={busy} onClick={() => file.current?.click()}>{f.hasLogo ? 'Replace logo' : 'Upload logo'}</button>{' '}
              {f.hasLogo && <button type="button" disabled={busy} onClick={() => void removeLogo()}>Remove</button>}
              <p className="muted" style={{ margin: '6px 0 0' }}>PNG, JPEG, WebP, GIF or SVG, up to 256 KB. A wide logo on a transparent background works best.</p>
            </div>
          </div>
        </div></Card>
        <Card title="Browser-tab icon (favicon)"><div className="form">
          <div className="logo-box">
            <div className="tab-prev" aria-label="Preview of a browser tab">
              <span className="tab-prev-icon">{tabIcon ? <img src={tabIcon} alt="" width={16} height={16} /> : <span className="tab-prev-std" aria-hidden />}</span>
              <span className="tab-prev-title">{f.name || 'DeskAsk'}</span>
            </div>
            <div>
              <input ref={icon} type="file" accept=".ico,.png,.svg,image/png,image/svg+xml,image/x-icon" hidden aria-label="Browser-tab icon file" onChange={(e) => { uploadIcon(e.target.files?.[0]); e.target.value = ''; }} />
              <button type="button" disabled={busy} onClick={() => icon.current?.click()}>{f.hasFavicon ? 'Replace icon' : 'Upload icon'}</button>{' '}
              {f.hasFavicon && <button type="button" disabled={busy} onClick={() => void removeIcon()}>Remove</button>}
              <p className="muted" style={{ margin: '6px 0 0' }}>PNG, SVG or ICO, square, up to 128 KB. Shown in the browser tab, bookmarks and the sign-in page. {f.hasFavicon ? '' : f.hasLogo ? 'Until you upload one, the logo is used.' : 'Until you upload one, the standard DeskAsk icon is used.'}</p>
            </div>
          </div>
        </div></Card>
        <Card title="Header and footer"><div className="form">
          <Field label="Header text (shown at the top of every page)"><input value={f.headerText} maxLength={120} onChange={(e) => set({ headerText: e.target.value })} /></Field>
          <Field label="Footer text (shown at the bottom of every page and on the sign-in page)"><textarea rows={2} value={f.footerText} maxLength={300} onChange={(e) => set({ footerText: e.target.value })} /></Field>
        </div></Card>
        <Card title="Colours and theme"><div className="form">
          <div className="fld"><span className="cap">Accent colour (buttons and links)</span>
            <div className="colour-row"><input type="color" aria-label="Accent colour picker" value={isHex(f.accent) ? f.accent : '#0d8a7f'} onChange={(e) => set({ accent: e.target.value })} />
              <input type="text" aria-label="Accent colour" placeholder="#0d8a7f" value={f.accent} maxLength={7} onChange={(e) => set({ accent: e.target.value.trim() })} />
              {f.accent && <button type="button" onClick={() => set({ accent: '' })}>Standard</button>}</div></div>
          {!accentOk && <Err text="The accent colour must be like #0d8a7f." />}{warn && <p className="muted" role="note">{warn}</p>}
          <div className="fld"><span className="cap">Second colour (the short end of the stripe at the top of the menu)</span>
            <div className="colour-row"><input type="color" aria-label="Second colour picker" value={isHex(f.accent2) ? f.accent2 : '#5b6bff'} onChange={(e) => set({ accent2: e.target.value })} />
              <input type="text" aria-label="Second colour" placeholder="#5b6bff" value={f.accent2} maxLength={7} onChange={(e) => set({ accent2: e.target.value.trim() })} />
              {f.accent2 && <button type="button" onClick={() => set({ accent2: '' })}>Standard</button>}</div></div>
          {f.accent2 && !isHex(f.accent2) && <Err text="The second colour must be like #c31528." />}
          <div className="fld"><span className="cap">Menu colour (left side; empty = white)</span>
            <div className="colour-row"><input type="color" aria-label="Menu colour picker" value={isHex(f.nav) ? f.nav : '#ffffff'} onChange={(e) => set({ nav: e.target.value })} />
              <input type="text" aria-label="Menu colour" placeholder="white" value={f.nav} maxLength={7} onChange={(e) => set({ nav: e.target.value.trim() })} />
              {f.nav && <button type="button" onClick={() => set({ nav: '' })}>Standard</button>}</div></div>
          {!navOk && <Err text="The menu colour must be like #0f1c2e." />}
          <Field label="Theme"><select value={f.theme} onChange={(e) => set({ theme: e.target.value })}>
            <option value="auto">Follow each person's computer (light or dark)</option><option value="light">Always light</option><option value="dark">Always dark</option></select></Field>
        </div></Card>
        <p><button type="button" className="primary" disabled={busy || !accentOk || !navOk || !f.name.trim()} onClick={() => void save()}>Save</button>{' '}
          <button type="button" disabled={busy} onClick={reset}>Reset to standard</button></p>
      </div>
      <div className="preview" aria-label="Preview">
        <div style={{ height: 5, background: `linear-gradient(90deg, ${pvAccent} 0 72%, ${pvA2} 72% 100%)` }} />
        <div className="p-nav" style={{ background: pvNav, color: inkOn(pvNav) }}>{f.hasLogo ? <span className="logo-chip"><img src={logoUrl(f)} alt="" /></span> : null}<span>{f.name || 'DeskAsk'}</span></div>
        <div className="p-top"><span>Home</span>{f.headerText && <span className="muted">{f.headerText}</span>}</div>
        <div className="p-body"><span>Preview</span><button type="button" style={{ background: pvAccent, color: inkOn(pvAccent), justifySelf: 'start' }} tabIndex={-1}>Button</button></div>
        {f.footerText && <div className="p-foot">{f.footerText}</div>}
      </div>
    </div>
    <Domains />
    <PdfDesign url="/api/branding/pdf" title="Reports and exports (PDF)" intro="The design of the PDFs this installation makes: service and uptime reports and asset exports. Save, then download any report to see it. 'Use the standard design' is the logo's teal with black letters and borders; press Save design to keep it." />
  </>;
}
