import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Empty, Err, Field, Loading, Modal, useSubmit } from '../ui';

const blank = { name: '', code: '', contactName: '', contactEmail: '', phone: '', address: '', notes: '', status: 'active', remoteAllowed: false };

export default function Companies() {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/assets/companies'); const [edit, setEdit] = useState<Rec | null>(null); const list = Array.isArray(data) ? data : [];
  return <>
    <h1>Companies</h1>
    <div className="tools"><button className="primary" onClick={() => setEdit({ ...blank })}>Add company</button><span className="muted">Customers whose devices you look after. Remote access to a customer's devices is off until you switch it on.</span></div>
    <Loading loading={loading && !data} error={error} />
    {data && (list.length === 0 ? <Empty>No companies yet.</Empty> : <table><thead><tr><th>Name</th><th>Contact</th><th className="num">Assets</th><th>Remote access</th><th>Status</th><th /></tr></thead><tbody>
      {list.map((c) => <tr key={c.id}><td>{c.name} <span className="muted">{c.code}</span></td><td>{c.contactName}<div className="muted">{c.contactEmail} {c.phone}</div></td><td className="num">{c.assets}</td>
        <td><label><input type="checkbox" checked={c.remoteAllowed} onChange={async (e) => { await api(`/api/assets/companies/${c.id}`, { method: 'PUT', body: { remoteAllowed: e.target.checked } }); reload(); }} /> {c.remoteAllowed ? 'Allowed' : 'Off'}</label></td>
        <td><Badge tone={c.status === 'active' ? 'ok' : undefined}>{c.status}</Badge></td>
        <td><button onClick={() => setEdit(c)}>Edit</button> <Act label="Delete" danger confirm={`Delete ${c.name}?`} disabled={c.assets > 0} run={async () => { await api(`/api/assets/companies/${c.id}`, { method: 'DELETE' }); reload(); }} /></td></tr>)}</tbody></table>)}
    {edit && <Form init={edit} onClose={(ch) => { setEdit(null); if (ch) reload(); }} />}
  </>;
}
function Form({ init, onClose }: { init: Rec; onClose: (changed: boolean) => void }) {
  const [v, setV] = useState<Rec>(init); const f = useSubmit(() => onClose(true)); const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  return <Modal title={init.id ? 'Edit company' : 'Add company'} onClose={() => onClose(false)}><div className="form">
    <div className="row"><Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} autoFocus /></Field><Field label="Code"><input value={v.code} onChange={(e) => set('code', e.target.value)} /></Field></div>
    <div className="row"><Field label="Contact person"><input value={v.contactName} onChange={(e) => set('contactName', e.target.value)} /></Field><Field label="Contact email"><input value={v.contactEmail} onChange={(e) => set('contactEmail', e.target.value)} /></Field></div>
    <div className="row"><Field label="Phone"><input value={v.phone} onChange={(e) => set('phone', e.target.value)} /></Field><Field label="Status"><select value={v.status} onChange={(e) => set('status', e.target.value)}><option>active</option><option>inactive</option></select></Field></div>
    <Field label="Address"><textarea rows={2} value={v.address} onChange={(e) => set('address', e.target.value)} /></Field><Field label="Notes"><textarea rows={2} value={v.notes} onChange={(e) => set('notes', e.target.value)} /></Field>
    <label><input type="checkbox" checked={v.remoteAllowed} onChange={(e) => set('remoteAllowed', e.target.checked)} /> Allow remote commands, shell and remote control on this customer's devices</label>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !v.name.trim()} onClick={() => f.submit(() => api(init.id ? `/api/assets/companies/${init.id}` : '/api/assets/companies', { method: init.id ? 'PUT' : 'POST', body: v }))}>Save</button></div></div></Modal>;
}
