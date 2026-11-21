import { jsx as _jsx } from "react/jsx-runtime";
import { afterEach, expect, test, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { Services } from './Services';
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
const list = [
    { name: 'MSSQLSERVER', display: 'SQL Server', state: 'stopped', start: 'auto', watched: true, mode: null, quiet: false },
    { name: 'Spooler', display: 'Print Spooler', state: 'running', start: 'auto', watched: true, mode: null, quiet: false },
    { name: 'gupdate', display: 'Google Update', state: 'stopped', start: 'auto', watched: false, mode: null, quiet: true },
    { name: 'W32Time', display: 'Windows Time', state: 'stopped', start: 'manual', watched: false, mode: null, quiet: false },
];
test('lists the watched services, lets a service be ignored or chosen, and adds one by name', async () => {
    const calls = [];
    vi.stubGlobal('fetch', vi.fn(async (url, init) => { calls.push({ url: String(url), body: String(init?.body ?? '') }); return new Response('{}', { status: 200 }); }));
    const changed = vi.fn();
    render(_jsx(Services, { id: "5", list: list, at: null, custom: ['MyApp'], watched: [{ kind: 'service', name: 'MyApp', running: false, state: 'stopped' }], canEdit: true, agent: true, kind: "server", onChange: changed }));
    expect(screen.getByText('2 of 4 services watched, 1 not running')).toBeTruthy();
    expect(screen.queryByText('Google Update')).toBeNull(); // not watched: hidden until "All services"
    fireEvent.click(screen.getByLabelText('Watch MSSQLSERVER'));
    await waitFor(() => expect(calls.some((c) => c.body === JSON.stringify({ name: 'MSSQLSERVER', mode: 'ignore' }))).toBe(true));
    fireEvent.change(screen.getByLabelText('Which services'), { target: { value: 'all' } });
    fireEvent.click(screen.getByLabelText('Watch W32Time'));
    await waitFor(() => expect(calls.some((c) => c.body === JSON.stringify({ name: 'W32Time', mode: 'watch' }))).toBe(true));
    expect(screen.getByText(/often stops by itself/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Service to add'), { target: { value: 'Backup Exec' } });
    fireEvent.click(screen.getByText('Watch it'));
    await waitFor(() => expect(calls.some((c) => c.body === JSON.stringify({ name: 'Backup Exec', mode: 'watch' }))).toBe(true));
    expect(screen.getByText('MyApp')).toBeTruthy();
});
test('a device without an agent points to service checks', () => {
    render(_jsx(Services, { id: "9", list: [], at: null, custom: [], watched: [], canEdit: true, agent: false, kind: "network", onChange: () => { } }));
    expect(screen.getByText(/Service checks tab/)).toBeTruthy();
});
