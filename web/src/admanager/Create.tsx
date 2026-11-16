import { useState } from 'react';
import { type Rec } from '../http';
import { Card, Err, Field, useSubmit } from '../ui';
import { OuPicker, ouPath } from './pickers';
import { act } from './Objects';

/** A new group (with its scope and type) or a new OU. */
export function CreateOther({ domain }: { domain: Rec }) {
  const [what, setWhat] = useState<'group' | 'ou'>('group'); const [ou, setOu] = useState(''); const [name, setName] = useState(''); const [sam, setSam] = useState('');
  const [desc, setDesc] = useState(''); const [scope, setScope] = useState('global'); const [type, setType] = useState('security'); const [done, setDone] = useState('');
  const f = useSubmit(() => undefined);
  return <Card>
    <div className="tabs" role="tablist">{(['group', 'ou'] as const).map((k) => <button key={k} role="tab" aria-selected={what === k} className={what === k ? 'on' : ''} onClick={() => { setWhat(k); setDone(''); }}>{k === 'group' ? 'Group' : 'Organisational unit'}</button>)}</div>
    <div className="form">
      <div><span className="lbl">Create in</span><OuPicker domain={domain} value={ou} onChange={setOu} allowDomain={what === 'ou'} /></div>
      <Field label="Name"><input value={name} onChange={(e) => { setName(e.target.value); if (what === 'group') setSam(e.target.value.slice(0, 64)); }} /></Field>
      {what === 'group' && <>
        <Field label="Group name (pre-Windows 2000)"><input value={sam} maxLength={64} onChange={(e) => setSam(e.target.value)} /></Field>
        <fieldset><legend>Scope</legend>{[['domainlocal', 'Domain local'], ['global', 'Global'], ['universal', 'Universal']].map(([k, l]) => <label key={k} className="check"><input type="radio" checked={scope === k} onChange={() => setScope(k)} /> {l}</label>)}</fieldset>
        <fieldset><legend>Type</legend>{[['security', 'Security'], ['distribution', 'Distribution']].map(([k, l]) => <label key={k} className="check"><input type="radio" checked={type === k} onChange={() => setType(k)} /> {l}</label>)}</fieldset>
      </>}
      <Field label="Description"><input value={desc} onChange={(e) => setDesc(e.target.value)} /></Field>
      <Err text={f.err} />
      {done && <p className="ok">Created <code>{done}</code>.</p>}
      <div className="actions"><button className="primary" disabled={f.busy || !ou || !name} onClick={() => void f.submit(async () => {
        const r = await act(domain.id, what === 'group' ? { action: 'create-group', ou, name, sam, description: desc, scope, type } : { action: 'create-ou', ou, name, description: desc });
        setDone(ouPath(String(r.dn), domain.baseDn)); setName(''); setSam(''); setDesc('');
      })}>Create</button></div>
    </div>
  </Card>;
}
