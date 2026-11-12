import { useEffect, useRef, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Copy, Empty, Err, Field, Loading, Modal, ago, fmtDate, useSubmit } from '../ui';

/** API keys and webhooks: how other programs read tickets, open them, or get told about them. */
export default function Integrations() {
  const keys = useLoad<Rec>('/api/servicedesk/integration/keys'); const hooks = useLoad<Rec>('/api/servicedesk/integration/webhooks');
  const [newKey, setNewKey] = useState(false); const [shown, setShown] = useState<Rec | null>(null); const [hook, setHook] = useState<Rec | null | undefined>(undefined); const [log, setLog] = useState<Rec | null>(null);
  return <>
    <h1>Connections</h1>
    <p className="muted">Let other programs work with tickets. Send the key in an <code>X-Api-Key</code> header to <code>/api/v1/tickets</code>.</p>
    <Card title="API keys">
      <div className="tools"><button className="primary" onClick={() => setNewKey(true)}>New API key</button></div>
      <Loading loading={keys.loading && !keys.data} error={keys.error} />
      {shown && <p className="note">Copy this key now, it cannot be shown again: <Copy text={shown.key} /> <button onClick={() => setShown(null)}>Done</button></p>}
      {keys.data && (keys.data.items.length === 0 ? <Empty>No API keys yet.</Empty> : <table><thead><tr><th>Name</th><th>Key</th><th>Can</th><th>Last used</th><th /></tr></thead><tbody>
        {(keys.data.items as Rec[]).map((k) => <tr key={k.id}><td>{k.name}<div className="muted">Created {fmtDate(k.createdAt)} by {k.createdBy}</div></td><td className="mono">{k.prefix}…</td>
          <td>{k.scope === 'write' ? 'Read and open tickets' : 'Read only'}</td><td>{k.revokedAt ? <Badge tone="bad">Revoked</Badge> : k.lastUsedAt ? ago(k.lastUsedAt) : 'Never'}</td>
          <td>{!k.revokedAt && <Act label="Revoke" danger confirm="Programs using this key will stop working. Revoke it?" run={async () => { await api(`/api/servicedesk/integration/keys/${k.id}`, { method: 'DELETE' }); keys.reload(); }} />}</td></tr>)}</tbody></table>)}
    </Card>
    <Card title="Webhooks">
      <p className="muted">DeskAsk sends a signed message to your address when a ticket is opened, changed, answered or noted. Failed messages are tried again a few times.</p>
      <div className="tools"><button className="primary" onClick={() => setHook(null)}>New webhook</button></div>
      <Loading loading={hooks.loading && !hooks.data} error={hooks.error} />
      {hooks.data && (hooks.data.items.length === 0 ? <Empty>No webhooks yet.</Empty> : <table><thead><tr><th>Address</th><th>Events</th><th>Status</th><th /></tr></thead><tbody>
        {(hooks.data.items as Rec[]).map((h) => <tr key={h.id}><td className="mono">{h.url}</td><td>{h.events === '*' ? 'All events' : h.events}</td>
          <td>{h.active ? <Badge tone="ok">On</Badge> : <Badge>Off</Badge>} {h.failed > 0 && <Badge tone="bad">{h.failed} failed</Badge>}<div className="muted">{h.lastSentAt ? 'Last sent ' + ago(h.lastSentAt) : 'Nothing sent yet'}</div></td>
          <td className="actions"><button onClick={() => setHook(h)}>Edit</button><Act label="Send test" run={async () => { await api(`/api/servicedesk/integration/webhooks/${h.id}/test`, { method: 'POST', body: {} }); }} />
            <button onClick={async () => setLog({ hook: h, items: (await api<Rec>(`/api/servicedesk/integration/webhooks/${h.id}/deliveries`)).items })}>Messages</button></td></tr>)}</tbody></table>)}
    </Card>
    <ReportMail />
    {newKey && <KeyForm onClose={(k) => { setNewKey(false); if (k) { setShown(k); keys.reload(); } }} />}
    {hook !== undefined && <HookForm init={hook} events={(hooks.data?.events ?? []) as string[]} onClose={(r) => { setHook(undefined); if (r) { if (r.secret) setShown({ key: r.secret }); hooks.reload(); } }} />}
    {log && <Modal title={'Messages to ' + log.hook.url} onClose={() => setLog(null)}>{log.items.length === 0 ? <Empty>Nothing sent yet.</Empty> : <table><thead><tr><th>Event</th><th>Result</th><th>When</th><th /></tr></thead><tbody>
      {(log.items as Rec[]).map((d) => <tr key={d.id}><td>{d.kind}</td><td>{d.status === 'sent' ? <Badge tone="ok">Sent</Badge> : d.status === 'failed' ? <Badge tone="bad">Failed</Badge> : <Badge>Waiting</Badge>}<div className="muted">{d.error || (d.httpCode ? 'HTTP ' + d.httpCode : '')}</div></td><td>{fmtDate(d.createdAt)}</td>
        <td>{d.status === 'failed' && <Act label="Try again" run={async () => { await api(`/api/servicedesk/integration/deliveries/${d.id}/retry`, { method: 'POST', body: {} }); setLog(null); }} />}</td></tr>)}</tbody></table>}</Modal>}
  </>;
}

function KeyForm({ onClose }: { onClose: (created: Rec | null) => void }) {
  const [name, setName] = useState(''); const [scope, setScope] = useState('read'); const out = useRef<Rec | null>(null); const f = useSubmit(() => onClose(out.current));
  return <Modal title="New API key" onClose={() => onClose(null)}><div className="form">
    <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} placeholder="The program that will use it" autoFocus /></Field>
    <Field label="What it can do"><select value={scope} onChange={(e) => setScope(e.target.value)}><option value="read">Read tickets only</option><option value="write">Read, open tickets and add comments</option></select></Field>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(null)}>Cancel</button>
      <button className="primary" disabled={f.busy || !name.trim()} onClick={() => f.submit(async () => { out.current = await api<Rec>('/api/servicedesk/integration/keys', { body: { name, scope } }); })}>Create key</button></div></div></Modal>;
}

function HookForm({ init, events, onClose }: { init: Rec | null; events: string[]; onClose: (r: Rec | null) => void }) {
  const [url, setUrl] = useState<string>(init?.url ?? ''); const [sel, setSel] = useState<string[]>(!init || init.events === '*' ? [] : String(init.events).split(',')); const [active, setActive] = useState<boolean>(init?.active ?? true);
  const out = useRef<Rec | null>(null); const f = useSubmit(() => onClose(out.current ?? { ok: true }));
  const toggle = (e: string) => setSel((s) => (s.includes(e) ? s.filter((x) => x !== e) : [...s, e]));
  const body = { url, events: sel.length ? sel.join(',') : '*', active };
  return <Modal title={init ? 'Edit webhook' : 'New webhook'} onClose={() => onClose(null)}><div className="form">
    <Field label="Address that receives the messages"><input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://" autoFocus /></Field>
    <fieldset><legend>Events (none selected means all)</legend>{events.map((e) => <label key={e} style={{ display: 'block' }}><input type="checkbox" checked={sel.includes(e)} onChange={() => toggle(e)} /> {e}</label>)}</fieldset>
    {init && <label><input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /> Switched on</label>}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(null)}>Cancel</button>
      <button className="primary" disabled={f.busy || !url.trim()} onClick={() => f.submit(async () => { out.current = await api<Rec>(init ? `/api/servicedesk/integration/webhooks/${init.id}` : '/api/servicedesk/integration/webhooks', { method: init ? 'PUT' : 'POST', body }); })}>Save</button></div></div></Modal>;
}

/** Sends last month's report by mail on its own, to you and to each customer admin. */
function ReportMail() {
  const cfg = useLoad<Rec>('/api/servicedesk/integration/reportmail');
  const [v, setV] = useState<{ enabled: boolean; to: string; companyAdmins: boolean } | null>(null); const [saved, setSaved] = useState(false);
  const f = useSubmit(() => { setSaved(true); cfg.reload(); });
  useEffect(() => { if (cfg.data) setV({ enabled: !!cfg.data.enabled, to: cfg.data.to ?? '', companyAdmins: cfg.data.companyAdmins !== false }); }, [cfg.data]);
  const edit = (p: Partial<NonNullable<typeof v>>) => { setSaved(false); setV({ ...v!, ...p }); };
  return <Card title="Monthly report by mail">
    <Loading loading={!v} error={cfg.error} />
    {v && <div className="form">
      <label><input type="checkbox" checked={v.enabled} onChange={(e) => edit({ enabled: e.target.checked })} /> Mail last month's report at the start of every month</label>
      <Field label="Your own addresses (separate with commas). They get the report for all companies."><input value={v.to} onChange={(e) => edit({ to: e.target.value })} placeholder="it@yourcompany.com" /></Field>
      <label><input type="checkbox" checked={v.companyAdmins} onChange={(e) => edit({ companyAdmins: e.target.checked })} /> Each customer admin gets the report of their own company</label>
      <p className="muted">Mail must be set up first (Mail page). Each report is sent once.</p>
      <Err text={f.err} />
      <div className="actions">{saved && <span className="muted">Saved.</span>}
        <button className="primary" disabled={f.busy} onClick={() => f.submit(() => api('/api/servicedesk/integration/reportmail', { method: 'PUT', body: v }))}>Save</button>
        <Act label="Send last month's report now" confirm="Send last month's report to everyone chosen above, even if it was sent before?" run={async () => { await api('/api/servicedesk/integration/reportmail/send', { method: 'POST', body: {} }); }} /></div>
    </div>}
  </Card>;
}
