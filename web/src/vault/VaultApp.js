import { jsx as _jsx } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { useHash, useInterval, useLoad } from '../http';
import { Loading } from '../ui';
import * as S from './session';
import { SetUp, Unlock } from './Gate';
import Items from './Items';
import Vaults from './Vaults';
import Requests from './Requests';
import Sends from './Sends';
import Health from './Health';
import Managed from './Managed';
import Tools from './Tools';
import Account from './Account';
import Settings from './Settings';
import Access from './Access';
import { ModNav } from '../ModNav';
/** The Vault module: locked until the master password opens the keys in this browser. */
export default function VaultApp() {
    const [, a] = useHash();
    const session = S.useSession();
    const [err, setErr] = useState('');
    const [reset, setReset] = useState(false);
    const [inSetup, setInSetup] = useState(false);
    const pending = useLoad(session.unlocked ? '/api/vault/requests?scope=approve' : null, [session.unlocked]);
    useEffect(() => { if (!session.me)
        S.loadMe().catch((e) => setErr(e.message)); }, [session.me]);
    // give waiting members their keys while a manager has the vault open
    useInterval(() => { if (session.unlocked) {
        void S.givePending().catch(() => undefined);
        pending.reload();
    } }, 120_000);
    if (err)
        return _jsx("p", { className: "err", role: "alert", children: err });
    if (!session.me)
        return _jsx(Loading, { loading: true, error: "" });
    if (!session.me.setUp || reset || inSetup)
        return _jsx(SetUp, { reset: reset, onStart: () => setInSetup(true), onDone: () => { setInSetup(false); setReset(false); } });
    if (!session.unlocked)
        return _jsx(Unlock, { onReset: () => setReset(true) });
    const admin = session.me.settings?.role === 'admin';
    const waiting = (pending.data?.items ?? []).length;
    let page;
    switch (a) {
        case 'vaults':
            page = _jsx(Vaults, {});
            break;
        case 'requests':
            page = _jsx(Requests, {});
            break;
        case 'sends':
            page = _jsx(Sends, {});
            break;
        case 'managed':
            page = _jsx(Managed, {});
            break;
        case 'health':
            page = _jsx(Health, {});
            break;
        case 'access':
            page = _jsx(Access, {});
            break;
        case 'tools':
            page = _jsx(Tools, {});
            break;
        case 'account':
            page = _jsx(Account, {});
            break;
        case 'settings':
            page = admin ? _jsx(Settings, {}) : _jsx(Items, {});
            break;
        default: page = _jsx(Items, {}, location.hash);
    }
    const section = (a ?? '').split('?')[0];
    const groups = [
        { group: 'Overview', items: [{ key: '', label: 'All items' }] },
        { group: 'Items', items: [{ key: 'vaults', label: 'Vaults' }, { key: 'sends', label: 'Send links' }, { key: 'managed', label: 'Managed accounts' }] },
        { group: 'Workflow', items: [{ key: 'requests', label: 'Requests', badge: waiting }] },
        { group: 'Reports', items: [{ key: 'health', label: 'Password health' }, { key: 'access', label: 'Access report' }] },
        { group: 'Settings', items: [{ key: 'account', label: 'My key' }, { key: 'tools', label: 'Tools' }, ...(admin ? [{ key: 'settings', label: 'Settings' }] : [])] },
    ];
    return _jsx(ModNav, { title: "Vault", base: "vault", groups: groups, section: section, head: _jsx("button", { className: "lock-btn", onClick: S.lock, title: "Forget the keys in this browser", children: "Lock the vault" }), children: page });
}
