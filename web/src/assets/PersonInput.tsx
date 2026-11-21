import { useId } from 'react';
import { useLoad, type Rec } from '../http';

/**
 * A name box that offers the people read from the customer's domain controller as you type.
 * A customer without a domain controller (or someone not in it) can still be typed in by hand.
 */
export default function PersonInput({ value, onChange, companyId, label, placeholder, autoFocus }: { value: string; onChange: (v: string) => void; companyId?: number | null; label?: string; placeholder?: string; autoFocus?: boolean }) {
  const id = useId(); const q = value.trim();
  const { data } = useLoad<Rec>(`/api/assets/directory-people?limit=30&search=${encodeURIComponent(q)}${companyId ? `&companyId=${companyId}` : ''}`, [q, companyId]);
  const people = ((data?.items ?? []) as Rec[]);
  return <>
    <input value={value} onChange={(e) => onChange(e.target.value)} list={id} aria-label={label} placeholder={placeholder} autoFocus={autoFocus} autoComplete="off" />
    <datalist id={id}>{people.map((p) => <option key={p.companyId + ':' + p.account} value={p.name}>{[p.email, p.department].filter(Boolean).join(' · ')}</option>)}</datalist>
  </>;
}
