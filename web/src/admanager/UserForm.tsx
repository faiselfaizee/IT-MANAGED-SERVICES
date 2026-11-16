import { useEffect, useMemo, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Card, Err, Field, Modal, useSubmit } from '../ui';
import { ALL_FIELDS, DRIVES, TABS, applyName, cleanSam, type FieldDef } from './fields';
import { ObjectPicker, OuPicker, ouPath } from './pickers';

type Hit = { dn: string; name: string; sub?: string };

export function FieldInput({ f, value, onChange }: { f: FieldDef; value: string; onChange: (v: string) => void }) {
  return <Field label={f.l}>{f.type === 'drive'
    ? <select value={value} onChange={(e) => onChange(e.target.value)}><option value="">None</option>{DRIVES.map((d) => <option key={d}>{d}</option>)}</select>
    : <input value={value} onChange={(e) => onChange(e.target.value)} />}</Field>;
}

/** The new-user form: every field of the account, its OU, groups and manager from the directory, a template to start from. */
export function NewUser({ domain, onDone }: { domain: Rec; onDone?: (dn: string) => void }) {
  const info = useLoad<Rec>(`/api/adsuite/domains/${domain.id}/info`);
  const templates = useLoad<Rec[]>(`/api/adsuite/domains/${domain.id}/templates`);
  const [tab, setTab] = useState('account');
  const [tid, setTid] = useState<number>(0);
  const [first, setFirst] = useState(''); const [initials, setInitials] = useState(''); const [last, setLast] = useState('');
  const [display, setDisplay] = useState(''); const [displayTouched, setDT] = useState(false);
  const [sam, setSam] = useState(''); const [samTouched, setST] = useState(false);
  const [suffix, setSuffix] = useState(''); const [email, setEmail] = useState(''); const [emailTouched, setET] = useState(false);
  const [ou, setOu] = useState(''); const [groups, setGroups] = useState<Hit[]>([]); const [manager, setManager] = useState<Hit[]>([]);
  const [attrs, setAttrs] = useState<Rec>({});
  const [pwMode, setPwMode] = useState<'generate' | 'type'>('generate'); const [pw, setPw] = useState(''); const [pw2, setPw2] = useState('');
  const [mustChange, setMustChange] = useState(true); const [neverExpires, setNeverExpires] = useState(false); const [startDisabled, setStartDisabled] = useState(false);
  const [expires, setExpires] = useState(''); const [done, setDone] = useState<Rec | null>(null);
  const tpl = ((templates.data ?? []) as Rec[]).find((t) => t.id === tid);
  const naming = (tpl?.naming ?? {}) as Rec;
  const suffixes = ((info.data?.upnSuffixes ?? [domain.dnsName]) as string[]).filter(Boolean);
  useEffect(() => { if (!suffix && suffixes.length) setSuffix(suffixes[0]); }, [suffixes.join(','), suffix]); // eslint-disable-line react-hooks/exhaustive-deps

  // the names follow the person's name until someone types their own
  useEffect(() => { if (!displayTouched) setDisplay(applyName(String(naming.display || '{first} {last}'), first, last, initials)); }, [first, last, initials, naming.display, displayTouched]);
  useEffect(() => { if (!samTouched) setSam(cleanSam(applyName(String(naming.sam || '{first}.{last}'), first, last, initials)).toLowerCase()); }, [first, last, initials, naming.sam, samTouched]);
  useEffect(() => { if (!emailTouched && naming.email) setEmail(applyName(String(naming.email), first, last, initials).toLowerCase().replace(/\s+/g, '')); }, [first, last, initials, naming.email, emailTouched]);

  const applyTemplate = (id: number) => {
    setTid(id); const t = ((templates.data ?? []) as Rec[]).find((x) => x.id === id); if (!t) return;
    if (t.ou) setOu(String(t.ou));
    setGroups(((t.groups ?? []) as string[]).map((dn) => ({ dn, name: ouPath(dn).split(' › ').pop() ?? dn })));
    setAttrs({ ...(t.attrs as Rec) });
    const o = (t.options ?? {}) as Rec;
    if (typeof o.mustChange === 'boolean') setMustChange(o.mustChange);
    if (typeof o.pwdNeverExpires === 'boolean') setNeverExpires(o.pwdNeverExpires);
    if (t.naming?.upnSuffix) setSuffix(String(t.naming.upnSuffix));
    if (o.expiresDays) { const d = new Date(Date.now() + Number(o.expiresDays) * 86400000); setExpires(d.toISOString().slice(0, 10)); }
    setDT(false); setST(false); setET(false);
  };

  const policy = info.data?.password as Rec | undefined;
  const pwProblem = pwMode === 'type' ? (pw === '' ? 'Type the password.' : pw !== pw2 ? 'The two passwords differ.' : policy?.minLength && pw.length < Number(policy.minLength) ? `At least ${policy.minLength} characters.` : '') : '';
  const missing = !ou ? 'Choose the OU.' : !display ? 'Give a name.' : !sam ? 'Give a sign-in name.' : pwProblem;
  const f = useSubmit(() => undefined);
  const submit = () => f.submit(async () => {
    const extra: Rec = {}; for (const fd of ALL_FIELDS) if ((attrs[fd.k] ?? '') !== '') extra[fd.k] = attrs[fd.k];
    const r = await api<Rec>(`/api/adsuite/domains/${domain.id}/actions`, { method: 'POST', body: {
      action: 'create-user', ou, first, last, display, sam, upn: `${sam}@${suffix}`, email, manager: manager[0]?.dn ?? '', groups: groups.map((g) => g.dn), attrs: { ...extra, initials },
      password: pwMode === 'type' ? pw : '', mustChange: mustChange && !neverExpires, pwdNeverExpires: neverExpires, disabled: startDisabled, expires } });
    setDone(r);
  });
  const tabs = useMemo(() => [{ key: 'account', title: 'Account' }, { key: 'groups', title: `Member of (${groups.length})` }, ...TABS.map((t) => ({ key: t.key, title: t.title }))], [groups.length]);

  if (done) return <Card title="Account created">
    <p><strong>{display}</strong> was created in <code>{ouPath(ou, domain.baseDn)}</code>{groups.length ? ` and added to ${groups.length} group(s)` : ''}.</p>
    {done.password && <><p>The password is shown once. Give it to the person safely.</p><p><code className="copy" onClick={() => void navigator.clipboard?.writeText(String(done.password))} title="Click to copy">{String(done.password)}</code></p></>}
    <div className="actions"><button className="primary" onClick={() => { onDone?.(String(done.dn)); }}>Open the account</button>
      <button onClick={() => { setDone(null); setFirst(''); setLast(''); setInitials(''); setDT(false); setST(false); setET(false); setEmail(''); setManager([]); setPw(''); setPw2(''); }}>Create another</button></div>
  </Card>;

  return <div className="userform">
    <div className="formhead">
      <label className="inline">Template <select value={tid} onChange={(e) => applyTemplate(Number(e.target.value))}><option value={0}>None</option>
        {((templates.data ?? []) as Rec[]).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}</select></label>
      {tpl?.description && <span className="muted small">{tpl.description}</span>}
    </div>
    <div className="tabs" role="tablist">{tabs.map((t) => <button key={t.key} role="tab" aria-selected={tab === t.key} className={tab === t.key ? 'on' : ''} onClick={() => setTab(t.key)}>{t.title}</button>)}</div>
    <div className="tabbody">
      {tab === 'account' && <div className="grid2">
        <Field label="First name"><input value={first} onChange={(e) => setFirst(e.target.value)} autoFocus /></Field>
        <Field label="Initials"><input value={initials} maxLength={6} onChange={(e) => setInitials(e.target.value)} /></Field>
        <Field label="Last name"><input value={last} onChange={(e) => setLast(e.target.value)} /></Field>
        <Field label="Display name (also the account's name in the OU)"><input value={display} onChange={(e) => { setDisplay(e.target.value); setDT(true); }} /></Field>
        <Field label="Sign-in name (pre-Windows 2000)"><input value={sam} maxLength={20} onChange={(e) => { setSam(cleanSam(e.target.value)); setST(true); }} /></Field>
        <Field label="User principal name"><div className="joined"><input value={sam} readOnly aria-label="UPN name" /><span>@</span>
          <select aria-label="UPN suffix" value={suffix} onChange={(e) => setSuffix(e.target.value)}>{suffixes.map((s) => <option key={s}>{s}</option>)}</select></div></Field>
        <Field label="Email"><input value={email} type="email" onChange={(e) => { setEmail(e.target.value); setET(true); }} /></Field>
        <div className="wide"><span className="lbl">OU</span><OuPicker domain={domain} value={ou} onChange={setOu} /></div>
        <div className="wide"><span className="lbl">Manager</span><ObjectPicker domain={domain} kind="user" value={manager} onChange={setManager} label="Manager" placeholder="Search for the manager…" /></div>
        <fieldset className="wide"><legend>Password</legend>
          <label className="check"><input type="radio" checked={pwMode === 'generate'} onChange={() => setPwMode('generate')} /> Make a strong password (shown once after creating)</label>
          <label className="check"><input type="radio" checked={pwMode === 'type'} onChange={() => setPwMode('type')} /> Type a password</label>
          {pwMode === 'type' && <div className="grid2"><Field label="Password"><input type="password" autoComplete="new-password" value={pw} onChange={(e) => setPw(e.target.value)} /></Field>
            <Field label="Again"><input type="password" autoComplete="new-password" value={pw2} onChange={(e) => setPw2(e.target.value)} /></Field></div>}
          {policy && <p className="muted small">Domain rules: at least {String(policy.minLength)} characters{policy.complex ? ', three of upper case, lower case, digits and symbols, not containing the name' : ''}{policy.maxAgeDays ? `; expires after ${policy.maxAgeDays} days` : ''}.</p>}
          {!domain.tls && <p className="muted small">LDAPS is off for this domain: setting the password works only when the site controller runs on Windows.</p>}
        </fieldset>
        <fieldset className="wide"><legend>Account options</legend>
          <label className="check"><input type="checkbox" checked={mustChange && !neverExpires} disabled={neverExpires} onChange={(e) => setMustChange(e.target.checked)} /> Must change password at next sign-in</label>
          <label className="check"><input type="checkbox" checked={neverExpires} onChange={(e) => setNeverExpires(e.target.checked)} /> Password never expires</label>
          <label className="check"><input type="checkbox" checked={startDisabled} onChange={(e) => setStartDisabled(e.target.checked)} /> Leave the account disabled</label>
          <Field label="Account expires (last day; empty = never)"><input type="date" value={expires} onChange={(e) => setExpires(e.target.value)} /></Field>
        </fieldset>
      </div>}
      {tab === 'groups' && <div>
        <p className="muted small">Every account is in Domain Users. Add the groups this person needs:</p>
        <ObjectPicker domain={domain} kind="group" multiple value={groups} onChange={setGroups} label="Groups" placeholder="Search for a group…" />
      </div>}
      {TABS.filter((t) => t.key === tab).map((t) => <div key={t.key} className="grid2">
        {t.fields.map((fd) => <div key={fd.k} className={fd.wide ? 'wide' : ''}><FieldInput f={fd} value={String(attrs[fd.k] ?? '')} onChange={(v) => setAttrs({ ...attrs, [fd.k]: v })} /></div>)}</div>)}
    </div>
    <Err text={f.err} />
    <div className="actions sticky"><button className="primary" disabled={f.busy || !!missing} onClick={() => void submit()}>{f.busy ? 'Creating…' : 'Create account'}</button>
      {missing && <span className="muted small">{missing}</span>}</div>
  </div>;
}

/** Edit an existing account: the same groups of fields. */
export function EditUser({ x, onClose, onSave }: { x: Rec; onClose: () => void; onSave: (a: Rec) => Promise<unknown> }) {
  const ob = x.obj as Rec; const user = ob.kind === 'user';
  const start: Rec = { displayName: ob.name, givenName: '', sn: '', initials: '', mail: ob.mail ?? '', ...(x.values as Rec) };
  const [v, setV] = useState<Rec>(start); const [tab, setTab] = useState(user ? 'names' : 'general');
  const { err, busy, submit } = useSubmit(onClose);
  const names: FieldDef[] = [{ k: 'givenName', l: 'First name' }, { k: 'initials', l: 'Initials' }, { k: 'sn', l: 'Last name' }, { k: 'displayName', l: 'Display name' }, { k: 'mail', l: 'Email' }];
  const tabs = user ? [{ key: 'names', title: 'Name', fields: names }, ...TABS] : [{ key: 'general', title: 'General', fields: [{ k: 'description', l: 'Description', wide: true }, { k: 'mail', l: 'Email' }, { k: 'info', l: 'Notes', wide: true }] as FieldDef[] }];
  return <Modal title={'Edit ' + String(ob.name)} onClose={onClose}>
    <div className="tabs" role="tablist">{tabs.map((t) => <button key={t.key} role="tab" aria-selected={tab === t.key} className={tab === t.key ? 'on' : ''} onClick={() => setTab(t.key)}>{t.title}</button>)}</div>
    {tabs.filter((t) => t.key === tab).map((t) => <div key={t.key} className="grid2">{t.fields.map((fd) => <div key={fd.k} className={fd.wide ? 'wide' : ''}>
      <FieldInput f={fd} value={String(v[fd.k] ?? '')} onChange={(val) => setV({ ...v, [fd.k]: val })} /></div>)}</div>)}
    <Err text={err} />
    <div className="actions"><button className="primary" disabled={busy} onClick={() => void submit(() => {
      const changed: Rec = {}; for (const k of Object.keys(v)) if (k !== 'manager' && (v[k] ?? '') !== (start[k] ?? '')) changed[k] = v[k] ?? '';
      return Object.keys(changed).length ? onSave(changed) : Promise.resolve();
    })}>Save</button><button onClick={onClose}>Cancel</button></div>
  </Modal>;
}
