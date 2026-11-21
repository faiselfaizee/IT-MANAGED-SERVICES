import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { api, saveFile } from './http';
import { Card, Err, Field } from './ui';
/** The standard look of every PDF: the logo's teal header band with white letters, black borders and text. */
export const PDF_STANDARD = { headerColor: '#207178', headerText: '#FFFFFF', border: '#000000', text: '#000000', accent: '#207178', footer: '', note: '' };
const isHex = (s) => /^#[0-9a-fA-F]{6}$/.test(s);
function Colour({ label, value, onChange }) {
    return _jsxs("div", { className: "pdf-colour", children: [_jsx("span", { children: label }), _jsxs("span", { className: "pdf-colour-row", children: [_jsx("input", { type: "color", value: isHex(value) ? value : '#000000', onChange: (e) => onChange(e.target.value.toUpperCase()), "aria-label": label + ' picker' }), _jsx("input", { value: value, maxLength: 7, onChange: (e) => onChange(e.target.value), "aria-label": label, "aria-invalid": !isHex(value), style: { width: 110 } })] })] });
}
/**
 * The editable template of the PDFs: colours of the table header band, its letters, borders, text and headings, a line at the foot of every page and
 * (for invoices) the closing note. `url` reads (GET) and saves (PUT) the look; `previewUrl` downloads a sample invoice made with the saved look.
 */
export default function PdfDesign({ url, title, intro, withNote = false, previewUrl }) {
    const [f, setF] = useState(PDF_STANDARD);
    const [err, setErr] = useState('');
    const [ok, setOk] = useState('');
    const [busy, setBusy] = useState(false);
    useEffect(() => { api(url).then((d) => setF({ ...PDF_STANDARD, ...d }), (e) => setErr(e.message)); }, [url]);
    const set = (p) => { setF({ ...f, ...p }); setOk(''); };
    const valid = ['headerColor', 'headerText', 'border', 'text', 'accent'].every((k) => isHex(String(f[k])));
    const save = async () => {
        setBusy(true);
        setErr('');
        setOk('');
        try {
            const d = await api(url, { method: 'PUT', body: f });
            setF({ ...PDF_STANDARD, ...d });
            setOk('Saved. New PDFs use this design.');
        }
        catch (e) {
            setErr(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    const cell = { border: `1px solid ${f.border}`, padding: '5px 8px', color: String(f.text) };
    return _jsxs(Card, { title: title, children: [_jsx("p", { className: "muted", children: intro }), _jsxs("div", { className: "pdf-design", children: [_jsxs("div", { className: "pdf-fields", children: [_jsx(Colour, { label: "Table header colour", value: String(f.headerColor), onChange: (v) => set({ headerColor: v }) }), _jsx(Colour, { label: "Table header letters", value: String(f.headerText), onChange: (v) => set({ headerText: v }) }), _jsx(Colour, { label: "Borders", value: String(f.border), onChange: (v) => set({ border: v }) }), _jsx(Colour, { label: "Letters", value: String(f.text), onChange: (v) => set({ text: v }) }), _jsx(Colour, { label: "Headings and lines", value: String(f.accent), onChange: (v) => set({ accent: v }) }), _jsx(Field, { label: "Line at the foot of every page", hint: "For example your company name and website.", children: _jsx("input", { value: String(f.footer ?? ''), maxLength: 120, onChange: (e) => set({ footer: e.target.value }) }) }), withNote && _jsx(Field, { label: "Closing note of the invoice", hint: "Payment terms, bank details or thanks. Empty uses the standard wording.", children: _jsx("textarea", { rows: 4, maxLength: 600, value: String(f.note ?? ''), onChange: (e) => set({ note: e.target.value }) }) })] }), _jsxs("div", { className: "pdf-sample", "aria-label": "Preview", children: [_jsx("h3", { style: { color: String(f.accent), borderBottom: `2px solid ${f.accent}`, margin: '0 0 8px', paddingBottom: 4 }, children: "Tax Invoice" }), _jsxs("table", { style: { borderCollapse: 'collapse', width: '100%', fontSize: 12, background: '#fff' }, children: [_jsx("thead", { children: _jsx("tr", { children: ['#', 'Description', 'Amount'].map((h) => _jsx("th", { style: { ...cell, background: String(f.headerColor), color: String(f.headerText), textAlign: 'left' }, children: h }, h)) }) }), _jsxs("tbody", { children: [_jsxs("tr", { children: [_jsx("td", { style: cell, children: "1" }), _jsx("td", { style: cell, children: "Service Desk, 10 agents" }), _jsx("td", { style: cell, children: "12,000.00" })] }), _jsxs("tr", { children: [_jsx("td", { style: cell }), _jsx("td", { style: { ...cell, fontWeight: 700 }, children: "Total" }), _jsx("td", { style: { ...cell, fontWeight: 700 }, children: "12,000.00" })] })] })] }), String(f.footer ?? '') !== '' && _jsx("p", { style: { textAlign: 'right', fontSize: 11, margin: '8px 0 0', color: '#444' }, children: String(f.footer) })] })] }), _jsx(Err, { text: err }), _jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", disabled: busy || !valid, onClick: save, children: "Save design" }), _jsx("button", { type: "button", disabled: busy, onClick: () => { setF({ ...PDF_STANDARD }); setOk(''); }, children: "Use the standard design" }), previewUrl && _jsx("button", { type: "button", disabled: busy, onClick: () => void saveFile(previewUrl, 'sample-invoice.pdf').catch((e) => setErr(e.message)), children: "Download a sample invoice" }), ok && _jsx("span", { className: "muted", role: "status", children: ok })] }), !valid && _jsx("p", { className: "muted", children: "Colours look like #207178." })] });
}
