import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Card, Empty, Err, Field, useSubmit } from '../ui';

export const FIELD_KIND: Record<string, string> = { text: 'Short text', longtext: 'Long text', number: 'Number', date: 'Date', choice: 'Choice from a list', checkbox: 'Yes or no' };

/** The extra fields an administrator added to tickets (only the ones this person may see). */
export const useFields = () => {
  const r = useLoad<Rec>('/api/servicedesk/fields');
  return (Array.isArray(r.data?.items) ? r.data!.items : []) as Rec[];
};

/** One input for one field. An empty value means "not filled in". */
export function FieldInput({ def, value, onChange }: { def: Rec; value: unknown; onChange: (v: unknown) => void }) {
  const label = def.label + (def.staffOnly ? ' (team only)' : '');
  const text = value === undefined || value === null ? '' : String(value);
  return <Field label={label + (def.required ? ' *' : '')}>
    {def.kind === 'longtext' ? <textarea rows={3} value={text} onChange={(e) => onChange(e.target.value)} />
      : def.kind === 'choice' ? <select value={text} onChange={(e) => onChange(e.target.value)}><option value="">Not set</option>{(def.options as string[]).map((o) => <option key={o}>{o}</option>)}</select>
      : def.kind === 'checkbox' ? <input type="checkbox" checked={value === true} onChange={(e) => onChange(e.target.checked)} />
      : def.kind === 'number' ? <input type="number" value={text} onChange={(e) => onChange(e.target.value === '' ? '' : Number(e.target.value))} />
      : def.kind === 'date' ? <input type="date" value={text} onChange={(e) => onChange(e.target.value)} />
      : <input value={text} maxLength={500} onChange={(e) => onChange(e.target.value)} />}
  </Field>;
}

/** Values to send: only fields that have something in them (checkboxes always count). */
export const sendable = (defs: Rec[], values: Rec): Rec => {
  const o: Rec = {};
  for (const d of defs) { const v = values[d.key]; if (v === undefined || v === '' || v === null) continue; if (d.kind === 'checkbox' && v === false) continue; o[d.key] = v; }
  return o;
};
export const missingRequired = (defs: Rec[], values: Rec, staff: boolean) => defs.find((d) => d.required && (staff || !d.staffOnly) && (values[d.key] === undefined || values[d.key] === '' || values[d.key] === null || (d.kind === 'checkbox' && values[d.key] === false)));

/** The ticket screen's card: staff change values (saved when they leave the box), everyone else just reads them. */
export function CustomCard({ defs, values, edit, save }: { defs: Rec[]; values: Rec; edit: boolean; save: (b: Rec) => Promise<unknown> }) {
  const [draft, setDraft] = useState<Rec>({}); const f = useSubmit(() => undefined);
  const shown = edit ? defs : defs.filter((d) => values[d.key] !== undefined && values[d.key] !== null && values[d.key] !== '');
  if (shown.length === 0) return null;
  const now = (d: Rec) => (d.key in draft ? draft[d.key] : values[d.key]);
  const commit = (d: Rec) => { if (!(d.key in draft)) return; const v = draft[d.key]; const next = { ...draft }; delete next[d.key]; setDraft(next); f.submit(() => save({ custom: { [d.key]: v === '' ? null : v } })); };
  return <Card title="Extra details">
    {edit ? <div className="form">{shown.map((d) => <div key={d.key} onBlur={() => commit(d)}><FieldInput def={d} value={now(d)} onChange={(v) => { setDraft({ ...draft, [d.key]: v }); if (d.kind === 'checkbox' || d.kind === 'choice') { f.submit(() => save({ custom: { [d.key]: v === '' ? null : v } })); const n = { ...draft }; delete n[d.key]; setDraft(n); } }} /></div>)}<Err text={f.err} /></div>
      : <dl className="dl">{shown.map((d) => <div key={d.key}><dt>{d.label}</dt><dd>{d.kind === 'checkbox' ? (values[d.key] ? 'Yes' : 'No') : String(values[d.key])}</dd></div>)}</dl>}
  </Card>;
}

/** Settings: add, rename, reorder (by order of creation), switch off and delete fields. */
export function FieldsAdmin() {
  const { data, error, reload } = useLoad<Rec>('/api/servicedesk/fields?all=true');
  const items = (Array.isArray(data?.items) ? data!.items : []) as Rec[];
  const [v, setV] = useState<Rec>({ label: '', kind: 'text', options: '', required: false, staffOnly: false });
  const f = useSubmit(() => { setV({ label: '', kind: 'text', options: '', required: false, staffOnly: false }); reload(); });
  const upd = (d: Rec, b: Rec) => f.submit(() => api('/api/servicedesk/fields/' + d.id, { method: 'PUT', body: b }));
  return <Card title="Extra ticket fields">
    <p className="muted">Add your own questions to every ticket, such as a site code or a cost centre. A required field must be filled in when a ticket is opened. A team-only field is hidden from customers.</p>
    {error ? <Err text={error} /> : items.length === 0 ? <Empty>No extra fields yet.</Empty> : <table><thead><tr><th>Name</th><th>Type</th><th>Required</th><th>Team only</th><th>In use</th><th /></tr></thead><tbody>
      {items.map((d) => <tr key={d.id}><td>{d.label}{d.kind === 'choice' && <div className="muted">{(d.options as string[]).join(', ')}</div>}</td><td>{FIELD_KIND[d.kind]}</td>
        <td><input type="checkbox" checked={d.required} aria-label={'Required: ' + d.label} onChange={(e) => upd(d, { required: e.target.checked })} /></td>
        <td><input type="checkbox" checked={d.staffOnly} aria-label={'Team only: ' + d.label} onChange={(e) => upd(d, { staffOnly: e.target.checked })} /></td>
        <td><input type="checkbox" checked={d.active} aria-label={'In use: ' + d.label} onChange={(e) => upd(d, { active: e.target.checked })} /></td>
        <td style={{ textAlign: 'right' }}><button onClick={() => { if (confirm('Delete "' + d.label + '"? Values already saved on tickets stay hidden.')) f.submit(() => api('/api/servicedesk/fields/' + d.id, { method: 'DELETE' })); }}>Delete</button></td></tr>)}
    </tbody></table>}
    <div className="form" style={{ marginTop: 12 }}>
      <div className="row">
        <Field label="New field name"><input value={v.label} maxLength={80} onChange={(e) => setV({ ...v, label: e.target.value })} /></Field>
        <Field label="Type"><select value={v.kind} onChange={(e) => setV({ ...v, kind: e.target.value })}>{Object.entries(FIELD_KIND).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
      </div>
      {v.kind === 'choice' && <Field label="Choices (one per line)"><textarea rows={3} value={v.options} onChange={(e) => setV({ ...v, options: e.target.value })} /></Field>}
      <label><input type="checkbox" checked={v.required} onChange={(e) => setV({ ...v, required: e.target.checked })} /> Required</label>
      <label><input type="checkbox" checked={v.staffOnly} onChange={(e) => setV({ ...v, staffOnly: e.target.checked })} /> Team only</label>
      <Err text={f.err} />
      <div className="actions"><button className="primary" disabled={f.busy || !v.label.trim()} onClick={() => f.submit(() => api('/api/servicedesk/fields', { body: v }))}>Add field</button></div>
    </div>
  </Card>;
}
