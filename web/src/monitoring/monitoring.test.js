import { jsx as _jsx } from "react/jsx-runtime";
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import MonitoringApp from './MonitoringApp';
const metrics = (company, companyId, x = {}) => ({ company, companyId, devices: 0, watched: 0, ok: 0, warning: 0, critical: 0, muted: 0, unwatched: 0, down: 0,
    alertsCritical: 0, alertsWarning: 0, byMetric: { offline: 0, cpu: 0, memory: 0, disk: 0, check: 0, cert: 0, hardware: 0 }, checksOk: 0, checksFailing: 0, checksNotRun: 0,
    certOk: 0, certSoon: 0, certUrgent: 0, diskOk: 0, diskLow: 0, diskCritical: 0, diskNoData: 0, withMetrics: 0, ...x });
function mock(routes, calls = []) {
    vi.stubGlobal('fetch', vi.fn(async (url, init) => {
        calls.push({ url: String(url), method: init?.method ?? 'GET', body: String(init?.body ?? '') });
        const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
        return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{}', { status: 200 });
    }));
    return calls;
}
beforeEach(() => { location.hash = ''; });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
test('the monitoring home has a tile per customer, worst first, and a tile opens that customer', async () => {
    const acme = metrics('Acme Ltd', 1, { devices: 12, watched: 11, ok: 9, warning: 1, critical: 1, unwatched: 1, alertsCritical: 1, alertsWarning: 2, down: 1, diskCritical: 1, byMetric: { offline: 1, cpu: 0, memory: 1, disk: 1, check: 0, cert: 0, hardware: 0 } });
    const glob = metrics('Globex', 2, { devices: 4, watched: 4, ok: 4 });
    mock({ '/api/monitoring/dashboard?company=1': { customer: acme }, '/api/monitoring/dashboard': { all: metrics('All customers', null, { devices: 16 }), customers: [glob, acme], restricted: false }, '/api/platform/me': { role: 'admin' } });
    location.hash = '#/monitoring';
    render(_jsx(MonitoringApp, {}));
    await waitFor(() => expect(screen.getByText('Acme Ltd')).toBeTruthy());
    expect([...document.querySelectorAll('a.co-tile')].map((a) => a.getAttribute('href'))).toEqual(['#/monitoring/customer/all', '#/monitoring/customer/1', '#/monitoring/customer/2']);
    expect(screen.getByRole('img', { name: 'Devices of Acme Ltd: 9 ok, 1 warning, 1 critical, 0 muted, 1 not watched' })).toBeTruthy();
    cleanup();
    location.hash = '#/monitoring/customer/1';
    render(_jsx(MonitoringApp, {}));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Acme Ltd' })).toBeTruthy());
    expect(screen.getAllByText('Critical')[0].closest('a')?.getAttribute('href')).toBe('#/monitoring/devices?company=1&status=critical');
    expect(screen.getByText('Under 10% free').closest('a')?.getAttribute('href')).toBe('#/monitoring/devices?company=1&disk=critical');
    expect(screen.getByText('Memory').closest('a')?.getAttribute('href')).toBe('#/monitoring/alerts?company=1&metric=memory');
});
test('the device list sends the ring filter and shows where it came from', async () => {
    const calls = mock({ '/api/monitoring/dashboard?company=1': { customer: metrics('Acme Ltd', 1) }, '/api/monitoring/devices': { total: 1, items: [{ id: 5, tag: 'AST-5', name: 'SRV-DB1', company: 'Acme Ltd', status: 'critical', cpu: 42, mem: 95, worstDisk: 3, pingUp: null, agent: true, agentSeen: new Date().toISOString(), alerts: 2, alertsCritical: 1 }] } });
    location.hash = '#/monitoring/devices?company=1&disk=critical';
    render(_jsx(MonitoringApp, {}));
    await waitFor(() => expect(screen.getByText('SRV-DB1')).toBeTruthy());
    expect(calls.some((c) => c.url.startsWith('/api/monitoring/devices?') && c.url.includes('&company=1') && c.url.includes('&disk=critical'))).toBe(true);
    expect(screen.getByRole('link', { name: 'Under 10% disk free ✕' }).getAttribute('href')).toBe('#/monitoring/devices?company=1');
    expect(screen.getByText('SRV-DB1').closest('a')?.getAttribute('href')).toBe('#/monitoring/device/5');
    expect(screen.getByText('95%')).toBeTruthy();
});
test('a device shows its figures, a chart that can become a table, and an alert can be acknowledged', async () => {
    const now = Date.now();
    const points = [0, 1, 2].map((i) => ({ at: new Date(now - (3 - i) * 60000).toISOString(), cpu: 40 + i, cpuMax: 40 + i, mem: 70, memMax: 70, diskFree: 12 }));
    const calls = mock({
        '/api/platform/me': { role: 'admin' },
        '/api/monitoring/devices/5/series': { range: '24h', hourly: false, from: new Date(now - 86400000).toISOString(), to: new Date(now).toISOString(), points },
        '/api/monitoring/devices/5': { device: { id: 5, tag: 'AST-5', name: 'SRV-DB1', company: 'Acme Ltd', companyId: 1, status: 'warning', cpu: 42, mem: 70, memTotal: 16e9, worstDisk: 12, uptimeS: 7200, agent: true, agentVersion: '3.0.0', agentSeen: new Date().toISOString(), metricsAt: new Date().toISOString(), disks: [{ name: 'C:', freePct: 12, free: 12e9, total: 100e9 }], checks: [], faults: [] },
            alerts: [{ id: 9, assetId: 5, asset: 'SRV-DB1', company: 'Acme Ltd', severity: 'warning', metric: 'memory', rule: 'Memory high', text: 'Memory use is 95%.', openedAt: new Date(now - 600000).toISOString(), closedAt: null, ackAt: null, ticket: '' }] },
    });
    location.hash = '#/monitoring/device/5';
    render(_jsx(MonitoringApp, {}));
    await waitFor(() => expect(screen.getByRole('heading', { name: 'SRV-DB1' })).toBeTruthy());
    await waitFor(() => expect(screen.getByRole('img', { name: 'Processor and memory use: Processor latest 42%, Memory latest 70%' })).toBeTruthy());
    fireEvent.click(screen.getAllByText('Show as table')[0]);
    expect(screen.getAllByText('42.0%').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: '7 days' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/monitoring/devices/5/series?range=7d')).toBe(true));
    fireEvent.click(screen.getByRole('button', { name: 'Acknowledge' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/monitoring/alerts/9/ack' && c.method === 'POST')).toBe(true));
    fireEvent.click(screen.getByRole('button', { name: 'Mute' }));
    await waitFor(() => expect(calls.find((c) => c.url === '/api/monitoring/devices/5/mute')?.body).toBe(JSON.stringify({ hours: 2, reason: '' })));
});
test('an administrator adds a rule; the form shows only the fields that rule needs', async () => {
    const calls = mock({ '/api/platform/me': { role: 'admin' }, '/api/assets/companies': [{ id: 1, name: 'Acme Ltd' }],
        '/api/monitoring/rules': { items: [{ id: 1, name: 'Disk space below 10%', description: 'Less than 10% free on a disk or datastore of any device', severity: 'warning', enabled: true, mail: true, ticket: false, ticketPriority: 'high', kinds: [], metric: 'disk', threshold: 10, minutes: 0, company: '' }] } });
    location.hash = '#/monitoring/rules';
    render(_jsx(MonitoringApp, {}));
    await waitFor(() => expect(screen.getByText('Less than 10% free on a disk or datastore of any device')).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: 'New rule' }));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Busy servers' } });
    fireEvent.change(screen.getByLabelText('What to watch'), { target: { value: 'cpu' } });
    expect(screen.queryByLabelText('Free space below (%)')).toBeNull();
    fireEvent.change(screen.getByLabelText('Above (%)'), { target: { value: '85' } });
    fireEvent.change(screen.getByLabelText('For at least (minutes; 0 means the latest reading)'), { target: { value: '10' } });
    fireEvent.click(screen.getByLabelText('Servers'));
    fireEvent.click(screen.getByRole('button', { name: 'Add rule' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/monitoring/rules' && c.method === 'POST')).toBe(true));
    const body = JSON.parse(calls.find((c) => c.url === '/api/monitoring/rules' && c.method === 'POST').body);
    expect(body).toMatchObject({ name: 'Busy servers', metric: 'cpu', threshold: 85, minutes: 10, kinds: ['server'], companyId: null });
});
test('a service rule names the services and can restart them; the form asks only what that kind needs', async () => {
    const calls = mock({ '/api/platform/me': { role: 'admin' }, '/api/assets/companies': [], '/api/monitoring/rules': { items: [] } });
    location.hash = '#/monitoring/rules';
    render(_jsx(MonitoringApp, {}));
    fireEvent.click(await screen.findByRole('button', { name: 'New rule' }));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'SQL running' } });
    fireEvent.change(screen.getByLabelText('What to watch'), { target: { value: 'service' } });
    fireEvent.change(screen.getByLabelText('Service names (as in Services, or systemd units), separated by commas'), { target: { value: 'MSSQLSERVER, SQLSERVERAGENT' } });
    fireEvent.click(screen.getByLabelText('Ask the agent to restart the service once when the alert opens'));
    fireEvent.change(screen.getByLabelText('What to watch'), { target: { value: 'log' } });
    expect(screen.getByLabelText('At this level or worse')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('What to watch'), { target: { value: 'service' } });
    fireEvent.click(screen.getByRole('button', { name: 'Add rule' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/monitoring/rules' && c.method === 'POST')).toBe(true));
    expect(JSON.parse(calls.find((c) => c.method === 'POST').body)).toMatchObject({ metric: 'service', target: 'MSSQLSERVER, SQLSERVERAGENT', restart: true, teams: true });
});
test('a switch shows its ports; a port can be watched and its traffic opened', async () => {
    const calls = mock({
        '/api/platform/me': { role: 'admin' }, '/api/monitoring/events': { total: 1, items: [{ id: 1, assetId: 7, asset: 'CORE-SW1', at: new Date().toISOString(), kind: 'trap', severity: 3, source: 'linkDown', eventId: 0, text: 'linkDown: Gi1/0/1' }] },
        '/api/monitoring/devices/7/ports/2/series': { hourly: false, from: new Date(Date.now() - 86400000).toISOString(), to: new Date().toISOString(), points: [{ at: new Date().toISOString(), inPct: 64, outPct: 3 }] },
        '/api/monitoring/devices/7/series': { hourly: false, from: new Date(Date.now() - 86400000).toISOString(), to: new Date().toISOString(), points: [] },
        '/api/monitoring/devices/7': { device: { id: 7, name: 'CORE-SW1', tag: 'AST-7', company: 'Acme', companyId: 1, status: 'critical', disks: [], checks: [], faults: [], watched: [],
                portsAt: new Date().toISOString(), ports: [{ index: '1', name: 'Gi1/0/1', alias: 'Uplink to FW', up: false, adminUp: true, mbps: 1000, rates: true, inPct: 0, outPct: 0, watched: true, errors: 0, discards: 0 },
                    { index: '2', name: 'Gi1/0/2', alias: '', up: true, adminUp: true, mbps: 1000, rates: true, inPct: 64.2, outPct: 2.5, inBps: 642e6, outBps: 25e6, watched: false, errors: 0.4, discards: 0 }] }, alerts: [] },
    });
    location.hash = '#/monitoring/device/7';
    render(_jsx(MonitoringApp, {}));
    await waitFor(() => expect(screen.getByText('Uplink to FW')).toBeTruthy());
    expect(screen.getByText('642.0 Mbit/s')).toBeTruthy();
    expect(await screen.findByText('linkDown: Gi1/0/1')).toBeTruthy();
    fireEvent.click(screen.getByLabelText('Watch Gi1/0/2'));
    await waitFor(() => expect(calls.find((c) => c.url === '/api/monitoring/devices/7/ports/2/watch')?.body).toBe(JSON.stringify({ watched: true })));
    fireEvent.click(screen.getAllByRole('button', { name: 'Traffic' })[1]);
    await waitFor(() => expect(screen.getByRole('img', { name: 'Gi1/0/2: share of 1 Gbps in use: In latest 64%, Out latest 3%' })).toBeTruthy());
});
test('settings send a Teams test and keep a saved webhook when it comes back unchanged', async () => {
    const calls = mock({ '/api/platform/me': { role: 'admin' }, '/api/assets/companies': [], '/api/monitoring/settings': { recipients: 'noc@x.test', mailResolved: true, teams: 'contoso.webhook.office.com/…', companies: [], escalateMinutes: 15, escalateRecipients: '', escalateTeams: '', reportEnabled: false, reportTo: '', reportCustomers: true } });
    location.hash = '#/monitoring/settings';
    render(_jsx(MonitoringApp, {}));
    await waitFor(() => expect(screen.getByLabelText('Microsoft Teams channel webhook').value).toBe('contoso.webhook.office.com/…'));
    fireEvent.click(screen.getAllByRole('button', { name: 'Send a test message' })[0]);
    await waitFor(() => expect(calls.find((c) => c.url === '/api/monitoring/settings/teams-test')?.body).toBe('{}')); // the saved one, not the hint
    fireEvent.click(screen.getByLabelText("Mail last month's uptime report as a PDF on the 1st of every month"));
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/monitoring/settings' && c.method === 'PUT')).toBe(true));
    expect(JSON.parse(calls.find((c) => c.method === 'PUT').body)).toMatchObject({ teams: 'contoso.webhook.office.com/…', reportEnabled: true, escalateMinutes: 15 });
});
test('a rule that opens tickets can resolve them when the alert clears; a printer supply rule asks for a percentage', async () => {
    const calls = mock({ '/api/platform/me': { role: 'admin' }, '/api/assets/companies': [], '/api/monitoring/rules': { items: [] } });
    location.hash = '#/monitoring/rules';
    render(_jsx(MonitoringApp, {}));
    fireEvent.click(await screen.findByRole('button', { name: 'New rule' }));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Toner nearly gone' } });
    fireEvent.change(screen.getByLabelText('What to watch'), { target: { value: 'supply' } });
    fireEvent.change(screen.getByLabelText('At or below (% left)'), { target: { value: '5' } });
    expect(screen.queryByLabelText(/Resolve the ticket when the alert clears/)).toBeNull();
    fireEvent.click(screen.getByLabelText('Printers'));
    fireEvent.click(screen.getByLabelText('Open a Service Desk ticket'));
    fireEvent.click(screen.getByLabelText(/Resolve the ticket when the alert clears/));
    fireEvent.click(screen.getByRole('button', { name: 'Add rule' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/monitoring/rules' && c.method === 'POST')).toBe(true));
    expect(JSON.parse(calls.find((c) => c.method === 'POST').body)).toMatchObject({ metric: 'supply', threshold: 5, kinds: ['printer'], ticket: true, resolve: true });
});
test('the rules list says which rules resolve their tickets', async () => {
    mock({ '/api/platform/me': { role: 'admin' }, '/api/monitoring/rules': { items: [{ id: 3, name: 'Disk space below 5%', description: 'Less than 5% free', severity: 'critical', enabled: true, mail: true, teams: false, ticket: true, resolve: true, ticketPriority: 'high', kinds: [], metric: 'disk', threshold: 5, minutes: 0, company: '' }] } });
    location.hash = '#/monitoring/rules';
    render(_jsx(MonitoringApp, {}));
    await waitFor(() => expect(screen.getByText('Mail, Ticket (high), resolves itself')).toBeTruthy());
});
