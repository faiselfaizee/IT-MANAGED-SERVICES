import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { ago, copyText, Empty, fmtDate, Loading } from '../ui';
import * as C from './crypto';

/** Makes a send link: the text is encrypted here with a key that only exists in the link's # part. */
export async function makeSend(text: string, o: { label: string; hours: number; maxViews: number; passcode?: string }): Promise<string> {
  const frag = C.random(32);
  const key = await C.sendKey(frag, o.passcode || undefined);
  const r = await api<Rec>('/api/vault/sends', { method: 'POST', body: { dataEnc: await C.encryptJson(key, { text }), label: o.label, hours: o.hours, maxViews: o.maxViews, passcode: !!o.passcode, passcodeCheck: o.passcode ? await C.sendCheck(frag, o.passcode) : undefined } });
  return `${location.origin}/#/send/${r.id}/${C.b64u(frag)}`;
}

export function SendForm({ initial = '', onMade }: { initial?: string; onMade?: () => void }) {
  const [text, setText] = useState(initial); const [label, setLabel] = useState(''); const [hours, setHours] = useState(24); const [views, setViews] = useState(1); const [pass, setPass] = useState('');
  const [link, setLink] = useState(''); const [err, setErr] = useState('');
  if (link) return <div className="form">
    <p>Send this link. It works {views === 1 ? 'once' : `${views} times`} and for {hours < 24 ? `${hours} hours` : `${hours / 24} days`}.</p>
    <p className="link-out"><code>{link}</code></p>
    <div className="tools"><button className="primary" onClick={() => copyText(link)}>Copy the link</button><button onClick={() => { setLink(''); setText(''); setPass(''); }}>Make another</button></div>
    {pass && <p className="note">Tell them the passcode another way (by phone, or in a different app). The link alone does not open it.</p>}
    <p className="muted small">The secret part of the link (after the last /) never reaches the server. Anyone who has the link{pass ? ' and the passcode' : ''} can open it, so send it only to the person it is for.</p>
  </div>;
  return <form className="form" onSubmit={async (e) => { e.preventDefault(); setErr(''); try { setLink(await makeSend(text, { label, hours, maxViews: views, passcode: pass })); onMade?.(); } catch (x) { setErr((x as Error).message); } }}>
    <label><span>What to send</span><textarea rows={5} value={text} onChange={(e) => setText(e.target.value)} placeholder="User name and password for the new starter, a Wi-Fi key, a licence key…" spellCheck={false} /></label>
    <label><span>Label for your list (not secret, not shown to them)</span><input value={label} onChange={(e) => setLabel(e.target.value)} placeholder="New starter at Acme" /></label>
    <div className="row">
      <label><span>Works for</span><select value={hours} onChange={(e) => setHours(Number(e.target.value))}>{[1, 4, 24, 72, 168, 720].map((h) => <option key={h} value={h}>{h < 24 ? `${h} hour${h > 1 ? 's' : ''}` : `${h / 24} day${h > 24 ? 's' : ''}`}</option>)}</select></label>
      <label><span>Opens</span><select value={views} onChange={(e) => setViews(Number(e.target.value))}>{[1, 2, 3, 5, 10].map((v) => <option key={v} value={v}>{v === 1 ? 'once' : `${v} times`}</option>)}</select></label>
      <label><span>Passcode (optional)</span><input value={pass} onChange={(e) => setPass(e.target.value)} placeholder="Told separately" autoComplete="off" /></label>
    </div>
    {err && <p className="err" role="alert">{err}</p>}
    <div className="tools"><button className="primary" disabled={!text.trim()}>Make the link</button></div>
  </form>;
}

export default function Sends() {
  const list = useLoad<Rec>('/api/vault/sends');
  const items = (list.data?.items ?? []) as Rec[];
  return <>
    <h1>Send links</h1>
    <p className="muted">For someone outside DeskAsk: a customer's new starter, a contractor, a supplier. The link opens a page that shows the text once, then stops working.</p>
    <div className="grid">
      <section className="card"><h2>New link</h2><SendForm onMade={list.reload} /></section>
      <section className="card"><h2>Your links</h2>
        <Loading loading={list.loading && !list.data} error={list.error} />
        {list.data && (items.length === 0 ? <Empty>No links yet.</Empty> : <div className="table-wrap"><table><thead><tr><th>Label</th><th>Made</th><th>Opened</th><th>State</th><th /></tr></thead>
          <tbody>{items.map((s) => <tr key={s.id}><td>{s.label || <span className="muted">No label</span>}<div className="muted small">{s.createdBy}{s.passcode ? ' · with passcode' : ''}</div></td><td title={fmtDate(s.createdAt)}>{ago(s.createdAt)}</td><td>{s.views} of {s.maxViews}</td>
            <td><span className={'badge ' + (s.active ? 'ok' : '')}>{s.active ? `Works until ${fmtDate(s.expiresAt)}` : s.deletedAt ? 'Deleted' : s.views >= s.maxViews ? 'Used up' : 'Expired'}</span></td>
            <td>{s.active && <button className="small" onClick={async () => { await api(`/api/vault/sends/${s.id}`, { method: 'DELETE' }); list.reload(); }}>Stop it now</button>}</td></tr>)}</tbody></table></div>)}
      </section>
    </div>
  </>;
}
