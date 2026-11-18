#!/usr/bin/env node
// DeskAsk security scanner — a self-contained, zero-dependency checker you run from OUTSIDE against your own DeskAsk server.
// It serves a live dashboard on http://localhost:<port> and performs SAFE, non-destructive checks (headers, TLS, auth, a short
// rate-limit probe, information leaks, exposed files). It never tries real passwords, never writes, deletes or changes anything.
// Usage:  node security/deskask-scan.mjs            then open the page and type your public URL
//         node security/deskask-scan.mjs --port 7900
// Only run this against a server you own or are authorised to test.

import http from 'node:http';
import https from 'node:https';
import tls from 'node:tls';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const PORT = Number(arg('--port') ?? 7900);
const here = dirname(fileURLToPath(import.meta.url));
function arg(name) { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : undefined; }

// ---- one HTTP(S) request, no redirect following, response body capped, bad TLS tolerated (reported, not fatal) ----
function request(url, { method = 'GET', headers = {}, body, timeoutMs = 12000 } = {}) {
  return new Promise((resolve) => {
    let u; try { u = new URL(url); } catch { return resolve({ error: 'bad url' }); }
    const lib = u.protocol === 'https:' ? https : http;
    const opts = { method, headers: { 'user-agent': 'DeskAsk-Scanner/1.0', accept: '*/*', ...headers }, timeout: timeoutMs };
    if (u.protocol === 'https:') { opts.rejectUnauthorized = false; opts.servername = u.hostname; }
    const start = Date.now();
    const req = lib.request(u, opts, (res) => {
      const chunks = []; let len = 0;
      res.on('data', (c) => { len += c.length; if (len <= 262144) chunks.push(c); });
      res.on('end', () => {
        const h = {}; for (const [k, v] of Object.entries(res.headers)) h[k.toLowerCase()] = Array.isArray(v) ? v.join(', ') : v;
        const setCookie = res.headers['set-cookie'] || [];
        resolve({ status: res.statusCode, headers: h, setCookie, body: Buffer.concat(chunks).toString('utf8'), ms: Date.now() - start });
      });
    });
    req.on('timeout', () => { req.destroy(); resolve({ error: 'timeout' }); });
    req.on('error', (e) => resolve({ error: e.code || e.message }));
    if (body) req.write(body);
    req.end();
  });
}

// ---- read the TLS certificate of a host ----
function certInfo(hostname, port = 443) {
  return new Promise((resolve) => {
    const s = tls.connect({ host: hostname, port, servername: hostname, rejectUnauthorized: false, timeout: 10000 }, () => {
      const c = s.getPeerCertificate(); const err = s.authorizationError; const proto = s.getProtocol();
      s.end(); resolve({ cert: c && Object.keys(c).length ? c : null, authorizationError: err, protocol: proto });
    });
    s.on('error', (e) => resolve({ error: e.code || e.message }));
    s.on('timeout', () => { s.destroy(); resolve({ error: 'timeout' }); });
  });
}

// ---- the checks. Each pushes findings through ctx.finding() and narrates through ctx.log(). ----
const SEV = { critical: 4, high: 3, medium: 2, low: 1, info: 0 };

const CHECKS = [
  async function reachable(ctx) {
    ctx.log('Connecting to ' + ctx.base + ' …');
    const r = await request(ctx.base);
    if (r.error) { ctx.finding({ id: 'reach', title: 'Server did not answer', sev: 'high', status: 'error', evidence: 'Request failed: ' + r.error, fix: 'Check the address, that the site is online, and that the port is open from the public internet.' }); ctx.stop = true; return; }
    ctx.log('Answered ' + r.status + ' in ' + r.ms + ' ms.');
    ctx.homeHeaders = r.headers; ctx.homeStatus = r.status;
    ctx.finding({ id: 'reach', title: 'Server is reachable', sev: 'info', status: 'info', evidence: 'HTTP ' + r.status + ' from ' + ctx.base });
  },

  async function httpsRedirect(ctx) {
    if (ctx.origin.protocol !== 'https:') { ctx.finding({ id: 'https', title: 'Site is not using HTTPS', sev: 'high', status: 'fail', evidence: 'You entered an http:// address.', fix: 'Serve DeskAsk only over HTTPS. Put it behind your HAProxy/IIS TLS front end and redirect http to https.' }); return; }
    ctx.log('Checking that plain HTTP redirects to HTTPS …');
    const httpUrl = 'http://' + ctx.origin.host + '/';
    const r = await request(httpUrl, { timeoutMs: 8000 });
    if (r.error) { ctx.finding({ id: 'http-redirect', title: 'Plain HTTP port not reachable (acceptable)', sev: 'info', status: 'info', evidence: 'Port 80 did not answer: ' + r.error + '. Fine if you do not serve HTTP at all.' }); return; }
    const loc = r.headers['location'] || '';
    if (r.status >= 300 && r.status < 400 && loc.startsWith('https://')) ctx.finding({ id: 'http-redirect', title: 'HTTP redirects to HTTPS', sev: 'info', status: 'pass', evidence: r.status + ' → ' + loc });
    else ctx.finding({ id: 'http-redirect', title: 'HTTP does not redirect to HTTPS', sev: 'medium', status: 'warn', evidence: 'HTTP ' + r.status + (loc ? ' → ' + loc : ' (no redirect)'), fix: 'Redirect all HTTP to HTTPS at the proxy so no page is ever served in the clear.' });
  },

  async function certificate(ctx) {
    if (ctx.origin.protocol !== 'https:') return;
    ctx.log('Reading the TLS certificate …');
    const port = ctx.origin.port ? Number(ctx.origin.port) : 443;
    const info = await certInfo(ctx.origin.hostname, port);
    if (info.error) { ctx.finding({ id: 'tls', title: 'Could not read the certificate', sev: 'low', status: 'warn', evidence: info.error }); return; }
    const c = info.cert;
    if (info.authorizationError && info.authorizationError !== 'ok') {
      ctx.finding({ id: 'tls-trust', title: 'Certificate is not trusted', sev: 'high', status: 'fail', evidence: String(info.authorizationError) + (c ? ' (issuer: ' + (c.issuer?.O || c.issuer?.CN || '?') + ')' : ''), fix: 'Use a certificate from a public CA (Let’s Encrypt) that matches the host name and is not expired or self-signed.' });
    }
    if (c) {
      const to = c.valid_to ? new Date(c.valid_to) : null;
      const days = to ? Math.round((to - Date.now()) / 86400000) : null;
      const names = (c.subjectaltname || '').replace(/DNS:/g, '');
      ctx.log('Certificate for ' + (c.subject?.CN || '?') + ', ' + (days ?? '?') + ' days left.');
      if (days != null && days < 0) ctx.finding({ id: 'tls-exp', title: 'Certificate has expired', sev: 'critical', status: 'fail', evidence: 'Expired on ' + c.valid_to, fix: 'Renew the certificate now. Automate renewal so it cannot lapse.' });
      else if (days != null && days < 21) ctx.finding({ id: 'tls-exp', title: 'Certificate expires soon', sev: 'medium', status: 'warn', evidence: days + ' days left (' + c.valid_to + ')', fix: 'Renew before it expires and automate renewal.' });
      else ctx.finding({ id: 'tls-exp', title: 'Certificate valid', sev: 'info', status: 'pass', evidence: (days ?? '?') + ' days left · ' + (names || c.subject?.CN || '') });
      if (info.protocol && /TLSv1(\.0|\.1)?$/.test(info.protocol)) ctx.finding({ id: 'tls-proto', title: 'Old TLS version negotiated', sev: 'medium', status: 'warn', evidence: info.protocol, fix: 'Allow only TLS 1.2 and 1.3 at the proxy; disable TLS 1.0/1.1.' });
    }
  },

  async function securityHeaders(ctx) {
    ctx.log('Checking browser security headers …');
    const h = ctx.homeHeaders || (await request(ctx.base)).headers || {};
    const want = [
      ['content-security-policy', 'Content-Security-Policy', 'high', (v) => /frame-ancestors[^;]*'none'/.test(v || ''), "Set a CSP with frame-ancestors 'none' (DeskAsk sends one by default — if it is missing, a proxy may be stripping it)."],
      ['x-content-type-options', 'X-Content-Type-Options', 'medium', (v) => /nosniff/i.test(v || ''), 'Send X-Content-Type-Options: nosniff.'],
      ['x-frame-options', 'X-Frame-Options', 'medium', (v) => /deny|sameorigin/i.test(v || ''), 'Send X-Frame-Options: DENY to stop click-jacking.'],
      ['strict-transport-security', 'Strict-Transport-Security', 'medium', (v) => /max-age=\d{5,}/.test(v || ''), 'Send HSTS with a long max-age over HTTPS.'],
      ['referrer-policy', 'Referrer-Policy', 'low', (v) => !!v, 'Send Referrer-Policy: no-referrer.'],
      ['permissions-policy', 'Permissions-Policy', 'low', (v) => !!v, 'Send a Permissions-Policy that turns off camera, microphone, geolocation and payment.'],
    ];
    for (const [key, name, sev, ok, fix] of want) {
      const v = h[key];
      if (v && ok(v)) ctx.finding({ id: 'hdr-' + key, title: name + ' present', sev: 'info', status: 'pass', evidence: cut(v) });
      else if (v) ctx.finding({ id: 'hdr-' + key, title: name + ' present but weak', sev, status: 'warn', evidence: cut(v), fix });
      else ctx.finding({ id: 'hdr-' + key, title: name + ' missing', sev, status: 'fail', evidence: 'Header not sent on ' + ctx.base, fix });
    }
    if (/includeSubDomains/i.test(h['strict-transport-security'] || '')) {} else if (h['strict-transport-security'])
      ctx.finding({ id: 'hsts-sub', title: 'HSTS without includeSubDomains/preload', sev: 'low', status: 'warn', evidence: cut(h['strict-transport-security']), fix: 'If every sub-domain is HTTPS, add includeSubDomains (and consider preload) to the HSTS header at the proxy.' });
  },

  async function banner(ctx) {
    const h = ctx.homeHeaders || {};
    const s = [h['server'], h['x-powered-by'], h['x-aspnet-version'], h['x-aspnetmvc-version']].filter(Boolean).join(' · ');
    if (/x-powered-by|x-aspnet/i.test(Object.keys(h).join(' ')) || /kestrel|iis\/|microsoft/i.test(s))
      ctx.finding({ id: 'banner', title: 'Server reveals software/version', sev: 'low', status: 'warn', evidence: cut(s || Object.keys(h).filter((k) => k.startsWith('x-')).join(', ')), fix: 'Remove Server, X-Powered-By and X-AspNet* headers at IIS/HAProxy so the stack and version are not advertised.' });
    else ctx.finding({ id: 'banner', title: 'No obvious version banner', sev: 'info', status: 'pass', evidence: s ? cut(s) : 'No Server/X-Powered-By version detail.' });
  },

  async function healthLeak(ctx) {
    ctx.log('Checking /health does not leak version or roles …');
    const r = await request(ctx.base.replace(/\/$/, '') + '/health');
    if (r.error) return;
    if (/version|roles|"host"/i.test(r.body || '')) ctx.finding({ id: 'health', title: '/health leaks internal detail', sev: 'medium', status: 'fail', evidence: cut(r.body), fix: 'Return only {"status":"ok"} to remote callers (DeskAsk does this; a reverse proxy adding its own body, or an old build, can leak it).' });
    else ctx.finding({ id: 'health', title: '/health reveals nothing', sev: 'info', status: 'pass', evidence: cut(r.body) || 'HTTP ' + r.status });
  },

  async function apiRequiresAuth(ctx) {
    ctx.log('Checking that API routes refuse anonymous callers …');
    const routes = ['/api/platform/me', '/api/platform/license', '/api/assets', '/api/assets/dashboard', '/api/monitoring/dashboard', '/api/platform/users', '/api/vault/vaults'];
    let worst = null;
    for (const path of routes) {
      const r = await request(ctx.base.replace(/\/$/, '') + path);
      if (r.error) continue;
      const open = r.status === 200 && /[[{]/.test((r.body || '').trim()[0] || '');
      ctx.log('  ' + path + ' → ' + r.status);
      if (open) { worst = path; ctx.finding({ id: 'auth-' + path, title: 'API route served data without sign-in', sev: 'critical', status: 'fail', evidence: 'GET ' + path + ' → 200 with a body of ' + (r.body || '').length + ' bytes', fix: 'Every /api/* route must require authorisation. DeskAsk has a test that enforces this — if this fails, a build or proxy is bypassing it. Investigate at once.' }); }
    }
    if (!worst) ctx.finding({ id: 'auth', title: 'API routes require sign-in', sev: 'info', status: 'pass', evidence: 'All sampled /api routes answered 401/403 (or no data) to an anonymous caller.' });
  },

  async function whoami(ctx) {
    const r = await request(ctx.base.replace(/\/$/, '') + '/api/platform/whoami');
    if (r.error) return;
    if (r.status === 200) ctx.finding({ id: 'whoami', title: 'Customer name is public at /whoami (by design)', sev: 'info', status: 'info', evidence: cut(r.body), fix: 'This is intentional: the sign-in page needs the customer name before log-in. Fine unless the tenant name itself is sensitive.' });
  },

  async function openRedirect(ctx) {
    ctx.log('Checking the sign-in return address is not an open redirect …');
    const r = await request(ctx.base.replace(/\/$/, '') + '/account/login?ReturnUrl=' + encodeURIComponent('https://evil.example/x'));
    if (r.error || !r.body) return;
    if (/value="https:\/\/evil\.example/.test(r.body)) ctx.finding({ id: 'open-redirect', title: 'Sign-in keeps an external return address', sev: 'high', status: 'fail', evidence: 'ReturnUrl was reflected unchanged into the form.', fix: 'Only accept a local path as the return address after sign-in (reject anything starting with a scheme or //).' });
    else if (/name="returnUrl"\s+value="\/"/.test(r.body) || /value="\/"/.test(r.body)) ctx.finding({ id: 'open-redirect', title: 'Sign-in return address is forced to a local path', sev: 'info', status: 'pass', evidence: 'An external ReturnUrl was replaced with "/".' });
  },

  async function exposedFiles(ctx) {
    ctx.log('Checking that config and source files are not served …');
    const files = ['/appsettings.json', '/appsettings.Production.json', '/web.config', '/.git/config', '/.env', '/release-public.pem', '/DeskAsk.Platform.Host.dll', '/appsettings.Development.json'];
    let leaked = 0;
    for (const f of files) {
      const r = await request(ctx.base.replace(/\/$/, '') + f);
      if (r.error) continue;
      const served = r.status === 200 && (r.body || '').length > 0 && !/<!doctype html>/i.test(r.body.slice(0, 200));
      if (served) { leaked++; ctx.finding({ id: 'file' + f, title: 'Sensitive file is downloadable', sev: 'critical', status: 'fail', evidence: 'GET ' + f + ' → 200 (' + r.body.length + ' bytes)', fix: 'Block this path at IIS/the proxy. Keep secrets and binaries outside the web root; never serve appsettings, .pem, .dll, web.config or .git.' }); }
      else ctx.log('  ' + f + ' → ' + (r.status || r.error));
    }
    if (!leaked) ctx.finding({ id: 'files', title: 'Sensitive files are not served', sev: 'info', status: 'pass', evidence: 'None of ' + files.length + ' sensitive paths returned content.' });
  },

  async function methods(ctx) {
    ctx.log('Checking TRACE is disabled …');
    const r = await request(ctx.base, { method: 'TRACE' });
    if (r.error) return;
    if (r.status === 200 && /trace/i.test(r.body || '')) ctx.finding({ id: 'trace', title: 'HTTP TRACE is enabled', sev: 'low', status: 'warn', evidence: 'TRACE → 200', fix: 'Disable the TRACE method at IIS/the proxy.' });
    else ctx.finding({ id: 'trace', title: 'HTTP TRACE is not enabled', sev: 'info', status: 'pass', evidence: 'TRACE → ' + r.status });
  },

  async function cookies(ctx) {
    const r = await request(ctx.base.replace(/\/$/, '') + '/account/login');
    const cookies = r.setCookie || [];
    if (!cookies.length) { ctx.finding({ id: 'cookies', title: 'No cookies set on the sign-in page', sev: 'info', status: 'info', evidence: 'Nothing to check yet.' }); return; }
    let bad = [];
    for (const c of cookies) { const name = c.split('=')[0]; const flags = c.toLowerCase(); if (!flags.includes('httponly')) bad.push(name + ' (no HttpOnly)'); if (ctx.origin.protocol === 'https:' && !flags.includes('secure')) bad.push(name + ' (no Secure)'); if (!flags.includes('samesite')) bad.push(name + ' (no SameSite)'); }
    if (bad.length) ctx.finding({ id: 'cookies', title: 'Cookie flags could be stronger', sev: 'low', status: 'warn', evidence: cut(bad.join(', ')), fix: 'Set HttpOnly, Secure and SameSite on session and anti-forgery cookies.' });
    else ctx.finding({ id: 'cookies', title: 'Cookies carry HttpOnly/Secure/SameSite', sev: 'info', status: 'pass', evidence: cookies.length + ' cookie(s) checked.' });
  },

  async function rateLimit(ctx) {
    ctx.log('Probing the sign-in rate limit (short burst of bad requests) …');
    const url = ctx.base.replace(/\/$/, '') + '/account/login';
    const body = 'email=scanner@example.invalid&password=x&__RequestVerificationToken=x';
    const headers = { 'content-type': 'application/x-www-form-urlencoded' };
    let got429 = 0, at = 0;
    for (let i = 1; i <= 20; i++) {
      const r = await request(url, { method: 'POST', headers, body, timeoutMs: 8000 });
      if (r.error) break;
      if (r.status === 429) { got429++; if (!at) at = i; }
      if (i % 5 === 0) ctx.log('  sent ' + i + ' requests…' + (got429 ? ' (429 seen)' : ''));
    }
    if (got429) ctx.finding({ id: 'ratelimit', title: 'Sign-in rate limiting is active', sev: 'info', status: 'pass', evidence: 'A 429 (Too Many Requests) appeared after about ' + at + ' rapid attempts.' });
    else ctx.finding({ id: 'ratelimit', title: 'No rate limit seen on sign-in', sev: 'medium', status: 'warn', evidence: '20 rapid sign-in POSTs, no 429.', fix: 'Confirm Security:AuthPerMinute is set (default 30) and that the proxy is not hiding each caller behind one IP — pass the real client IP (X-Forwarded-For) through to DeskAsk, or the limiter buckets everyone together.' });
  },

  async function dirListing(ctx) {
    const r = await request(ctx.base.replace(/\/$/, '') + '/assets/');
    if (r.error) return;
    if (r.status === 200 && /Index of|Directory listing/i.test(r.body || '')) ctx.finding({ id: 'dir', title: 'Directory listing is enabled', sev: 'medium', status: 'fail', evidence: 'GET /assets/ returned a directory listing.', fix: 'Turn off directory browsing at IIS/the proxy.' });
    else ctx.finding({ id: 'dir', title: 'No directory listing', sev: 'info', status: 'pass', evidence: '/assets/ → ' + r.status });
  },
];

function cut(s, n = 220) { s = String(s || '').replace(/\s+/g, ' ').trim(); return s.length > n ? s.slice(0, n) + '…' : s; }

// ---- run all checks, streaming events to one SSE client ----
async function runScan(target, send) {
  let origin; try { origin = new URL(target); } catch { send({ type: 'log', level: 'error', text: 'That is not a valid URL.' }); send({ type: 'done', summary: { error: true } }); return; }
  if (!/^https?:$/.test(origin.protocol)) { send({ type: 'log', level: 'error', text: 'Use an http:// or https:// address.' }); send({ type: 'done', summary: {} }); return; }
  const base = origin.origin;
  const ctx = { base, origin, stop: false, log: (text, level = 'info') => send({ type: 'log', level, text }), finding: (f) => send({ type: 'finding', finding: { sevRank: SEV[f.sev] ?? 0, ...f } }) };
  send({ type: 'log', level: 'info', text: 'Scan started against ' + base + ' at ' + new Date().toLocaleString() });
  send({ type: 'log', level: 'info', text: 'Only safe, read-only checks run. Nothing is created, changed or deleted.' });
  const total = CHECKS.length;
  for (let i = 0; i < total; i++) {
    send({ type: 'progress', done: i, total });
    try { await CHECKS[i](ctx); } catch (e) { ctx.log('Check "' + CHECKS[i].name + '" failed: ' + (e.message || e), 'error'); }
    if (ctx.stop) { ctx.log('Stopping: the server could not be reached.', 'error'); break; }
  }
  send({ type: 'progress', done: total, total });
  send({ type: 'log', level: 'info', text: 'Scan finished.' });
  send({ type: 'done', summary: { finishedAt: new Date().toISOString() } });
}

// ---- tiny local web server: the dashboard + an SSE endpoint ----
const server = http.createServer(async (req, res) => {
  const u = new URL(req.url, 'http://localhost');
  if (u.pathname === '/') {
    try { const html = await readFile(join(here, 'dashboard.html')); res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' }); res.end(html); }
    catch { res.writeHead(500); res.end('dashboard.html not found next to the script.'); }
    return;
  }
  if (u.pathname === '/run') {
    const target = u.searchParams.get('target') || '';
    res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-store', connection: 'keep-alive' });
    const send = (obj) => { try { res.write('data: ' + JSON.stringify(obj) + '\n\n'); } catch {} };
    const keep = setInterval(() => res.write(': ping\n\n'), 15000);
    req.on('close', () => clearInterval(keep));
    await runScan(target, send);
    clearInterval(keep); res.end();
    return;
  }
  res.writeHead(404); res.end('not found');
});

server.listen(PORT, '127.0.0.1', () => {
  console.log('\n  DeskAsk security scanner');
  console.log('  Open  http://localhost:' + PORT + '  in your browser.');
  console.log('  Then type your public DeskAsk URL and press Start.');
  console.log('  Run only against a server you are authorised to test. Ctrl+C to stop.\n');
});
