import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import ServiceDeskApp from './ServiceDeskApp';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); location.hash = ''; });
const me = { id: 'c1', name: 'Cat', role: 'customer', staff: false, customer: true, companyId: 1, canRaise: true, canModify: true };
const item = { id: 3, name: 'New laptop', category: 'Hardware', description: 'For a new starter', needsApproval: true, resolveHours: 72,
  fields: [{ key: 'model', label: 'Model', kind: 'choice', options: ['Standard', 'Developer'], required: true }] };

test('a customer asks for an item from the catalogue and is told it waits for approval', async () => {
  location.hash = '#/servicedesk/catalog';
  const sent: unknown[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (url === '/api/servicedesk/me') return new Response(JSON.stringify(me));
    if (url === '/api/servicedesk/catalog') return new Response(JSON.stringify([item]));
    if (url === '/api/servicedesk/catalog/3/request') { sent.push(JSON.parse(String(init?.body))); return new Response(JSON.stringify({ id: 9, number: 'SRV-0009', approval: 'pending' }), { status: 201 }); }
    return new Response('{}', { status: 404 });
  }));
  render(<ServiceDeskApp />);
  fireEvent.click(await screen.findByText('Request'));
  expect(screen.queryByText('Approvals')).toBeNull();   // only staff approve
  fireEvent.change(screen.getByLabelText('Model *'), { target: { value: 'Developer' } });
  fireEvent.click(screen.getByText('Send request'));
  await waitFor(() => expect(screen.getByText('SRV-0009')).toBeTruthy());
  expect(screen.getByText(/waits for approval/)).toBeTruthy();
  expect(sent[0]).toMatchObject({ answers: { model: 'Developer' } });
});
