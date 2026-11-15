import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Field, Loading } from '../ui';

type Own = { companyId: number; recipients: string; teams: string };

/** Where alerts go (mail and Teams, with a customer's own where needed), escalation of unacknowledged alerts, and the monthly report. */
export default function Settings() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/monitoring/settings');
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [v, setV] = useState<Rec>({}); const [own, setOwn] = useState<Own[]>([]);
  const [msg, setMsg] = useState(''); const [err, setErr] = useState(''); const [test, setTest] = useState<Record<string, string>>({});
  const [month, setMonth] = useState(() => { const d = new Date(); d.setUTCDate(1); d.setUTCMonth(d.getUTCMonth() - 1); return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`; });
  useEffect(() => { if (data) { setV({ ...data }); setOwn(((data.companies ?? []) as Rec[]).map((c) => ({ companyId: Number(c.companyId), recipients: String(c.recipients ?? ''), teams: String(c.teams ?? '') }))); } }, [data]);
  const cos = (Array.isArray(companies.data) ? companies.data : ((companies.data as Rec | null)?.items ?? [])) as Rec[];
  const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const save = async () => {
    setMsg(''); setErr('');
    try { await api('/api/monitoring/settings', { method: 'PUT', body: { ...v, escalateMinutes: Number(v.escalateMinutes ?? 0), companies: own } }); setMsg('Saved.'); reload(); } catch (e) { setErr((e as Error).message); }
  };
  const tryTeams = async (key: string, body: Rec) => {
    setTest({ ...test, [key]: 'Sending…' });
    try { await api('/api/monitoring/settings/teams-test', { method: 'POST', body }); setTest({ ...test, [key]: 'Sent. Check the channel.' }); } catch (e) { setTest({ ...test, [key]: (e as Error).message }); }
  };
  const hookHint = 'Paste the webhook address from Teams (Workflows › "Post to a channel when a webhook request is received"). Saved addresses show only their host.';
  if (loading && !data) return <Loading loading error={error} />;
  return <>
    <h1>Monitoring settings</h1>
    <form className="form" style={{ maxWidth: 720 }} onSubmit={(e) => { e.preventDefault(); void save(); }}>
      <h2>Where alerts go</h2>
      <Field label="Mail to (addresses separated by commas)"><textarea rows={2} value={v.recipients ?? ''} onChange={(e) => set('recipients', e.target.value)} placeholder="noc@yourcompany.com, oncall@yourcompany.com" /></Field>
      <Field label="Microsoft Teams channel webhook"><input value={v.teams ?? ''} onChange={(e) => set('teams', e.target.value)} placeholder="https://….webhook.office.com/… or a Workflows address" aria-describedby="hook-hint" /></Field>
      <p className="muted small" id="hook-hint">{hookHint} Clear the field to stop posting.</p>
      <div className="tools"><button type="button" onClick={() => void tryTeams('main', String(v.teams ?? '').includes('/…') ? {} : { url: v.teams })} disabled={!v.teams}>Send a test message</button>{test.main && <span className="muted" role="status">{test.main}</span>}</div>
      <label className="check"><input type="checkbox" checked={v.mailResolved !== false} onChange={(e) => set('mailResolved', e.target.checked)} /> Also tell them when an alert clears (mail and Teams)</label>

      <h2>Customers with their own recipients</h2>
      <p className="muted">Alerts of these customers go to their own addresses or channel instead of the ones above. The wording of alert mail is under Mail › Templates.</p>
      {own.map((o, i) => <fieldset key={i} className="own">
        <select value={o.companyId || ''} aria-label="Customer" onChange={(e) => setOwn(own.map((x, j) => j === i ? { ...x, companyId: Number(e.target.value) } : x))}><option value="">Choose a customer</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
        <input value={o.recipients} aria-label="Addresses" placeholder="it@customer.com" onChange={(e) => setOwn(own.map((x, j) => j === i ? { ...x, recipients: e.target.value } : x))} />
        <input value={o.teams} aria-label="Teams webhook" placeholder="Teams webhook (optional)" onChange={(e) => setOwn(own.map((x, j) => j === i ? { ...x, teams: e.target.value } : x))} />
        <button type="button" onClick={() => setOwn(own.filter((_, j) => j !== i))}>Remove</button>
      </fieldset>)}
      <div className="tools"><button type="button" onClick={() => setOwn([...own, { companyId: 0, recipients: '', teams: '' }])}>Add a customer</button></div>

      <h2>Escalation</h2>
      <p className="muted">When nobody acknowledges a critical alert in time, it goes once to these people and this channel as well.</p>
      <Field label="Escalate after (minutes; 0 means never)"><input type="number" min={0} max={1440} value={v.escalateMinutes ?? 0} onChange={(e) => set('escalateMinutes', e.target.value)} /></Field>
      <Field label="Escalation mail to"><input value={v.escalateRecipients ?? ''} onChange={(e) => set('escalateRecipients', e.target.value)} placeholder="manager@yourcompany.com" /></Field>
      <Field label="Escalation Teams webhook"><input value={v.escalateTeams ?? ''} onChange={(e) => set('escalateTeams', e.target.value)} placeholder="Teams webhook of the escalation channel" /></Field>
      <div className="tools"><button type="button" disabled={!v.escalateTeams} onClick={() => void tryTeams('esc', String(v.escalateTeams ?? '').includes('/…') ? { which: 'escalation' } : { url: v.escalateTeams })}>Send a test message</button>{test.esc && <span className="muted" role="status">{test.esc}</span>}</div>

      <h2>Monthly uptime report</h2>
      <label className="check"><input type="checkbox" checked={!!v.reportEnabled} onChange={(e) => set('reportEnabled', e.target.checked)} /> Mail last month's uptime report as a PDF on the 1st of every month</label>
      <Field label="Report of all customers to"><input value={v.reportTo ?? ''} onChange={(e) => set('reportTo', e.target.value)} placeholder="management@yourcompany.com" /></Field>
      <label className="check"><input type="checkbox" checked={v.reportCustomers !== false} onChange={(e) => set('reportCustomers', e.target.checked)} /> Each customer with its own recipients (above) gets its own report</label>
      <div className="tools"><input type="month" value={month} onChange={(e) => setMonth(e.target.value)} aria-label="Report month" />
        <button type="button" onClick={async () => { try { await api('/api/monitoring/reports/send', { method: 'POST', body: { month } }); setTest({ ...test, rep: 'Queued: the reports go out within a minute.' }); } catch (e) { setTest({ ...test, rep: (e as Error).message }); } }}>Send the reports for this month now</button>
        {test.rep && <span className="muted" role="status">{test.rep}</span>}</div>

      {err && <p className="err" role="alert">{err}</p>}
      {msg && <p className="muted" role="status">{msg}</p>}
      <div className="tools"><button type="submit" className="primary">Save settings</button></div>
    </form>
  </>;
}
