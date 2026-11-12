import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Licence from './Licence';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
const lic = { state: 'valid', plan: 'standard', tenant: 'acme', expires: '2027-01-01T00:00:00Z', graceUntil: '2027-01-15T00:00:00Z', modules: [{ key: 'assets', limits: { max_assets: 100 } }] };

test('shows what the licence covers and what is missing', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(lic), { status: 200 })));
  render(<Licence />);
  await waitFor(() => expect(screen.getByText('Valid')).toBeTruthy());
  expect(screen.getByText('max assets: 100')).toBeTruthy();
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
