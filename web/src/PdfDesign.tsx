import { useEffect, useState } from 'react';
import { api, saveFile, type Rec } from './http';
import { Card, Err, Field } from './ui';

/** The standard look of every PDF: the logo's teal header band with white letters, black borders and text. */
export const PDF_STANDARD = { headerColor: '#207178', headerText: '#FFFFFF', border: '#000000', text: '#000000', accent: '#207178', footer: '', note: '' };
const isHex = (s: string) => /^#[0-9a-fA-F]{6}$/.test(s);

function Colour({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return <div className="pdf-colour"><span>{label}</span>
    <span className="pdf-colour-row"><input type="color" value={isHex(value) ? value : '#000000'} onChange={(e) => onChange(e.target.value.toUpperCase())} aria-label={label + ' picker'} />
      <input value={value} maxLength={7} onChange={(e) => onChange(e.target.value)} aria-label={label} aria-invalid={!isHex(value)} style={{ width: 110 }} /></span></div>;
}

/**
 * The editable template of the PDFs: colours of the table header band, its letters, borders, text and headings, a line at the foot of every page and
 * (for invoices) the closing note. `url` reads (GET) and saves (PUT) the look; `previewUrl` downloads a sample invoice made with the saved look.
 */
export default function PdfDesign({ url, title, intro, withNote = false, previewUrl }: { url: string; title: string; intro: string; withNote?: boolean; previewUrl?: string }) {
  const [f, setF] = useState<Rec>(PDF_STANDARD); const [err, setErr] = useState(''); const [ok, setOk] = useState(''); const [busy, setBusy] = useState(false);
  useEffect(() => { api<Rec>(url).then((d) => setF({ ...PDF_STANDARD, ...d }), (e: Error) => setErr(e.message)); }, [url]);
  const set = (p: Rec) => { setF({ ...f, ...p }); setOk(''); };
  const valid = ['headerColor', 'headerText', 'border', 'text', 'accent'].every((k) => isHex(String(f[k])));
  const save = async () => {
    setBusy(true); setErr(''); setOk('');
    try { const d = await api<Rec>(url, { method: 'PUT', body: f }); setF({ ...PDF_STANDARD, ...d }); setOk('Saved. New PDFs use this design.'); }
    catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const cell = { border: `1px solid ${f.border}`, padding: '5px 8px', color: String(f.text) } as const;
  return <Card title={title}>
    <p className="muted">{intro}</p>
    <div className="pdf-design">
      <div className="pdf-fields">
        <Colour label="Table header colour" value={String(f.headerColor)} onChange={(v) => set({ headerColor: v })} />
        <Colour label="Table header letters" value={String(f.headerText)} onChange={(v) => set({ headerText: v })} />
        <Colour label="Borders" value={String(f.border)} onChange={(v) => set({ border: v })} />
        <Colour label="Letters" value={String(f.text)} onChange={(v) => set({ text: v })} />
        <Colour label="Headings and lines" value={String(f.accent)} onChange={(v) => set({ accent: v })} />
        <Field label="Line at the foot of every page" hint="For example your company name and website."><input value={String(f.footer ?? '')} maxLength={120} onChange={(e) => set({ footer: e.target.value })} /></Field>
        {withNote && <Field label="Closing note of the invoice" hint="Payment terms, bank details or thanks. Empty uses the standard wording."><textarea rows={4} maxLength={600} value={String(f.note ?? '')} onChange={(e) => set({ note: e.target.value })} /></Field>}
      </div>
      <div className="pdf-sample" aria-label="Preview">
        <h3 style={{ color: String(f.accent), borderBottom: `2px solid ${f.accent}`, margin: '0 0 8px', paddingBottom: 4 }}>Tax Invoice</h3>
        <table style={{ borderCollapse: 'collapse', width: '100%', fontSize: 12, background: '#fff' }}><thead><tr>
          {['#', 'Description', 'Amount'].map((h) => <th key={h} style={{ ...cell, background: String(f.headerColor), color: String(f.headerText), textAlign: 'left' }}>{h}</th>)}</tr></thead>
          <tbody><tr><td style={cell}>1</td><td style={cell}>Service Desk, 10 agents</td><td style={cell}>12,000.00</td></tr><tr><td style={cell}></td><td style={{ ...cell, fontWeight: 700 }}>Total</td><td style={{ ...cell, fontWeight: 700 }}>12,000.00</td></tr></tbody></table>
        {String(f.footer ?? '') !== '' && <p style={{ textAlign: 'right', fontSize: 11, margin: '8px 0 0', color: '#444' }}>{String(f.footer)}</p>}
      </div>
    </div>
    <Err text={err} />
    <div className="tools">
      <button className="primary" disabled={busy || !valid} onClick={save}>Save design</button>
      <button type="button" disabled={busy} onClick={() => { setF({ ...PDF_STANDARD }); setOk(''); }}>Use the standard design</button>
      {previewUrl && <button type="button" disabled={busy} onClick={() => void saveFile(previewUrl, 'sample-invoice.pdf').catch((e: Error) => setErr(e.message))}>Download a sample invoice</button>}
      {ok && <span className="muted" role="status">{ok}</span>}
    </div>
    {!valid && <p className="muted">Colours look like #207178.</p>}
  </Card>;
}
