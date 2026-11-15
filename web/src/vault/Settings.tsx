import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Field, Loading } from '../ui';

/** Vault settings an administrator chooses. */
export default function Settings() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/vault/settings');
  const [v, setV] = useState<Rec>({}); const [msg, setMsg] = useState(''); const [err, setErr] = useState('');
  useEffect(() => { if (data) setV({ ...data }); }, [data]);
  const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  if (loading && !data) return <Loading loading error={error} />;
  return <>
    <h1>Vault settings</h1>
    <form className="form" style={{ maxWidth: 640 }} onSubmit={async (e) => { e.preventDefault(); setMsg(''); setErr(''); try {
      await api('/api/vault/settings', { method: 'PUT', body: { ...v, autoLockMinutes: Number(v.autoLockMinutes), rotationLength: Number(v.rotationLength), defaultRotateDays: Number(v.defaultRotateDays), requestMaxMinutes: Number(v.requestMaxMinutes) } }); setMsg('Saved.'); reload(); } catch (x) { setErr((x as Error).message); } }}>
      <Field label="Who may create vaults"><select value={v.creators ?? 'admins'} onChange={(e) => set('creators', e.target.value)}><option value="admins">Administrators</option><option value="engineers">Administrators and engineers</option></select></Field>
      <Field label="Lock the vault after this many idle minutes"><input type="number" min={1} max={480} value={v.autoLockMinutes ?? 15} onChange={(e) => set('autoLockMinutes', e.target.value)} /></Field>
      <Field label="Longest time an access request can be approved for (minutes)"><input type="number" min={15} max={10080} value={v.requestMaxMinutes ?? 480} onChange={(e) => set('requestMaxMinutes', e.target.value)} /></Field>
      <h2>Managed accounts</h2>
      <Field label="Length of passwords DeskAsk sets"><input type="number" min={14} max={128} value={v.rotationLength ?? 24} onChange={(e) => set('rotationLength', e.target.value)} /></Field>
      <Field label="Change managed passwords every (days, for new accounts; 0 = only when asked)"><input type="number" min={0} max={365} value={v.defaultRotateDays ?? 30} onChange={(e) => set('defaultRotateDays', e.target.value)} /></Field>
      <h2>Breach check</h2>
      <label className="check"><input type="checkbox" checked={v.breachCheck !== false} onChange={(e) => set('breachCheck', e.target.checked)} /> Let people check passwords against Have I Been Pwned (only the first 5 characters of each password's SHA-1 hash leave the server)</label>
      {err && <p className="err" role="alert">{err}</p>}{msg && <p className="muted" role="status">{msg}</p>}
      <div className="tools"><button type="submit" className="primary">Save settings</button></div>
    </form>
  </>;
}
