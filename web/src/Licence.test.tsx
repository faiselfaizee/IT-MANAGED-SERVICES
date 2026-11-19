import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Licence from './Licence';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
const lic = { state: 'valid', plan: 'standard', tenant: 'acme', expires: '2027-01-01T00:00:00Z', graceUntil: '2027-01-15T00:00:00Z', modules: [{ key: 'assets', limits: { max_assets: 100 } }] };

test('shows what the licence covers and what is missing', async () => {
  vi.stubGlobal('fetch', vi.fn(async (u: string) => new Response(JSON.stringify(String(u).includes('/usage') ? { 'assets:max_assets': 100, 'monitoring:max_devices': 40 } : lic), { status: 200 })));
  render(<Licence />);
  await waitFor(() => expect(screen.getByText('Valid')).toBeTruthy());
  await waitFor(() => expect(screen.getByText(/Assets in service/).textContent).toBe('Assets in service: 100 / 100 (limit reached)'));
  expect(screen.getByText('Service Desk').closest('tr')?.textContent).toContain('No');
});

test('a rejected licence shows the reason', async () => {
  vi.stubGlobal('fetch', vi.fn(async (_u: string, o?: RequestInit) => o?.method === 'PUT'
    ? new Response(JSON.stringify({ error: 'license_rejected', reason: 'Signed by someone else.' }), { status: 400 })
    : new Response(JSON.stringify(lic), { status: 200 })));
  render(<Licence />);
  await waitFor(() => expect(screen.getByText('Valid')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Licence token'), { target: { value: 'abc.def.ghi' } });
  fireEvent.click(screen.getByText('Install licence'));
  await waitFor(() => expect(screen.getByText('Signed by someone else.')).toBeTruthy());
});

test('a licence key activates the installation; an ended licence says the modules are read-only', async () => {
  let activated = false;
  const fetch = vi.fn(async (u: string, _o?: RequestInit) => {
    const url = String(u);
    if (url.endsWith('/activate')) { activated = true; return new Response(JSON.stringify(lic), { status: 200 }); }
    if (url.endsWith('/activation')) return new Response(JSON.stringify({ installation: 'a'.repeat(32), activation: activated ? { activated: true, keyHint: 'WXYZ', activatedAt: '2026-11-19T00:00:00Z', lastCheck: null, lastResult: 'Activated.' } : { activated: false } }), { status: 200 });
    if (url.includes('/usage')) return new Response('{}', { status: 200 });
    return new Response(JSON.stringify({ ...lic, state: 'expired', modules: [], readOnly: ['assets'] }), { status: 200 });
  });
  vi.stubGlobal('fetch', fetch);
  render(<Licence />);
  await waitFor(() => expect(screen.getByText(/The licence has ended/)).toBeTruthy());
  expect(screen.getByText('a'.repeat(32))).toBeTruthy();   // the installation id for an offline licence
  fireEvent.change(screen.getByLabelText('Licence key'), { target: { value: 'DKA-ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2' } });
  fireEvent.click(screen.getByText('Activate'));
  await waitFor(() => expect(screen.getByText(/Key ending/)).toBeTruthy());
  const call = fetch.mock.calls.find((c) => String(c[0]).endsWith('/activate'))!;
  expect(JSON.parse(String((call[1] as RequestInit).body))).toEqual({ key: 'DKA-ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2' });
  expect(screen.getByText('Check now')).toBeTruthy();
});
