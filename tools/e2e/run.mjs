#!/usr/bin/env node
// End-to-end check of the real chain: running platform  <->  real Go agent binary. Node 18+ only.
//   node tools/e2e/run.mjs --base http://localhost:5080 --agent ./agent/deskask-agent --email admin@acme.test --password <pw>
// The platform must run with an administrator (Platform:BootstrapAdmin) and a sign-in client "web" whose redirect address is <base>/cb.
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const BASE = arg('base', 'http://localhost:5080').replace(/\/+$/, '');
const AGENT = arg('agent', './agent/deskask-agent');
const EMAIL = arg('email', 'admin@acme.test');
const PASSWORD = arg('password', 'Correct-Horse-9');
const REDIRECT = BASE + '/cb';

let failed = 0, total = 0;
function check(name, ok, detail = '') { total++; if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  (' + detail + ')' : ''}`); return ok; }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(fn, ms, step = 500) { const end = Date.now() + ms; for (;;) { const v = await fn(); if (v) return v; if (Date.now() > end) return null; await sleep(step); } }
function done() { console.log(`\n${total - failed} passed, ${failed} failed`); process.exit(failed ? 1 : 0); }

const jar = new Map();
const cookie = () => [...jar].map(([k, v]) => `${k}=${v}`).join('; ');
async function req(path, opts = {}) {
  const res = await fetch(BASE + path, { redirect: 'manual', ...opts, headers: { ...(opts.headers || {}), cookie: cookie() } });
  for (const c of res.headers.getSetCookie()) { const kv = c.split(';')[0]; const i = kv.indexOf('='); jar.set(kv.slice(0, i), kv.slice(i + 1)); }
  return res;
}
const b64u = (b) => Buffer.from(b).toString('base64url');

async function signIn() {
  const verifier = b64u(crypto.randomBytes(48));
  const challenge = b64u(crypto.createHash('sha256').update(verifier).digest());
  const authPath = '/connect/authorize?' + new URLSearchParams({ response_type: 'code', client_id: 'web', redirect_uri: REDIRECT, scope: 'openid email profile roles', state: 'e2e', code_challenge: challenge, code_challenge_method: 'S256' });
  const page = await (await req('/account/login?ReturnUrl=' + encodeURIComponent(authPath))).text();
  const token = /name="__RequestVerificationToken" value="([^"]+)"/.exec(page)?.[1];
  if (!token) return null;
  const login = await req('/account/login', { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams({ __RequestVerificationToken: token, returnUrl: authPath, email: EMAIL, password: PASSWORD }) });
  if (login.status !== 302) return null;
  const back = await req(authPath);
  const code = new URL(back.headers.get('location') || BASE).searchParams.get('code');
  if (!code) return null;
  const tr = await fetch(BASE + '/connect/token', { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams({ grant_type: 'authorization_code', code, redirect_uri: REDIRECT, client_id: 'web', code_verifier: verifier }) });
  return tr.status === 200 ? (await tr.json()).access_token : null;
}

const health = await fetch(BASE + '/health').then((r) => r.status).catch(() => 0);
if (!check('platform is up', health === 200, 'status ' + health)) done();

const access = await signIn();
if (!check('administrator signs in (code flow + PKCE)', !!access)) done();
const api = (path, opts = {}) => fetch(BASE + path, { ...opts, headers: { ...(opts.headers || {}), authorization: 'Bearer ' + access, 'content-type': 'application/json' } });
const me = await (await api('/api/platform/me')).json();
check('token opens the API', me.email === EMAIL && me.role === 'admin', JSON.stringify(me));

check('poll interval set to 10 s', (await api('/api/platform/settings/connect.interval', { method: 'PUT', body: JSON.stringify({ value: 10 }) })).status === 200);
const created = await (await api('/api/connect/agents', { method: 'POST', body: JSON.stringify({ name: 'e2e agent' }) })).json();
check('agent created, token shown once', created.token?.startsWith('dka_'), created.id);

let out = '', exitCode = null;
const child = spawn(AGENT, ['-platform', BASE, '-token', created.token, '-id', 'e2e-host'], { stdio: ['ignore', 'pipe', 'pipe'] });
child.stdout.on('data', (d) => (out += d)); child.stderr.on('data', (d) => (out += d));
child.on('exit', (c) => { exitCode = c; });

const online = await until(async () => {
  const list = await (await api('/api/connect/agents')).json();
  const a = list.find((x) => x.id === created.id);
  return a && a.online ? a : null;
}, 20000);
check('real agent shows online in the platform', !!online, online ? `version ${online.version}` : out.trim().slice(-200));
check('agent reported its version', !!online && online.version !== '');

const del = await api('/api/connect/agents/' + created.id, { method: 'DELETE' });
check('agent revoked', del.status === 200);
const exited = await until(async () => exitCode !== null, 40000, 500);
check('agent stops by itself after revocation', exited && exitCode === 1, `exit ${exitCode}`);
check('agent says why', /does not accept this agent token/.test(out), out.trim().split('\n').pop());

const after = (await (await api('/api/connect/agents')).json()).find((x) => x.id === created.id);
check('platform shows it revoked and offline', after?.revoked === true && after?.online === false);
const audit = await (await api('/api/platform/audit?action=connect.')).json();
check('creation and revocation are in the audit log', audit.some((r) => r.action === 'connect.agent_created' && r.target === created.id) && audit.some((r) => r.action === 'connect.agent_revoked' && r.target === created.id));
await api('/api/platform/settings/connect.interval', { method: 'DELETE' });
if (exitCode === null) child.kill();
done();
