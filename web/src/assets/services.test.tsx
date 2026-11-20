import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import { ServicesPanel, UninstallForm } from './Services';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const list = { hasAgent: true, canControl: true, canLogon: true, platform: 'windows', at: new Date().toISOString(), items: [
  { name: 'Spooler', display: 'Print Spooler', state: 'stopped', start: 'auto', logon: 'LocalSystem' },
  { name: 'MSSQLSERVER', display: 'SQL Server', state: 'running', start: 'delayed', logon: 'CORP\\svc_sql' }] };

test('a service starts through the agent and shows the state the computer reported', async () => {
  const calls: { url: string; body?: string }[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, body: init?.body as string });
    if (url.endsWith('/services')) return new Response(JSON.stringify(list), { status: 200 });
    if (url.endsWith('/services/action')) return new Response(JSON.stringify({ id: 'j1' }), { status: 200 });
    return new Response(JSON.stringify({ status: 'done', output: 'Print Spooler (Spooler) is now Running.', service: { name: 'Spooler', state: 'running', start: 'auto', logon: 'LocalSystem' } }), { status: 200 });
  }));
  render(<ServicesPanel assetId="7" />);
  await waitFor(() => expect(screen.getByText('Print Spooler')).toBeTruthy());
  expect(screen.getByText('CORP\\svc_sql')).toBeTruthy(); expect(screen.getByText('Automatic (delayed start)')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Start' }));
  await waitFor(() => expect(screen.getByText(/is now Running/)).toBeTruthy());
  expect(JSON.parse(calls.find((c) => c.url.endsWith('/services/action'))!.body!)).toEqual({ name: 'Spooler', action: 'start' });
  expect(screen.getAllByText('Running', { selector: '.badge' }).length).toBe(2);   // the row took the reported state
});

test('Log On needs a reason, and an account needs a matching password', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(list), { status: 200 })));
  render(<ServicesPanel assetId="7" />);
  await waitFor(() => expect(screen.getByText('SQL Server')).toBeTruthy());
  fireEvent.click(screen.getAllByRole('button', { name: 'Log On' })[1]);
  const go = screen.getByRole('button', { name: 'Change account' }) as HTMLButtonElement;
  expect(screen.getByRole('button', { name: 'Fill from Vault' })).toBeTruthy();
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'a' } });
  fireEvent.change(screen.getByLabelText('Confirm the password'), { target: { value: 'b' } });
  fireEvent.change(screen.getByLabelText('Reason'), { target: { value: 'CHG-1' } });
  expect(go.disabled).toBe(true); expect(screen.getByText('The passwords are not the same.')).toBeTruthy();
  fireEvent.change(screen.getByLabelText('Confirm the password'), { target: { value: 'a' } });
  expect(go.disabled).toBe(false);
});

test('uninstall asks for a reason before it sends anything', () => {
  const f = vi.fn(); vi.stubGlobal('fetch', f);
  render(<UninstallForm assetId="7" app={{ name: '7-Zip', version: '23.01' }} onClose={() => undefined} onDone={() => undefined} />);
  const btn = screen.getByRole('button', { name: 'Uninstall' }) as HTMLButtonElement;
  expect(btn.disabled).toBe(true);
  fireEvent.change(screen.getByLabelText('Reason'), { target: { value: 'not needed' } });
  expect(btn.disabled).toBe(false); expect(f).not.toHaveBeenCalled();
});
