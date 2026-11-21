import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useId } from 'react';
import { useLoad } from '../http';
/**
 * A name box that offers the people read from the customer's domain controller as you type.
 * A customer without a domain controller (or someone not in it) can still be typed in by hand.
 */
export default function PersonInput({ value, onChange, companyId, label, placeholder, autoFocus }) {
    const id = useId();
    const q = value.trim();
    const { data } = useLoad(`/api/assets/directory-people?limit=30&search=${encodeURIComponent(q)}${companyId ? `&companyId=${companyId}` : ''}`, [q, companyId]);
    const people = (data?.items ?? []);
    return _jsxs(_Fragment, { children: [_jsx("input", { value: value, onChange: (e) => onChange(e.target.value), list: id, "aria-label": label, placeholder: placeholder, autoFocus: autoFocus, autoComplete: "off" }), _jsx("datalist", { id: id, children: people.map((p) => _jsx("option", { value: p.name, children: [p.email, p.department].filter(Boolean).join(' · ') }, p.companyId + ':' + p.account)) })] });
}
