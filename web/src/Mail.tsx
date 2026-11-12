import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from './http';
import { Act, Badge, Card, Empty, Err, Field, Loading, fmtDate, useSubmit } from './ui';

const STATUS: Record<string, [string, 'ok' | 'warn' | 'bad' | undefined]> = { queued: ['Waiting', 'warn'], sent: ['Sent', 'ok'], failed: ['Failed', 'bad'] };

/** Admin page: how this customer sends mail, a test button, and the outbox. */
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
    const b: Rec = { provider: v.provider, from: v.from, fromName: v.fromName, host: v.host, port: Number(v.port) || 587, security: v.security, user: v.user, tenantId: v.tenantId, clientId: v.clientId };
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
