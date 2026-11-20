import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import StorePage from './StorePage';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
const info = { business: { legalName: 'DamazeTech Solutions (OPC) Private Limited', tradeName: 'DeskAsk', gstin: '33AAKCD8221B1Z3', supportEmail: 'servicedesk@deskask.in', tenantDomain: 'deskask.in', trialDays: 14, termsUrl: 'https://www.deskask.in/terms' }, open: true, trials: true, payments: 'test' };
const plans = [{ id: 2, module: 'servicedesk', moduleName: 'Service Desk', sizeLabel: '10 agents', limits: { agents: 10 }, inr: { year: 68000, month: 6800, onprem: 54400 }, usd: { year: 1190, month: 119, onprem: 955 } }];
const quote = { currency: 'INR', kind: 'saas', period: 'year', lines: [{ module: 'servicedesk', planId: 2, description: 'Service Desk, 10 agents, 1 year', amount: 68000 }], discount: 0, subtotal: 68000,
  taxes: [{ name: 'CGST', rate: 9, amount: 6120 }, { name: 'SGST', rate: 9, amount: 6120 }], tax: 12240, total: 80240, taxNote: '', licence: { servicedesk: { agents: 10 } } };

test('a size is priced with GST on the server and the store footer names the seller', async () => {
  const fetch = vi.fn(async (u: string, _o?: RequestInit) => new Response(JSON.stringify(String(u).includes("/info") ? info : String(u).includes("/plans") ? plans : quote), { status: 200 }));
  vi.stubGlobal('fetch', fetch);
  render(<StorePage path={['pricing']} />);
  await waitFor(() => expect(screen.getByLabelText('Service Desk size')).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Service Desk size'), { target: { value: '2' } });
  await waitFor(() => expect(screen.getByText('₹80,240')).toBeTruthy());
  expect(screen.getByText('CGST 9%')).toBeTruthy();
  const q = fetch.mock.calls.find((c) => String(c[0]).endsWith('/api/store/quote'))!;
  expect(JSON.parse(String((q[1] as RequestInit).body))).toMatchObject({ kind: 'saas', plans: { servicedesk: 2 }, country: 'IN', stateCode: '33' });
  expect(screen.getByText(/DamazeTech Solutions/)).toBeTruthy();
  expect(screen.getAllByText('Terms of Service').length).toBeGreaterThan(0);
  expect(screen.getByText(/Test mode/)).toBeTruthy();
});

test('a free trial checks the address and starts', async () => {
  vi.stubGlobal('fetch', vi.fn(async (u: string, o?: RequestInit) => {
    const url = String(u);
    if (url.includes('/info')) return new Response(JSON.stringify(info), { status: 200 });
    if (url.includes('/available')) return new Response(JSON.stringify({ available: true, address: 'https://acme.deskask.in' }), { status: 200 });
    if (url.endsWith('/trial') && o?.method === 'POST') return new Response(JSON.stringify({ address: 'https://acme.deskask.in', ends: '2026-12-03T00:00:00Z' }), { status: 200 });
    return new Response('[]', { status: 200 });
  }));
  render(<StorePage path={['trial']} />);
  await waitFor(() => expect(screen.getByText(/Try DeskAsk free for 14 days/)).toBeTruthy());
  expect(screen.getByRole('status').textContent).toContain('company');
  expect((screen.getByRole('checkbox', { name: 'Service Desk' }) as HTMLInputElement).checked).toBe(true);
  fireEvent.change(screen.getByLabelText('Company'), { target: { value: 'Acme' } });
  fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'Asha' } });
  fireEvent.change(screen.getByLabelText('Work email'), { target: { value: 'asha@acme.in' } });
  fireEvent.change(screen.getByLabelText('Trial address'), { target: { value: 'acme' } });
  await waitFor(() => expect(screen.getByText('https://acme.deskask.in is free')).toBeTruthy(), { timeout: 2000 });
  expect(screen.queryByText(/Still needed/)).toBeNull();
  fireEvent.click(screen.getByText('Start free trial'));
  await waitFor(() => expect(screen.getByText('Your trial is ready')).toBeTruthy());
});

test('the home page tells the story, switches modules and links the policy pages', async () => {
  vi.stubGlobal('fetch', vi.fn(async (u: string) => new Response(JSON.stringify(String(u).includes('/info') ? info : plans), { status: 200 })));
  render(<StorePage path={[]} />);
  await waitFor(() => expect(screen.getByRole('heading', { level: 1 }).textContent).toMatch(/One desk/));
  expect(screen.getByText('INC-0142 opened, priority P2')).toBeTruthy();
  await waitFor(() => expect(screen.getByText('₹68,000')).toBeTruthy());   // "from" price of the first module
  fireEvent.click(screen.getByRole('tab', { name: /Vault/ }));
  expect(screen.getByRole('tabpanel').textContent).toMatch(/encrypted in the browser/i);
  expect(screen.getByText('Privacy Policy').getAttribute('href')).toBe('#/store/privacy');
  expect(screen.getByText('Terms of Service').getAttribute('href')).toBe('https://www.deskask.in/terms');   // a link set in Business details wins
});

test('a policy page is drawn from its markdown without passing HTML through', async () => {
  const page = { slug: 'refund', title: 'Cancellation and Refund Policy', markdown: '*Last updated: today*\n\n| Situation | Refund |\n| --- | --- |\n| Free trial | Nothing to pay |\n\n**How refunds are paid.** Write to servicedesk@deskask.in. <script>x</script>' };
  vi.stubGlobal('fetch', vi.fn(async (u: string) => new Response(JSON.stringify(String(u).includes('/info') ? info : page), { status: 200 })));
  const { container } = render(<StorePage path={['refund']} />);
  await waitFor(() => expect(screen.getByRole('heading', { level: 1, name: 'Cancellation and Refund Policy' })).toBeTruthy());
  expect(screen.getByRole('columnheader', { name: 'Refund' })).toBeTruthy();
  expect(screen.getByText('How refunds are paid.').tagName).toBe('STRONG');
  expect(screen.getByText('servicedesk@deskask.in', { selector: '.md a' }).getAttribute('href')).toBe('mailto:servicedesk@deskask.in');
  expect(container.querySelector('script')).toBeNull();
});
