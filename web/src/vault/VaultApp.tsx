import { useEffect, useState } from 'react';
import { useHash, useInterval, useLoad, type Rec } from '../http';
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
  const [err, setErr] = useState(''); const [reset, setReset] = useState(false); const [inSetup, setInSetup] = useState(false);
  const pending = useLoad<Rec>(session.unlocked ? '/api/vault/requests?scope=approve' : null, [session.unlocked]);
  useEffect(() => { if (!session.me) S.loadMe().catch((e: Error) => setErr(e.message)); }, [session.me]);
  // give waiting members their keys while a manager has the vault open
  useInterval(() => { if (session.unlocked) { void S.givePending().catch(() => undefined); pending.reload(); } }, 120_000);
  if (err) return <p className="err" role="alert">{err}</p>;
  if (!session.me) return <Loading loading error="" />;
  if (!session.me.setUp || reset || inSetup) return <SetUp reset={reset} onStart={() => setInSetup(true)} onDone={() => { setInSetup(false); setReset(false); }} />;
  if (!session.unlocked) return <Unlock onReset={() => setReset(true)} />;
  const admin = session.me.settings?.role === 'admin';
  const waiting = ((pending.data?.items ?? []) as Rec[]).length;
  let page;
  switch (a) {
    case 'vaults': page = <Vaults />; break;
    case 'requests': page = <Requests />; break;
    case 'sends': page = <Sends />; break;
    case 'managed': page = <Managed />; break;
    case 'health': page = <Health />; break;
    case 'access': page = <Access />; break;
    case 'tools': page = <Tools />; break;
    case 'account': page = <Account />; break;
    case 'settings': page = admin ? <Settings /> : <Items />; break;
    default: page = <Items key={location.hash} />;
  }
  const section = (a ?? '').split('?')[0];
  const groups = [
    { group: 'Overview', items: [{ key: '', label: 'All items' }] },
    { group: 'Items', items: [{ key: 'vaults', label: 'Vaults' }, { key: 'sends', label: 'Send links' }, { key: 'managed', label: 'Managed accounts' }] },
    { group: 'Workflow', items: [{ key: 'requests', label: 'Requests', badge: waiting }] },
    { group: 'Reports', items: [{ key: 'health', label: 'Password health' }, { key: 'access', label: 'Access report' }] },
    { group: 'Settings', items: [{ key: 'account', label: 'My key' }, { key: 'tools', label: 'Tools' }, ...(admin ? [{ key: 'settings', label: 'Settings' }] : [])] },
  ];
  return <ModNav title="Vault" base="vault" groups={groups} section={section}
    head={<button className="lock-btn" onClick={S.lock} title="Forget the keys in this browser">Lock the vault</button>}>{page}</ModNav>;
}
