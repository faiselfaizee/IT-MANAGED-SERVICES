import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import VendorApp from './VendorApp';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
const status = { enabled: true, vendorKey: true, signing: { ready: true, source: 'stored', kid: 'abcd' } };
const sub = { id: 's1', account: 'Beta Industries', kind: 'onprem', tenantId: '', plan: 'business', lines: { assets: { max_assets: 500 }, monitoring: { max_devices: 150 } },
  startsAt: '2026-11-19T00:00:00Z', endsAt: '2027-11-19T00:00:00Z', graceDays: 14, state: 'active', period: 'year', notes: '' };

test('lists subscriptions with their modules', async () => {
  vi.stubGlobal('fetch', vi.fn(async (u: string) => new Response(JSON.stringify(String(u).includes('/status') ? status : [{ sub, email: 'it@beta.test' }]), { status: 200 })));
  render(<VendorApp path={[]} />);
  await waitFor(() => expect(screen.getByText('Beta Industries')).toBeTruthy());
  expect(screen.getByText('Asset management 500, Monitoring 150')).toBeTruthy();
  expect(screen.getByText('set at activation')).toBeTruthy();
});

test('a new licence key is shown once, ready to copy', async () => {
  vi.stubGlobal('fetch', vi.fn(async (u: string, o?: RequestInit) => {
    const url = String(u);
    if (url.includes('/status')) return new Response(JSON.stringify(status), { status: 200 });
    if (url.endsWith('/keys') && o?.method === 'POST') return new Response(JSON.stringify({ id: 'k1', key: 'DKA-ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2', hint: 'XYZ2' }), { status: 200 });
    return new Response(JSON.stringify({ sub, account: { name: 'Beta Industries', email: 'it@beta.test' }, licences: [], keys: [] }), { status: 200 });
  }));
  render(<VendorApp path={['sub', 's1']} />);
  await waitFor(() => expect(screen.getByText('Create a licence key')).toBeTruthy());
  fireEvent.click(screen.getByText('Create a licence key'));
  await waitFor(() => expect(screen.getByDisplayValue('DKA-ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2')).toBeTruthy());
  expect(screen.getByText(/only time the whole key is shown/)).toBeTruthy();
});

test('signing is not ready: the console says so', async () => {
  vi.stubGlobal('fetch', vi.fn(async (u: string) => new Response(JSON.stringify(String(u).includes('/status') ? { ...status, signing: { ready: false, source: 'none' } } : []), { status: 200 })));
  render(<VendorApp path={[]} />);
  await waitFor(() => expect(screen.getByText(/import the licence signing key first/)).toBeTruthy());
});

const products = [
  { key: 'servicedesk', name: 'Service Desk', tagline: '', sort: 10, onSale: true, built: true, sizes: 1 },
  { key: 'demo', name: 'Demo product', tagline: '', sort: 20, onSale: true, built: false, sizes: 1 },
];
const plan = { id: 7, module: 'demo', sizeLabel: 'Up to 10 users', limits: { users: 10 }, prices: { saas: { usd: { year: 100 } } }, sort: 10, active: true };

test('a product that is not part of the program can be deleted; the others cannot', async () => {
  const calls: string[] = [];
  vi.stubGlobal('confirm', () => true);
  vi.stubGlobal('fetch', vi.fn(async (u: string, o?: RequestInit) => {
    const url = String(u); calls.push((o?.method ?? 'GET') + ' ' + url);
    if (url.includes('/status')) return new Response(JSON.stringify(status), { status: 200 });
    if (url.endsWith('/products')) return new Response(JSON.stringify(products), { status: 200 });
    if (url.endsWith('/plans')) return new Response(JSON.stringify([plan]), { status: 200 });
    return new Response(JSON.stringify({ deleted: true }), { status: 200 });
  }));
  render(<VendorApp path={['prices']} />);
  await waitFor(() => expect(screen.getAllByText('Delete').length).toBe(1));
  fireEvent.click(screen.getByText('Delete'));
  await waitFor(() => expect(calls).toContain('DELETE /api/commerce/products/demo'));
});

test('another size is added to an existing product with its limits and prices', async () => {
  const calls: { url: string; method?: string; body?: string }[] = [];
  vi.stubGlobal('fetch', vi.fn(async (u: string, o?: RequestInit) => {
    const url = String(u); calls.push({ url, method: o?.method, body: o?.body as string });
    if (url.includes('/status')) return new Response(JSON.stringify(status), { status: 200 });
    if (url.endsWith('/products')) return new Response(JSON.stringify(products), { status: 200 });
    if (url.endsWith('/plans') && o?.method !== 'POST') return new Response(JSON.stringify([plan]), { status: 200 });
    return new Response(JSON.stringify({ saved: true }), { status: 200 });
  }));
  render(<VendorApp path={['prices']} />);
  await waitFor(() => expect(screen.getByText('Add a size')).toBeTruthy());
  fireEvent.click(screen.getByText('Add a size'));
  fireEvent.change(screen.getByLabelText('Product'), { target: { value: 'demo' } });
  fireEvent.change(screen.getByLabelText('Size name, for example Up to 25 users'), { target: { value: 'Up to 25 users' } });
  fireEvent.change(screen.getByLabelText(/What it allows/), { target: { value: 'users=25' } });
  fireEvent.change(screen.getByLabelText('SaaS yearly price (USD)'), { target: { value: '220' } });
  fireEvent.click(screen.getByText('Add size'));
  await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url.endsWith('/api/commerce/plans'))).toBe(true));
  const post = calls.find((c) => c.method === 'POST' && c.url.endsWith('/api/commerce/plans'))!;
  expect(JSON.parse(post.body!)).toMatchObject({ module: 'demo', sizeLabel: 'Up to 25 users', limits: { users: 25 }, prices: { saas: { usd: { year: 220 } } } });
});
