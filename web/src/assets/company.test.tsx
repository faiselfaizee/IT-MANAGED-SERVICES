import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import { CompanyUsers } from './CompanyPage';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function api(routes: Record<string, unknown>, calls: [string, string, string][]) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, o?: RequestInit) => {
    calls.push([o?.method ?? 'GET', String(url), String(o?.body ?? '')]);
    if (String(url) === '/api/platform/users' && o?.method === 'POST') return new Response(JSON.stringify({ id: 'new1' }), { status: 201 });
    const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
    return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{"ok":true}', { status: 200 });
  }));
}
const users = [
  { id: 'u1', email: 'ana@acme.test', name: 'Ana Lee', role: 'cadmin', active: true },
  { id: 'u2', email: 'bo@acme.test', name: 'Bo', role: 'customer', active: true },
  { id: 'u3', email: 'cy@globex.test', name: 'Cy', role: 'customer', active: true },
  { id: 'u4', email: 'dee@free.test', name: 'Dee', role: 'customer', active: true },
  { id: 's1', email: 'eng@msp.test', name: 'Eng', role: 'engineer', active: true },
];

test("a company's users show their ticket rights and how many assets each holds", async () => {
  const calls: [string, string, string][] = [];
  api({ '/api/platform/mode': { singleCompany: false }, '/api/platform/users': users, '/api/platform/access/user-companies': [{ userId: 'u1', companyId: 4 }, { userId: 'u3', companyId: 9 }],
    '/api/servicedesk/people': [{ id: 'u1', companyId: 4, mayRaise: true, mayModify: false }, { id: 'u2', companyId: 4, mayRaise: false, mayModify: true }],
    '/api/assets/holders': { items: [{ name: 'Ana Lee', companyId: 4, assets: 2 }, { name: 'ana@acme.test', companyId: 4, assets: 1 }] } }, calls);
  render(<CompanyUsers company={{ id: 4, name: 'Acme' }} />);
  await waitFor(() => expect(screen.getByText('Ana Lee')).toBeTruthy());
  expect(screen.getByText('Bo')).toBeTruthy();   // tied by the older Service Desk link
  expect(screen.queryByText('Cy')).toBeNull(); expect(screen.queryByText('Eng')).toBeNull();
  expect(screen.getByLabelText('3 assets held by Ana Lee')).toBeTruthy();   // found under her name and her email
  expect((screen.getByLabelText('Ana Lee may reply and close') as HTMLInputElement).checked).toBe(false);
  fireEvent.click(screen.getByLabelText('Bo may open tickets'));
  await waitFor(() => expect(calls.some(([m, u, b]) => m === 'PUT' && u === '/api/servicedesk/people/u2' && b.includes('"mayRaise":true') && b.includes('"companyId":4'))).toBe(true));
  expect(screen.getByText('Add an existing user (1)')).toBeTruthy();   // Dee belongs to no company
});

test('adding a user to a company creates the account and ties it to the company with its rights', async () => {
  const calls: [string, string, string][] = [];
  api({ '/api/platform/mode': { singleCompany: false }, '/api/platform/users': [], '/api/platform/access/user-companies': [], '/api/servicedesk/people': [], '/api/assets/holders': { items: [] } }, calls);
  render(<CompanyUsers company={{ id: 4, name: 'Acme' }} />);
  fireEvent.click(await screen.findByText('Add user'));
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Sam' } });
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'sam@acme.test' } });
  fireEvent.change(screen.getByLabelText('Role'), { target: { value: 'cadmin' } });
  fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'Long-Enough-1' } });
  fireEvent.click(screen.getByLabelText('May reply and close'));
  fireEvent.click(screen.getAllByText('Add user').at(-1)!);
  await waitFor(() => expect(calls.some(([m, u]) => m === 'PUT' && u === '/api/servicedesk/people/new1')).toBe(true));
  expect(calls.find(([m, u]) => m === 'POST' && u === '/api/platform/users')![2]).toContain('"role":"cadmin"');
  expect(calls.find(([m, u]) => m === 'PUT' && u === '/api/platform/access/user-companies/new1')![2]).toContain('"companyId":4');
  expect(calls.find(([m, u]) => m === 'PUT' && u === '/api/servicedesk/people/new1')![2]).toContain('"mayModify":false');
});

test('people picked from the domain controller get accounts that sign in with Windows', async () => {
  const calls: [string, string, string][] = [];
  api({ '/api/platform/mode': { singleCompany: false }, '/api/platform/users': [{ id: 'x', email: 'old@acme.test', name: 'Old', role: 'customer', active: true }], '/api/platform/access/user-companies': [], '/api/servicedesk/people': [], '/api/assets/holders': { items: [] },
    '/api/assets/directory-people': { items: [{ companyId: 4, account: 'ana', name: 'Ana Lee', email: 'ana@acme.test', department: 'Sales', enabled: true }, { companyId: 4, account: 'nomail', name: 'No Mail', email: '', enabled: true }, { companyId: 4, account: 'old', name: 'Old', email: 'old@acme.test', enabled: true }] },
    '/api/companies/4/users/from-directory': { created: 1, linked: 0, skipped: 0 } }, calls);
  render(<CompanyUsers company={{ id: 4, name: 'Acme' }} />);
  fireEvent.click(await screen.findByText('Pick from the domain controller'));
  await waitFor(() => expect(screen.getByLabelText('Choose Ana Lee')).toBeTruthy());
  expect((screen.getByLabelText('Choose No Mail') as HTMLInputElement).disabled).toBe(true);
  expect((screen.getByLabelText('Choose Old') as HTMLInputElement).disabled).toBe(true);
  fireEvent.click(screen.getByLabelText('Choose Ana Lee'));
  fireEvent.click(screen.getByText('Add 1'));
  await waitFor(() => expect(screen.getByText('1 added, 0 linked, 0 skipped.')).toBeTruthy());
  const body = calls.find(([m, u]) => m === 'POST' && u === '/api/companies/4/users/from-directory')![2];
  expect(body).toContain('"account":"ana"'); expect(body).toContain('"role":"customer"');
});

test("a company's own Microsoft sign-in is set up on its Sign-in tab, with company roles", async () => {
  const calls: [string, string, string][] = [];
  api({ '/api/platform/mode': { singleCompany: false }, '/api/assets/companies': [{ id: 4, name: 'Acme' }],
    '/api/companies/4/sso/microsoft': { enabled: false, tenantId: '', clientId: '', hasSecret: false, domains: '', autoCreate: true, defaultRole: 'customer', redirectUri: 'https://x.test/account/sso/microsoft/callback' },
    '/api/companies/4/sso/google': { enabled: false, domains: '' } }, calls);
  location.hash = '#/setup/companies/4/signin';
  const { default: CompanyPage } = await import('./CompanyPage');
  render(<CompanyPage />);
  fireEvent.click((await screen.findAllByText('Set up…'))[0]);
  await waitFor(() => expect(screen.getByText('Acme: sign in with Microsoft')).toBeTruthy());
  const roles = [...(screen.getByLabelText('Role of new people') as HTMLSelectElement).options].map((o) => o.value);
  expect(roles).toEqual(['customer', 'cadmin']);
  fireEvent.change(screen.getByLabelText('Email domains of this company (required)'), { target: { value: 'acme.com' } });
  fireEvent.click(screen.getByText('Save'));
  await waitFor(() => expect(calls.some(([m, u, b]) => m === 'PUT' && u === '/api/companies/4/sso/microsoft' && b.includes('"domains":"acme.com"'))).toBe(true));
  location.hash = '';
});
