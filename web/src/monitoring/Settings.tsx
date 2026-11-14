import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Field, Loading } from '../ui';

/** Who gets alert mail: one list for everyone, and a customer's own list where it should go elsewhere. */
export default function Settings() {
  const { data, error, loading } = useLoad<Rec>('/api/monitoring/settings');
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [rec, setRec] = useState(''); const [resolved, setResolved] = useState(true); const [own, setOwn] = useState<{ companyId: number; recipients: string }[]>([]);
  const [msg, setMsg] = useState(''); const [err, setErr] = useState('');
  useEffect(() => { if (data) { setRec(String(data.recipients ?? '')); setResolved(data.mailResolved !== false); setOwn(((data.companies ?? []) as Rec[]).map((c) => ({ companyId: Number(c.companyId), recipients: String(c.recipients) }))); } }, [data]);
  const cos = (Array.isArray(companies.data) ? companies.data : ((companies.data as Rec | null)?.items ?? [])) as Rec[];
  const save = async () => {
    setMsg(''); setErr('');
    try { await api('/api/monitoring/settings', { method: 'PUT', body: { recipients: rec, mailResolved: resolved, companies: own } }); setMsg('Saved.'); } catch (e) { setErr((e as Error).message); }
  };
  if (loading && !data) return <Loading loading error={error} />;
  return <>
    <h1>Monitoring settings</h1>
    <form className="form" style={{ maxWidth: 640 }} onSubmit={(e) => { e.preventDefault(); void save(); }}>
      <Field label="Alert mail goes to (addresses separated by commas)"><textarea rows={2} value={rec} onChange={(e) => setRec(e.target.value)} placeholder="noc@yourcompany.com, oncall@yourcompany.com" /></Field>
      <label className="check"><input type="checkbox" checked={resolved} onChange={(e) => setResolved(e.target.checked)} /> Also mail when an alert clears</label>
      <h2>Customers with their own recipients</h2>
      <p className="muted">Alerts for these customers go to their own list instead of the one above. The wording of alert mail is under Mail › Templates.</p>
      {own.map((o, i) => <div key={i} className="tools">
        <select value={o.companyId || ''} aria-label="Customer" onChange={(e) => setOwn(own.map((x, j) => j === i ? { ...x, companyId: Number(e.target.value) } : x))}><option value="">Choose a customer</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
        <input value={o.recipients} aria-label="Addresses" placeholder="it@customer.com" onChange={(e) => setOwn(own.map((x, j) => j === i ? { ...x, recipients: e.target.value } : x))} />
        <button type="button" onClick={() => setOwn(own.filter((_, j) => j !== i))}>Remove</button>
      </div>)}
      <div className="tools"><button type="button" onClick={() => setOwn([...own, { companyId: 0, recipients: '' }])}>Add a customer</button></div>
      {err && <p className="err" role="alert">{err}</p>}
      {msg && <p className="muted" role="status">{msg}</p>}
      <div className="tools"><button type="submit" className="primary">Save settings</button></div>
    </form>
  </>;
}
