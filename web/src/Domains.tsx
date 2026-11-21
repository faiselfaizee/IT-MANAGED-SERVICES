import { useState } from 'react';
import { api, useLoad, type Rec } from './http';
import { Card, Empty, Err, Loading, useSubmit } from './ui';

/** Administration › Branding › Your own web address: desk.example.com instead of the built-in address. */
export default function Domains() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/platform/domains');
  const [host, setHost] = useState(''); const [msg, setMsg] = useState('');
  const add = useSubmit(() => { setHost(''); reload(); });
  const items = (data?.items ?? []) as Rec[];
  const act = async (fn: () => Promise<unknown>, done = '') => { setMsg(''); try { const r = await fn(); if (done) setMsg(done); else if (r && (r as Rec).detail) setMsg(String((r as Rec).detail)); reload(); } catch (e) { setMsg((e as Error).message); } };

  return <Card title="Your own web address">
    <p className="muted">Let people open DeskAsk at your own address, such as desk.example.com. The built-in address{data?.builtIn ? ` (${data.builtIn})` : ''} keeps working. The certificate is issued automatically once the address is checked.</p>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No address of your own yet.</Empty> : items.map((d) => <div key={d.host} className="card" style={{ marginBottom: 8 }}>
      <p><strong>{d.host}</strong> {d.verified ? <span className="pill ok">Checked</span> : <span className="pill">Not checked yet</span>}{d.primary && <> <span className="pill">Main address</span></>}</p>
      {!d.verified && <>
        <p className="muted">At your DNS provider, add these two records, wait a few minutes, then press Check.</p>
        <table><tbody>
          <tr><td>TXT</td><td><code>{d.txtName}</code></td><td><code>{d.txtValue}</code></td></tr>
          <tr><td>CNAME</td><td><code>{d.host}</code></td><td><code>{d.cname}</code></td></tr>
        </tbody></table></>}
      <p>
        {!d.verified && <button type="button" className="primary" onClick={() => void act(() => api<Rec>(`/api/platform/domains/${encodeURIComponent(d.host)}/verify`, { method: 'POST', body: {} }))}>Check</button>}{' '}
        {d.verified && <button type="button" onClick={() => void act(() => api(`/api/platform/domains/${encodeURIComponent(d.host)}/primary`, { method: 'POST', body: { primary: !d.primary } }), d.primary ? 'Links now use the built-in address.' : 'Links in emails now use this address.')}>{d.primary ? 'Use the built-in address in links' : 'Use in links and emails'}</button>}{' '}
        <button type="button" onClick={() => { if (confirm(`Remove ${d.host}? People can no longer open DeskAsk at it.`)) void act(() => api(`/api/platform/domains/${encodeURIComponent(d.host)}`, { method: 'DELETE' }), 'The address is removed.'); }}>Remove</button>
      </p></div>))}
    {msg && <p role="status">{msg}</p>}
    <form onSubmit={(e) => { e.preventDefault(); void add.submit(() => api('/api/platform/domains', { body: { host: host.trim() } })); }} className="tools">
      <input aria-label="New web address" placeholder="desk.example.com" value={host} onChange={(e) => setHost(e.target.value)} />
      <button type="submit" disabled={add.busy || !host.trim()}>Add address</button></form>
    <Err text={add.err} />
  </Card>;
}
