import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from './http';
import { Act, Badge, Card, Empty, Err, Field, Loading, fmtDate, useSubmit } from './ui';

const STATUS: Record<string, [string, 'ok' | 'warn' | 'bad' | undefined]> = { queued: ['Waiting', 'warn'], sent: ['Sent', 'ok'], failed: ['Failed', 'bad'] };

/** Admin page: how this customer sends mail, a test button, and the outbox. */
function Incoming() {
  const cfg = useLoad<Rec>('/api/platform/mail/inbound');
  const [v, setV] = useState<Rec | null>(null); const [msg, setMsg] = useState('');
  useEffect(() => { if (cfg.data && !v) setV({ ...cfg.data, password: '' }); }, [cfg.data, v]);
  const save = useSubmit(() => { setMsg('Saved.'); cfg.reload(); });
  const test = useSubmit(() => undefined); const [found, setFound] = useState('');
  if (!v) return <Card title="Incoming mail"><Loading loading={cfg.loading} error={cfg.error} /></Card>;
  const set = (k: string, x: unknown) => { setMsg(''); setFound(''); setV({ ...v, [k]: x }); };
  const body = () => { const b: Rec = { provider: v.provider, host: v.host, port: Number(v.port) || 993, security: v.security, user: v.user, folder: v.folder, mailbox: v.mailbox, autoCreate: !!v.autoCreate }; if (v.password) b.password = v.password; return b; };
  return <Card title="Incoming mail">
    <p className="muted">DeskAsk can read your support mailbox every minute. A reply that carries a ticket number is added to that ticket. Anything else waits below for you to turn into a ticket or dismiss.</p>
    <div className="form">
      <Field label="Read mail with"><select value={v.provider} onChange={(e) => set('provider', e.target.value)}><option value="none">Nothing (off)</option><option value="imap">A mail server (IMAP)</option><option value="graph">Microsoft 365 (uses the sending application)</option></select></Field>
      {v.provider === 'imap' && <>
        <div className="row">
          <Field label="Mail server"><input value={v.host} onChange={(e) => set('host', e.target.value)} placeholder="imap.yourcompany.com" /></Field>
          <Field label="Port"><input type="number" value={v.port} onChange={(e) => set('port', e.target.value)} /></Field>
          <Field label="Security"><select value={v.security} onChange={(e) => set('security', e.target.value)}><option value="ssl">SSL (usually port 993)</option><option value="starttls">STARTTLS (usually port 143)</option><option value="none">None</option></select></Field>
        </div>
        <div className="row">
          <Field label="User name"><input value={v.user} onChange={(e) => set('user', e.target.value)} autoComplete="off" /></Field>
          <Field label={cfg.data?.hasPassword ? 'Password (saved; type to replace)' : 'Password'}><input type="password" value={v.password} onChange={(e) => set('password', e.target.value)} autoComplete="new-password" /></Field>
        </div>
        <Field label="Folder (empty for the main inbox)"><input value={v.folder} onChange={(e) => set('folder', e.target.value)} /></Field>
      </>}
      {v.provider === 'graph' && <>
        <p className="muted">The Microsoft 365 application also needs the Mail.ReadWrite application permission (with admin consent). It reads the mailbox below.</p>
        <Field label="Mailbox (empty to use the From address)"><input type="email" value={v.mailbox} onChange={(e) => set('mailbox', e.target.value)} /></Field>
      </>}
      {v.provider !== 'none' && <label className="check"><input type="checkbox" checked={!!v.autoCreate} onChange={(e) => set('autoCreate', e.target.checked)} /> Open tickets automatically for customer users who are linked to a company</label>}
      <Err text={save.err} />{msg && <p role="status">{msg}</p>}
      <div className="actions">
        {cfg.data?.on && <button disabled={test.busy} onClick={() => test.submit(async () => { const r = await api<Rec>('/api/platform/mail/inbound/test', { body: {} }); setFound(`Connected. ${r.unread} unread message${r.unread === 1 ? '' : 's'} waiting.`); })}>Test connection</button>}
        <button className="primary" disabled={save.busy} onClick={() => save.submit(() => api('/api/platform/mail/inbound', { method: 'PUT', body: body() }))}>Save incoming settings</button>
      </div>
      <Err text={test.err} />{found && <p role="status">{found}</p>}
    </div>
  </Card>;
}

function Inbox() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/platform/mail/inbox?status=new');
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [pick, setPick] = useState<Record<string, string>>({}); const [msg, setMsg] = useState('');
  const items = (data?.items ?? []) as Rec[]; const cos = Array.isArray(companies.data) ? companies.data : [];
  const run = async (fn: () => Promise<unknown>) => { setMsg(''); try { await fn(); reload(); } catch (e) { setMsg((e as Error).message); } };
  return <Card title="Inbox: waiting for a decision">
    <Loading loading={loading && !data} error={error} /><Err text={msg} />
    {data && (items.length === 0 ? <Empty>No mail is waiting.</Empty> : items.map((m) => <div key={m.id} className="mailrow" style={{ borderTop: '1px solid var(--line, #0001)', padding: '10px 0' }}>
      <strong>{m.subject || '(no subject)'}</strong> <span className="muted">from {m.name ? `${m.name} <${m.from}>` : m.from} · {fmtDate(m.receivedAt)}</span>
      <p style={{ whiteSpace: 'pre-wrap', margin: '6px 0' }}>{String(m.body).slice(0, 400)}{String(m.body).length > 400 ? '…' : ''}</p>
      {m.files > 0 && <p className="muted">{m.files} file{m.files === 1 ? '' : 's'} attached; they go onto the ticket.</p>}
      <div className="row">
        <select aria-label={`Company for ${m.subject}`} value={pick[m.id] ?? ''} onChange={(e) => setPick({ ...pick, [m.id]: e.target.value })}><option value="">No company</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
        <Act label="Open as ticket" primary run={() => run(async () => { await api(`/api/platform/mail/inbox/${m.id}/convert`, { body: pick[m.id] ? { companyId: Number(pick[m.id]) } : {} }); })} />
        <Act label="Dismiss" confirm="Dismiss this message? It will not become a ticket." run={() => run(() => api(`/api/platform/mail/inbox/${m.id}/reject`, { method: 'POST', body: {} }))} />
      </div></div>))}
    <div className="actions"><button onClick={() => run(async () => { await api('/api/platform/mail/inbound/fetch', { body: {} }); })}>Check mail now</button><button onClick={reload}>Refresh</button></div>
  </Card>;
}

function Templates() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/platform/mail/templates');
  const items = (data?.items ?? []) as Rec[];
  const [key, setKey] = useState(''); const [edit, setEdit] = useState<Rec | null>(null); const [prev, setPrev] = useState<Rec | null>(null);
  const [msg, setMsg] = useState(''); const [to, setTo] = useState(''); const save = useSubmit(() => { setMsg('Saved.'); setEdit(null); reload(); }); const test = useSubmit(() => undefined);
  const cur = items.find((t) => t.key === (key || items[0]?.key));
  if (!cur) return <Card title="Message templates"><Loading loading={loading} error={error} /></Card>;
  const v = edit && edit.key === cur.key ? edit : { key: cur.key, subject: cur.subject, body: cur.body };
  const show = async () => { try { setPrev(await api<Rec>(`/api/platform/mail/templates/${cur.key}/preview`, { body: { subject: v.subject, body: v.body } })); } catch (e) { setMsg((e as Error).message); } };
  return <Card title="Message templates">
    <p className="muted">The wording of the mails DeskAsk sends by itself. Put values in double braces, for example {'{{number}}'}. The preview uses an example ticket.</p>
    <div className="form">
      <Field label="Message"><select value={cur.key} onChange={(e) => { setKey(e.target.value); setEdit(null); setPrev(null); setMsg(''); }}>{items.map((t) => <option key={t.key} value={t.key}>{t.name}{t.custom ? ' (edited)' : ''}</option>)}</select></Field>
      <p className="muted">{cur.when}</p>
      <Field label="Subject"><input value={v.subject} onChange={(e) => { setEdit({ ...v, subject: e.target.value }); setMsg(''); }} maxLength={200} /></Field>
      <Field label="Message"><textarea rows={11} value={v.body} onChange={(e) => { setEdit({ ...v, body: e.target.value }); setMsg(''); }} /></Field>
      <p className="muted">You can use: {(cur.values as string[]).map((x) => <code key={x} style={{ marginRight: 6 }}>{`{{${x}}}`}</code>)}</p>
      <Err text={save.err} />{msg && <p role="status">{msg}</p>}
      <div className="actions">
        <Act label="Back to the original wording" disabled={!cur.custom} confirm="Replace your wording with the original?" run={async () => { await api(`/api/platform/mail/templates/${cur.key}`, { method: 'DELETE' }); setEdit(null); setPrev(null); setMsg('Original wording restored.'); reload(); }} />
        <button onClick={show}>Preview</button>
        <button className="primary" disabled={save.busy || !edit} onClick={() => save.submit(() => api(`/api/platform/mail/templates/${cur.key}`, { method: 'PUT', body: { subject: v.subject, body: v.body } }))}>Save wording</button>
      </div>
      {prev && <div><p><strong>{prev.subject}</strong></p><iframe title="Mail preview" sandbox="" srcDoc={prev.html} style={{ width: '100%', height: 360, border: '1px solid var(--line, #0002)', borderRadius: 8, background: '#fff' }} /></div>}
      <div className="row">
        <Field label="Send an example to"><input type="email" value={to} onChange={(e) => setTo(e.target.value)} placeholder="you@yourcompany.com" /></Field>
        <button disabled={test.busy || !to.includes('@') || !!edit} title={edit ? 'Save your changes first' : ''} onClick={() => test.submit(async () => { await api(`/api/platform/mail/templates/${cur.key}/test`, { body: { to } }); setMsg('Example sent.'); })}>Send example</button>
      </div>
      <Err text={test.err} />
    </div>
  </Card>;
}

export default function Mail() {
  const cfg = useLoad<Rec>('/api/platform/mail/config');
  const outbox = useLoad<Rec>('/api/platform/mail/outbox');
  const [v, setV] = useState<Rec | null>(null); const [msg, setMsg] = useState(''); const [to, setTo] = useState(''); const [tested, setTested] = useState('');
  useEffect(() => { if (cfg.data && !v) setV({ ...cfg.data, password: '', secret: '' }); }, [cfg.data, v]);
  const save = useSubmit(() => { setMsg('Saved.'); cfg.reload(); });
  const test = useSubmit(() => undefined);
  if (!v) return <><h1>Mail</h1><Loading loading={cfg.loading} error={cfg.error} /></>;
  const set = (k: string, x: unknown) => { setMsg(''); setV({ ...v, [k]: x }); };
  const items = (outbox.data?.items ?? []) as Rec[];
  const body = () => {
    const b: Rec = { provider: v.provider, from: v.from, fromName: v.fromName, host: v.host, port: Number(v.port) || 587, security: v.security, user: v.user, tenantId: v.tenantId, clientId: v.clientId, publicUrl: v.publicUrl ?? '' };
    if (v.password) b.password = v.password; if (v.secret) b.secret = v.secret;   // left empty = keep the saved one
    return b;
  };
  return <>
    <h1>Mail</h1>
    <p className="muted">DeskAsk sends ticket updates by mail. Choose how, save, then send yourself a test message.</p>
    <Card title="Sending">
      <div className="form">
        <Field label="Send mail with"><select value={v.provider} onChange={(e) => set('provider', e.target.value)}><option value="none">Nothing (mail is off)</option><option value="smtp">A mail server (SMTP)</option><option value="graph">Microsoft 365</option></select></Field>
        {v.provider !== 'none' && <div className="row">
          <Field label="From address"><input type="email" value={v.from} onChange={(e) => set('from', e.target.value)} placeholder="support@yourcompany.com" /></Field>
          <Field label="From name"><input value={v.fromName} onChange={(e) => set('fromName', e.target.value)} placeholder="IT Support" /></Field>
        </div>}
        {v.provider === 'smtp' && <>
          <div className="row">
            <Field label="Mail server"><input value={v.host} onChange={(e) => set('host', e.target.value)} placeholder="smtp.yourcompany.com" /></Field>
            <Field label="Port"><input type="number" value={v.port} onChange={(e) => set('port', e.target.value)} /></Field>
            <Field label="Security"><select value={v.security} onChange={(e) => set('security', e.target.value)}><option value="starttls">STARTTLS (usually port 587)</option><option value="ssl">SSL (usually port 465)</option><option value="none">None</option></select></Field>
          </div>
          <div className="row">
            <Field label="User name (leave empty if not needed)"><input value={v.user} onChange={(e) => set('user', e.target.value)} autoComplete="off" /></Field>
            <Field label={cfg.data?.hasPassword ? 'Password (saved; type to replace)' : 'Password'}><input type="password" value={v.password} onChange={(e) => set('password', e.target.value)} autoComplete="new-password" /></Field>
          </div>
        </>}
        {v.provider === 'graph' && <>
          <p className="muted">Create an app registration in Microsoft Entra with the Mail.Send application permission, grant admin consent, then enter its details. The From address must be a mailbox in your tenant.</p>
          <div className="row">
            <Field label="Directory (tenant) id"><input value={v.tenantId} onChange={(e) => set('tenantId', e.target.value)} /></Field>
            <Field label="Application (client) id"><input value={v.clientId} onChange={(e) => set('clientId', e.target.value)} /></Field>
          </div>
          <Field label={cfg.data?.hasSecret ? 'Client secret (saved; type to replace)' : 'Client secret'}><input type="password" value={v.secret} onChange={(e) => set('secret', e.target.value)} autoComplete="new-password" /></Field>
        </>}
        {v.provider !== 'none' && <Field label="Public address of DeskAsk (used for links in mails)"><input value={v.publicUrl ?? ''} onChange={(e) => set('publicUrl', e.target.value)} placeholder="https://support.yourcompany.com" /></Field>}
        <Err text={save.err} />{msg && <p role="status">{msg}</p>}
        <div className="actions"><button className="primary" disabled={save.busy} onClick={() => save.submit(() => api('/api/platform/mail/config', { method: 'PUT', body: body() }))}>Save settings</button></div>
      </div>
    </Card>
    {cfg.data?.on && <Card title="Send a test message">
      <div className="row">
        <Field label="Send to"><input type="email" value={to} onChange={(e) => { setTo(e.target.value); setTested(''); }} placeholder="you@yourcompany.com" /></Field>
        <button disabled={test.busy || !to.includes('@')} onClick={() => test.submit(async () => { await api('/api/platform/mail/test', { body: { to } }); setTested('Sent. Check that inbox.'); })}>Send test</button>
      </div>
      <Err text={test.err} />{tested && <p role="status">{tested}</p>}
      <p className="muted">Test only uses the saved settings. Save first if you changed something.</p>
    </Card>}
    <Templates />
    <Incoming />
    <Inbox />
    <Card title="Recent messages">
      <Loading loading={outbox.loading && !outbox.data} error={outbox.error} />
      {outbox.data && (items.length === 0 ? <Empty>Nothing has been sent yet.</Empty> : <table><thead><tr><th>To</th><th>Subject</th><th>Status</th><th>When</th><th /></tr></thead><tbody>
        {items.map((m) => { const [label, tone] = STATUS[m.status] ?? [m.status, undefined]; return <tr key={m.id}>
          <td>{m.to}</td><td>{m.subject}{m.error && <div className="muted">{m.error}</div>}</td><td><Badge tone={tone}>{label}</Badge>{m.attempts > 0 && m.status !== 'sent' ? <span className="muted"> {m.attempts} tries</span> : null}</td>
          <td className="muted">{fmtDate(m.sentAt ?? m.createdAt)}</td>
          <td>{m.status === 'failed' && <Act label="Try again" run={async () => { await api(`/api/platform/mail/outbox/${m.id}/retry`, { method: 'POST', body: {} }); outbox.reload(); }} />}</td></tr>; })}
      </tbody></table>)}
      <div className="actions"><button onClick={outbox.reload}>Refresh</button></div>
    </Card>
  </>;
}
