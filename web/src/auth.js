/** Sign-in for the web app: authorization-code flow with PKCE against the platform's own identity server (client "web"). */
const TOKEN = 'deskask.token', STATE = 'deskask.state', VERIFIER = 'deskask.verifier', BACK = 'deskask.back';
export const CALLBACK_PATH = '/auth/callback';
function get(k) { try {
    return sessionStorage.getItem(k);
}
catch {
    return null;
} }
function set(k, v) { try {
    if (v === null)
        sessionStorage.removeItem(k);
    else
        sessionStorage.setItem(k, v);
}
catch { /* private mode: sign in again next time */ } }
function b64u(buf) {
    const bytes = buf instanceof Uint8Array ? buf : new Uint8Array(buf);
    let s = '';
    bytes.forEach((b) => { s += String.fromCharCode(b); });
    return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
const random = (n) => b64u(crypto.getRandomValues(new Uint8Array(n)));
export function accessToken() {
    const raw = get(TOKEN);
    if (!raw)
        return null;
    try {
        const t = JSON.parse(raw);
        return t.exp > Date.now() + 15000 ? t.v : null;
    }
    catch {
        return null;
    }
}
export async function signIn() {
    const verifier = random(48), state = random(16);
    const challenge = b64u(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier)));
    set(VERIFIER, verifier);
    set(STATE, state);
    set(BACK, location.hash);
    const q = new URLSearchParams({
        response_type: 'code', client_id: 'web', redirect_uri: location.origin + CALLBACK_PATH, scope: 'openid email profile roles',
        state, code_challenge: challenge, code_challenge_method: 'S256',
    });
    location.assign('/connect/authorize?' + q.toString());
}
/** On the callback address: trade the code for a token. Returns true when it was a callback. */
export async function completeSignIn() {
    if (location.pathname !== CALLBACK_PATH)
        return false;
    const p = new URLSearchParams(location.search);
    const code = p.get('code'), state = p.get('state');
    if (!code || state !== get(STATE)) {
        set(STATE, null);
        await signIn();
        return true;
    }
    const r = await fetch('/connect/token', {
        method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({ grant_type: 'authorization_code', code, redirect_uri: location.origin + CALLBACK_PATH, client_id: 'web', code_verifier: get(VERIFIER) ?? '' }),
    });
    if (!r.ok) {
        set(STATE, null);
        await signIn();
        return true;
    }
    const j = (await r.json());
    set(TOKEN, JSON.stringify({ v: j.access_token, exp: Date.now() + (j.expires_in ?? 3600) * 1000 }));
    const back = get(BACK) ?? '';
    set(VERIFIER, null);
    set(STATE, null);
    set(BACK, null);
    history.replaceState(null, '', '/' + back);
    return false;
}
/** Resolves when there is a usable token; otherwise sends the browser to the sign-in page. */
export async function ensureSignedIn() {
    if (await completeSignIn())
        return false;
    if (accessToken())
        return true;
    await signIn();
    return false;
}
/** Ends the session on the server too (its cookie would otherwise sign the person straight back in), then returns to the start page. */
export function signOut() { set(TOKEN, null); location.assign('/connect/logout'); }
