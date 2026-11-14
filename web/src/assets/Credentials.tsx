import { Fragment, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Empty, Err, Field, Loading, Modal, useSubmit } from '../ui';

/** What a login is for. The first five read computers and install the agent; the rest read servers, hypervisors and network gear. */
const KINDS: [string, string][] = [['windows', 'Windows (user and password)'], ['sshpw', 'Linux (user and SSH password)'], ['ssh', 'Linux (user and SSH private key)'],
  ['snmp', 'Printer, switch or firewall (SNMP v2c community)'], ['snmp3', 'Printer, switch or firewall (SNMP v3 user)'], ['bmc', 'Server management (iLO, iDRAC, XClarity, IMM)'], ['vmware', 'VMware vCenter or ESXi'], ['nutanix', 'Nutanix Prism Element'], ['proxmox', 'Proxmox VE (API token or user)'], ['xen', 'XCP-ng or Citrix Hypervisor'],
  ['netssh', 'Network device (SSH user and password)'], ['netapi', 'Network device (API token or key)']];
const CATEGORIES: [string, string][] = [['', 'Any hardware'], ['computer', 'Computers and laptops'], ['server', 'Physical servers'], ['virtualization', 'Hypervisors (VMware, Nutanix, Proxmox, XCP-ng)'],
  ['firewall', 'Firewalls'], ['switch', 'Switches'], ['wireless', 'Access points and controllers'], ['printer', 'Printers'], ['other', 'Other']];
const DEFAULT_KIND: Record<string, string> = { computer: 'windows', server: 'bmc', virtualization: 'vmware', firewall: 'netssh', switch: 'netssh', wireless: 'netssh', printer: 'snmp' };
const VENDORS = ['HPE iLO', 'Dell iDRAC', 'Lenovo XClarity', 'IBM IMM', 'VMware', 'Nutanix', 'Fortinet FortiGate', 'Cisco', 'Cisco Meraki', 'HPE Aruba', 'Ubiquiti', 'Sophos', 'SonicWall', 'Palo Alto'];
const ACCOUNTS: [string, string][] = [['domain', 'Domain account'], ['service', 'Service account'], ['local', 'Local account']];
const kindName = (k: string) => KINDS.find(([x]) => x === k)?.[1] ?? k;
const catName = (k: string) => CATEGORIES.find(([x]) => x === k)?.[1] ?? k;
const acctName = (k: string) => ACCOUNTS.find(([x]) => x === k)?.[1] ?? '';
const catOrder = (k: string) => CATEGORIES.findIndex(([x]) => x === k);

/** Logins the collectors use to read and install on computers, and later to read servers and network gear. Each belongs to one customer (or is shared) and one hardware category. */
export default function Credentials() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/assets/discover/creds'); const companies = useLoad<Rec[]>('/api/assets/companies');
  const [edit, setEdit] = useState<Rec | null>(null); const [co, setCo] = useState(''); const [cat, setCat] = useState('');
  const list = Array.isArray(companies.data) ? companies.data : [];
  const items = ((data?.items ?? []) as Rec[])
    .filter((c) => co === '' || (co === 'shared' ? c.companyId == null : String(c.companyId) === co))
    .filter((c) => cat === '' || c.category === cat)
    .sort((a, b) => String(a.company || '').localeCompare(String(b.company || '')) || catOrder(a.category) - catOrder(b.category) || String(a.name).localeCompare(String(b.name)));
  const owner = (c: Rec) => (c.company as string) || 'Every customer';
  return <>
    <h1>Credentials</h1>
    <p className="muted">Logins the collectors use to read hardware and software details and to install the agent. They are kept per customer and per hardware category (computers, servers, VMware and Nutanix, firewalls, switches, access points). A login saved for one customer is never used for another. Passwords and keys are encrypted and never shown again.</p>
    <div className="tools"><button className="primary" onClick={() => setEdit({ kind: 'windows', accountType: 'domain', companyId: co && co !== 'shared' ? co : '', category: cat })}>Add credential</button>
      <select value={co} onChange={(e) => setCo(e.target.value)} aria-label="Customer"><option value="">All customers</option><option value="shared">Shared by every customer</option>{(list as Rec[]).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
      <select value={cat} onChange={(e) => setCat(e.target.value)} aria-label="Hardware category"><option value="">All hardware</option>{CATEGORIES.filter(([k]) => k).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No credentials here yet.</Empty> : <table><thead><tr><th>Hardware</th><th>Vendor</th><th>Name</th><th>Account</th><th>Used for</th><th>User</th><th /></tr></thead><tbody>
      {items.map((c, i) => <Fragment key={c.id}>
        {(i === 0 || owner(items[i - 1]) !== owner(c)) && <tr className="group"><th colSpan={7} scope="colgroup">{owner(c)}</th></tr>}
        <tr><td>{catName(c.category) || 'Any hardware'}</td><td>{c.vendor}</td><td>{c.name}{c.notes ? <div className="muted">{c.notes}</div> : null}</td><td>{acctName(c.accountType)}</td><td>{kindName(c.kind)}</td><td>{c.kind === 'snmp' ? '' : c.username}</td>
          <td><button onClick={() => setEdit({ ...c, companyId: c.companyId ?? '' })}>Edit</button> <Act label="Delete" danger confirm={`Delete the credential "${c.name}"?`} run={async () => { await api(`/api/assets/discover/creds/${c.id}`, { method: 'DELETE' }); reload(); }} /></td></tr>
      </Fragment>)}</tbody></table>)}
    {edit && <CredForm init={edit} companies={list as Rec[]} onClose={(ch) => { setEdit(null); if (ch) reload(); }} />}
  </>;
}

function CredForm({ init, companies, onClose }: { init: Rec; companies: Rec[]; onClose: (changed: boolean) => void }) {
  const isNew = !init.id;
  const [v, setV] = useState({ name: init.name ?? '', kind: init.kind ?? 'windows', accountType: init.accountType ?? 'domain', companyId: String(init.companyId ?? ''), username: init.username ?? '', secret: '',
    category: init.category ?? '', vendor: init.vendor ?? '', notes: init.notes ?? '', authProto: init.authProto || 'SHA', privProto: init.privProto ?? (isNew ? 'AES' : ''), privSecret: '' });
  const f = useSubmit(() => onClose(true)); const set = (k: string, x: string) => setV({ ...v, [k]: x });
  const body = { ...v, companyId: v.companyId ? Number(v.companyId) : null };
  const pickCategory = (c: string) => setV({ ...v, category: c, kind: isNew && DEFAULT_KIND[c] ? DEFAULT_KIND[c] : v.kind });
  const computerKind = ['windows', 'ssh', 'sshpw', 'login'].includes(v.kind);
  return <Modal title={isNew ? 'Add credential' : 'Edit credential'} onClose={() => onClose(false)}><div className="form">
    <Field label="Customer"><select value={v.companyId} onChange={(e) => set('companyId', e.target.value)}><option value="">Every customer (shared)</option>{companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
    <div className="row"><Field label="Hardware category"><select value={v.category} onChange={(e) => pickCategory(e.target.value)}>{CATEGORIES.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
      <Field label="Vendor or product"><input value={v.vendor} list="cred-vendors" onChange={(e) => set('vendor', e.target.value)} placeholder="HPE iLO, FortiGate…" /><datalist id="cred-vendors">{VENDORS.map((x) => <option key={x} value={x} />)}</datalist></Field></div>
    <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} placeholder="Domain admin (read only)" autoFocus /></Field>
    <div className="row">{computerKind && <Field label="Account"><select value={v.accountType} onChange={(e) => set('accountType', e.target.value)}>{ACCOUNTS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>}
      <Field label="Used for"><select value={v.kind} disabled={!isNew} onChange={(e) => set('kind', e.target.value)}>{KINDS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field></div>
    {v.kind !== 'snmp' && <Field label={v.kind === 'netapi' ? 'User name (optional)' : 'User name'}><input value={v.username} onChange={(e) => set('username', e.target.value)} autoComplete="off" placeholder={computerKind && v.accountType === 'domain' ? 'DOMAIN\\user' : computerKind && v.accountType === 'local' ? '.\\Administrator' : ''} /></Field>}
    {v.kind === 'snmp3' && <Field label="Authentication"><select value={v.authProto} onChange={(e) => set('authProto', e.target.value)}><option value="SHA">SHA (SHA-1)</option><option value="SHA256">SHA-256</option><option value="MD5">MD5</option></select></Field>}
    <Field label={v.kind === 'ssh' ? 'Private key' : v.kind === 'snmp' ? 'Community string' : v.kind === 'snmp3' ? 'Authentication password' : v.kind === 'netapi' ? 'API token or key' : 'Password'}>{v.kind === 'ssh' ? <textarea rows={5} value={v.secret} onChange={(e) => set('secret', e.target.value)} /> : <input type="password" value={v.secret} onChange={(e) => set('secret', e.target.value)} autoComplete="new-password" />}</Field>
    {v.kind === 'snmp3' && <div className="row"><Field label="Privacy (encryption)"><select value={v.privProto} onChange={(e) => set('privProto', e.target.value)}><option value="AES">AES-128</option><option value="DES">DES</option><option value="">None (authentication only)</option></select></Field>
      {v.privProto && <Field label="Privacy password"><input type="password" value={v.privSecret} onChange={(e) => set('privSecret', e.target.value)} autoComplete="new-password" /></Field>}</div>}
    {v.kind === 'snmp3' && <p className="muted">Use the user, protocols and passwords set on the device (security level authPriv, or authNoPriv when privacy is none). Passwords are at least 8 characters. Devices that answer only SNMP v3 are read the same way as with a community.</p>}
    {v.kind === 'netapi' && <p className="muted">Write the vendor: Fortinet (a REST API token), Palo Alto (an API key) or Cisco Meraki (a Dashboard API key; the controller needs internet access to api.meraki.com). Other makers are read over SSH.</p>}
    {v.kind === 'netssh' && <p className="muted">Used to back up the configuration. A read-only account is enough where the device has one. Writing the vendor helps when the device does not say its maker over SNMP.</p>}
    <Field label="Notes"><input value={v.notes} onChange={(e) => set('notes', e.target.value)} placeholder="Read-only account for the audit" maxLength={400} /></Field>
    {!isNew && <p className="muted">Leave the password or key empty to keep the saved one.</p>}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !v.name.trim()} onClick={() => f.submit(() => api(isNew ? '/api/assets/discover/creds' : `/api/assets/discover/creds/${init.id}`, { method: isNew ? 'POST' : 'PUT', body }))}>Save</button></div></div></Modal>;
}
