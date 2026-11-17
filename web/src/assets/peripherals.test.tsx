import { cleanup, render, screen, waitFor, fireEvent } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import { AttachedCard, People, Stock } from './Peripherals';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function mockApi(routes: Record<string, unknown>) {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
    return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{}', { status: 404 });
  }));
}

test('an asset shows the accessories attached to it with their serial numbers', () => {
  const data = { parent: null, peripheral: false, items: [{ id: 5, tag: 'AST-5', name: 'Dell keyboard', type: 'Keyboard', serial: 'KB-222', status: 'in_use', warrantyEnd: '2029-01-10' }] };
  render(<AttachedCard id="1" d={{ tag: 'AST-1', name: 'PC', identity: { serial: 'D-1' } }} data={data} reload={() => undefined} canEdit />);
  expect(screen.getByText('KB-222')).toBeTruthy();
  expect(screen.getByText('Add accessories…')).toBeTruthy();
});

test('an accessory names the main device it is attached to by serial number', () => {
  const data = { parent: { id: 1, tag: 'AST-1', name: 'PC', serial: 'DESK-111', assignedTo: 'ann@x.test' }, peripheral: true, items: [] };
  render(<AttachedCard id="5" d={{}} data={data} reload={() => undefined} canEdit={false} />);
  expect(screen.getByText('DESK-111')).toBeTruthy();
});

test('People lists who holds what', async () => {
  mockApi({ '/api/platform/access/mine': {}, '/api/platform/me': { role: 'admin' }, '/api/assets/holders': { items: [{ name: 'bob@x.test', companyId: null, company: '', assets: 3, accessories: 2 }] } });
  render(<People />);
  await waitFor(() => expect(screen.getByText('bob@x.test')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Search people'), { target: { value: 'bo' } });
});

test('Stock counts each kind', async () => {
  mockApi({ '/api/assets/companies': [], '/api/assets/stock': { items: [{ type: 'Keyboard', peripheral: true, stock: 4, inUse: 10, repair: 1, retired: 0, lost: 0, total: 15 }] } });
  render(<Stock />);
  await waitFor(() => expect(screen.getByText('Keyboard')).toBeTruthy());
  expect(screen.getByText('15')).toBeTruthy();
});
