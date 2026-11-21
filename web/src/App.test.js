import { jsx as _jsx } from "react/jsx-runtime";
import { render, screen, waitFor } from '@testing-library/react';
import { expect, test } from 'vitest';
import App, { MODULES } from './App';
const ok = (async () => new Response(JSON.stringify({ tenant: 'acme', name: 'Acme Ltd' }), { status: 200 }));
const notFound = (async () => new Response('{}', { status: 404 }));
test('shows the customer name', async () => {
    render(_jsx(App, { fetcher: ok }));
    await waitFor(() => expect(screen.getByText('Welcome, Acme Ltd')).toBeTruthy());
});
test('unknown address shows a message', async () => {
    render(_jsx(App, { fetcher: notFound }));
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('not set up'));
});
test('lists all six modules', () => { expect(MODULES.length).toBe(6); });
test('a module the licence does not include has no tile, for customers and for staff', async () => {
    const { Home } = await import('./App');
    const cust = render(_jsx(Home, { who: { tenant: 'acme', name: 'Acme' }, live: false, assets: true, customer: true }));
    expect(cust.container.querySelectorAll('.tile-soon').length).toBe(0);
    cust.unmount();
    const staff = render(_jsx(Home, { who: { tenant: 'acme', name: 'Acme' }, live: false, assets: true, deskOn: false }));
    expect(staff.container.querySelectorAll('.tile-soon').length).toBe(0);
    expect(staff.container.querySelector('#t-desk')).toBeNull();
    expect(staff.container.querySelector('#t-assets')).not.toBeNull();
});
