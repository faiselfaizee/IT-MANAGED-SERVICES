import { useEffect, useRef, useState } from 'react';
import { accessToken } from './auth';
import { DEFAULT_BRAND, contrast, inkOn, isHex, logoUrl, setBrand, useBrand, type Brand } from './brand';
import { api, message } from './http';
import { Card, Err, Field } from './ui';

const TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif', 'image/svg+xml'];
const MAX = 256 * 1024;

/** Administration › Branding: the logo, name, header and footer text, colours and theme of this customer's DeskAsk. */
export default function Branding() {
  const saved = useBrand();
  const [f, setF] = useState<Brand>(saved); const [err, setErr] = useState(''); const [ok, setOk] = useState(''); const [busy, setBusy] = useState(false);
  const file = useRef<HTMLInputElement>(null); const loaded = useRef(false);
  useEffect(() => { if (!loaded.current) { loaded.current = true; setF(saved); } }, [saved]);
  const set = (p: Partial<Brand>) => { setF({ ...f, ...p }); setOk(''); };

  const accentOk = !f.accent || isHex(f.accent); const navOk = !f.nav || isHex(f.nav);
  const warn = isHex(f.accent) && contrast(f.accent, '#ffffff') < 3 && contrast(f.accent, '#101820') < 3 ? 'This accent colour is hard to read on both light and dark backgrounds.' : '';

  const run = async (fn: () => Promise<Brand>, done: string) => {
    setBusy(true); setErr(''); setOk('');
    try { const b = await fn(); setBrand({ ...DEFAULT_BRAND, ...b }); setF({ ...DEFAULT_BRAND, ...b }); setOk(done); }
    catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const save = () => run(() => api<Brand>('/api/branding', { method: 'PUT', body: { name: f.name, headerText: f.headerText, footerText: f.footerText, accent: f.accent, nav: f.nav, theme: f.theme } }), 'Saved. Everyone sees the new look after their next page load.');
  const reset = () => { if (confirm('Go back to the standard DeskAsk look? The logo is removed too.')) void run(() => api<Brand>('/api/branding/reset', { method: 'POST', body: {} }), 'The standard look is back.'); };
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

  const pvAccent = isHex(f.accent) ? f.accent : '#0d8a7f'; const pvNav = isHex(f.nav) ? f.nav : '#0f1c2e';
  return <>
    <h1>Branding</h1>
    <p className="muted">How DeskAsk looks for everyone at this customer: the sign-in page and the web app. Only administrators can change it.</p>
    <Err text={err} />{ok ? <p role="status">{ok}</p> : null}
    <div className="brand-page">
      <div>
        <Card title="Name and logo">
          <Field label="Name"><input value={f.name} maxLength={60} onChange={(e) => set({ name: e.target.value })} /></Field>
          <div className="logo-box" style={{ marginTop: 10 }}>
            <div className="logo-prev">{f.hasLogo ? <img src={logoUrl(f)} alt="Current logo" /> : <span className="muted">No logo</span>}</div>
            <div>
              <input ref={file} type="file" accept={TYPES.join(',')} hidden aria-label="Logo file" onChange={(e) => { upload(e.target.files?.[0]); e.target.value = ''; }} />
              <button type="button" disabled={busy} onClick={() => file.current?.click()}>{f.hasLogo ? 'Replace logo' : 'Upload logo'}</button>{' '}
              {f.hasLogo && <button type="button" disabled={busy} onClick={() => void removeLogo()}>Remove</button>}
              <p className="muted" style={{ margin: '6px 0 0' }}>PNG, JPEG, WebP, GIF or SVG, up to 256 KB. A wide logo on a transparent background works best.</p>
            </div>
          </div>
        </Card>
        <Card title="Header and footer">
          <Field label="Header text (shown at the top of every page)"><input value={f.headerText} maxLength={120} onChange={(e) => set({ headerText: e.target.value })} /></Field>
          <Field label="Footer text (shown at the bottom of every page and on the sign-in page)"><textarea rows={2} value={f.footerText} maxLength={300} onChange={(e) => set({ footerText: e.target.value })} /></Field>
        </Card>
        <Card title="Colours and theme">
          <div className="swatch"><Field label="Accent colour (buttons and links)">
            <span className="swatch"><input type="color" aria-label="Accent colour picker" value={isHex(f.accent) ? f.accent : '#0d8a7f'} onChange={(e) => set({ accent: e.target.value })} />
              <input aria-label="Accent colour" placeholder="#0d8a7f" value={f.accent} maxLength={7} onChange={(e) => set({ accent: e.target.value.trim() })} />
              {f.accent && <button type="button" onClick={() => set({ accent: '' })}>Standard</button>}</span></Field></div>
          {!accentOk && <Err text="The accent colour must be like #0d8a7f." />}{warn && <p className="muted" role="note">{warn}</p>}
          <div className="swatch" style={{ marginTop: 10 }}><Field label="Menu colour (left side)">
            <span className="swatch"><input type="color" aria-label="Menu colour picker" value={isHex(f.nav) ? f.nav : '#0f1c2e'} onChange={(e) => set({ nav: e.target.value })} />
              <input aria-label="Menu colour" placeholder="#0f1c2e" value={f.nav} maxLength={7} onChange={(e) => set({ nav: e.target.value.trim() })} />
              {f.nav && <button type="button" onClick={() => set({ nav: '' })}>Standard</button>}</span></Field></div>
          {!navOk && <Err text="The menu colour must be like #0f1c2e." />}
          <Field label="Theme"><select value={f.theme} onChange={(e) => set({ theme: e.target.value })}>
            <option value="auto">Follow each person's computer (light or dark)</option><option value="light">Always light</option><option value="dark">Always dark</option></select></Field>
        </Card>
        <p><button type="button" className="primary" disabled={busy || !accentOk || !navOk || !f.name.trim()} onClick={() => void save()}>Save</button>{' '}
          <button type="button" disabled={busy} onClick={reset}>Reset to standard</button></p>
      </div>
      <div className="preview" aria-label="Preview">
        <div className="p-nav" style={{ background: pvNav }}>{f.hasLogo ? <span className="logo-chip"><img src={logoUrl(f)} alt="" /></span> : null}<span>{f.name || 'DeskAsk'}</span></div>
        <div className="p-top"><span>Home</span>{f.headerText && <span className="muted">{f.headerText}</span>}</div>
        <div className="p-body"><span>Preview</span><button type="button" style={{ background: pvAccent, color: inkOn(pvAccent), justifySelf: 'start' }} tabIndex={-1}>Button</button></div>
        {f.footerText && <div className="p-foot">{f.footerText}</div>}
      </div>
    </div>
  </>;
}
