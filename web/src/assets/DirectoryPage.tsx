import { useState } from 'react';
import { useLoad, type Rec } from '../http';
import { Empty, Loading } from '../ui';
import DirectorySetup from './DirectorySetup';

/** Devices setup › Domain controller, for an installation with one company: the same setup the Customers page offers on each customer. */
export default function DirectoryPage() {
  const { data, error, loading } = useLoad<Rec[]>('/api/assets/companies'); const [open, setOpen] = useState(false);
  const co = (Array.isArray(data) ? data : [])[0];
  return <>
    <h1>Domain controller</h1>
    <p className="muted">Read the people of your domain controller so they can be listed under Inventory › People and chosen when assets are handed out.</p>
    <Loading loading={loading && !data} error={error} />
    {data && !co && <Empty>Add your first device and the company is created for you; then come back here.</Empty>}
    {co && <p><button className="primary" onClick={() => setOpen(true)}>Set up the domain controller</button></p>}
    {open && co && <DirectorySetup company={{ id: co.id, name: co.name }} onClose={() => setOpen(false)} />}
  </>;
}
