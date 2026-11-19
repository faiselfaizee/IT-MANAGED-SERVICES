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
