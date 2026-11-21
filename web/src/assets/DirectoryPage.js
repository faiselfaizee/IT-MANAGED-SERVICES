import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useState } from 'react';
import { useLoad } from '../http';
import { Empty, Loading } from '../ui';
import DirectorySetup from './DirectorySetup';
/** Devices setup › Domain controller, for an installation with one company: the same setup the Customers page offers on each customer. */
export default function DirectoryPage() {
    const { data, error, loading } = useLoad('/api/assets/companies');
    const [open, setOpen] = useState(false);
    const co = (Array.isArray(data) ? data : [])[0];
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Domain controller" }), _jsx("p", { className: "muted", children: "Read the people of your domain controller so they can be listed under Inventory \u203A People and chosen when assets are handed out." }), _jsx(Loading, { loading: loading && !data, error: error }), data && !co && _jsx(Empty, { children: "Add your first device and the company is created for you; then come back here." }), co && _jsx("p", { children: _jsx("button", { className: "primary", onClick: () => setOpen(true), children: "Set up the domain controller" }) }), open && co && _jsx(DirectorySetup, { company: { id: co.id, name: co.name }, onClose: () => setOpen(false) })] });
}
