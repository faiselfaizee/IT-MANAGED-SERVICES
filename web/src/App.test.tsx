import { render, screen, waitFor } from '@testing-library/react';
import { expect, test } from 'vitest';
import App, { MODULES } from './App';

const ok = (async () => new Response(JSON.stringify({ tenant: 'acme', name: 'Acme Ltd' }), { status: 200 })) as unknown as typeof fetch;
const notFound = (async () => new Response('{}', { status: 404 })) as unknown as typeof fetch;

test('shows the customer name', async () => {
  render(<App fetcher={ok} />);
  await waitFor(() => expect(screen.getByText('Welcome, Acme Ltd')).toBeTruthy());
});
test('unknown address shows a message', async () => {
  render(<App fetcher={notFound} />);
  await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('not set up'));
});
test('lists all six modules', () => { expect(MODULES.length).toBe(6); });

test('customers see no placeholder tiles for products they cannot open', async () => {
  const { Home } = await import('./App');
  const { container } = render(<Home who={{ tenant: 'acme', name: 'Acme' }} live={false} assets={true} customer={true} />);
  expect(container.querySelectorAll('.tile-soon').length).toBe(0);
  const staff = render(<Home who={{ tenant: 'acme', name: 'Acme' }} live={false} assets={true} />);
  expect(staff.container.querySelectorAll('.tile-soon').length).toBeGreaterThan(0);
});
