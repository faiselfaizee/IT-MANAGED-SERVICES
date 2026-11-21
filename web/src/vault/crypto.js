/**
 * The Vault's encryption, all in the browser (WebCrypto). The server only ever stores what these functions return.
 *
 *  - A person's master password gives, through PBKDF2-SHA256 (600 000 rounds), the key that encrypts their RSA-OAEP 3072 private key.
 *    A recovery code (160 random bits, through HKDF) encrypts a second copy.
 *  - Each vault has two AES-256-GCM keys: the item key (contents, folder names) and the directory key (titles). They reach each
 *    member wrapped with that member's public key.
 *  - A copy for one person (an approved request) is sealed: a fresh AES key encrypts the data and is wrapped with their public key.
 *
 * Every encrypted value is a small JSON text: {"v":1,"iv":"…","ct":"…"} (AES-GCM), {"v":1,"k":"…"} (a wrapped key) or both (sealed).
 */
const te = new TextEncoder(), td = new TextDecoder();
const subtle = () => globalThis.crypto.subtle;
export const ITERATIONS = 600_000;
export function b64(b) {
    const u = b instanceof Uint8Array ? b : new Uint8Array(b);
    let s = '';
    for (let i = 0; i < u.length; i += 0x8000)
        s += String.fromCharCode(...u.subarray(i, i + 0x8000));
    return btoa(s);
}
export function unb64(s) { const bin = atob(s.replace(/-/g, '+').replace(/_/g, '/')); const u = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++)
    u[i] = bin.charCodeAt(i); return u; }
export const b64u = (b) => b64(b).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
export const random = (n) => globalThis.crypto.getRandomValues(new Uint8Array(n));
const buf = (u) => u;
const parse = (s) => { const o = JSON.parse(s); if (o.v !== 1)
    throw new Error('This was encrypted by a newer version of DeskAsk.'); return o; };
// ---- symmetric
export async function aesKey(raw, extractable = true) {
    return subtle().importKey('raw', buf(raw), { name: 'AES-GCM' }, extractable, ['encrypt', 'decrypt']);
}
export async function newAesKey() { return subtle().generateKey({ name: 'AES-GCM', length: 256 }, true, ['encrypt', 'decrypt']); }
export async function rawKey(k) { return new Uint8Array(await subtle().exportKey('raw', k)); }
export async function encryptBytes(key, data) {
    const iv = random(12);
    const ct = await subtle().encrypt({ name: 'AES-GCM', iv: buf(iv) }, key, buf(data));
    return JSON.stringify({ v: 1, iv: b64(iv), ct: b64(ct) });
}
export async function decryptBytes(key, blob) {
    const o = parse(blob);
    if (!o.iv || !o.ct)
        throw new Error('Not an encrypted value.');
    try {
        return new Uint8Array(await subtle().decrypt({ name: 'AES-GCM', iv: buf(unb64(o.iv)) }, key, buf(unb64(o.ct))));
    }
    catch {
        throw new Error('Could not decrypt (wrong key or damaged data).');
    }
}
export const encryptJson = (key, v) => encryptBytes(key, te.encode(JSON.stringify(v)));
export async function decryptJson(key, blob) { return JSON.parse(td.decode(await decryptBytes(key, blob))); }
// ---- keys from secrets
export async function masterKey(password, kdf) {
    if (kdf.alg !== 'pbkdf2-sha256')
        throw new Error('Unknown key derivation.');
    const base = await subtle().importKey('raw', buf(te.encode(password.normalize('NFKC'))), 'PBKDF2', false, ['deriveKey']);
    return subtle().deriveKey({ name: 'PBKDF2', hash: 'SHA-256', salt: buf(unb64(kdf.salt)), iterations: kdf.iterations }, base, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}
export const newKdf = (iterations = ITERATIONS) => ({ alg: 'pbkdf2-sha256', iterations, salt: b64u(random(16)) });
const B32 = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'; // no 0/O, 1/I
/** A recovery code: 32 characters in groups of four, 160 bits. */
export function newRecoveryCode() {
    const r = random(32);
    let s = '';
    for (let i = 0; i < 32; i++)
        s += B32[r[i] % 32];
    return s.match(/.{4}/g).join('-');
}
export const normalizeCode = (c) => c.toUpperCase().replace(/[^A-Z0-9]/g, '').replace(/O/g, '0').replace(/I/g, '1');
async function recoveryKey(code) {
    const base = await subtle().importKey('raw', buf(te.encode(normalizeCode(code))), 'HKDF', false, ['deriveKey']);
    return subtle().deriveKey({ name: 'HKDF', hash: 'SHA-256', salt: buf(te.encode('deskask-vault-recovery')), info: buf(te.encode('v1')) }, base, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}
const RSA = { name: 'RSA-OAEP', hash: 'SHA-256' };
async function importPrivate(pkcs8) { return subtle().importKey('pkcs8', buf(pkcs8), RSA, false, ['decrypt']); }
async function importPublic(spki) { return subtle().importKey('spki', buf(unb64(spki)), RSA, false, ['encrypt']); }
/** First set-up: a key pair, the private key encrypted twice, and the recovery code to show once. */
export async function createKeys(password, iterations = ITERATIONS) {
    const pair = await subtle().generateKey({ ...RSA, modulusLength: 3072, publicExponent: new Uint8Array([1, 0, 1]) }, true, ['encrypt', 'decrypt']);
    const pkcs8 = new Uint8Array(await subtle().exportKey('pkcs8', pair.privateKey));
    const publicKey = b64(await subtle().exportKey('spki', pair.publicKey));
    const kdf = newKdf(iterations);
    const recoveryCode = newRecoveryCode();
    const wrappedPrivate = await encryptBytes(await masterKey(password, kdf), pkcs8);
    const wrappedRecovery = await encryptBytes(await recoveryKey(recoveryCode), pkcs8);
    const privateKey = await importPrivate(pkcs8);
    pkcs8.fill(0);
    return { publicKey, wrappedPrivate, wrappedRecovery, kdf, recoveryCode, privateKey };
}
/** Unlocks with the master password. Throws "Wrong master password." when it does not fit. */
export async function unlock(password, wrappedPrivate, kdf) {
    let pkcs8;
    try {
        pkcs8 = await decryptBytes(await masterKey(password, kdf), wrappedPrivate);
    }
    catch {
        throw new Error('Wrong master password.');
    }
    const k = await importPrivate(pkcs8);
    pkcs8.fill(0);
    return k;
}
/** A new master password: the private key is encrypted again (with the current password, or with the recovery code when that is lost). */
export async function rewrap(secret, newPassword, iterations = ITERATIONS) {
    let pkcs8;
    try {
        pkcs8 = 'password' in secret ? await decryptBytes(await masterKey(secret.password, secret.kdf), secret.wrappedPrivate)
            : await decryptBytes(await recoveryKey(secret.recoveryCode), secret.wrappedRecovery);
    }
    catch {
        throw new Error('password' in secret ? 'Wrong master password.' : 'Wrong recovery code.');
    }
    const kdf = newKdf(iterations);
    const wrappedPrivate = await encryptBytes(await masterKey(newPassword, kdf), pkcs8);
    const privateKey = await importPrivate(pkcs8);
    pkcs8.fill(0);
    return { wrappedPrivate, kdf, privateKey };
}
/** A new recovery code (needs the unlocked private key's bytes, so the master password is asked again). */
export async function newRecovery(password, wrappedPrivate, kdf) {
    let pkcs8;
    try {
        pkcs8 = await decryptBytes(await masterKey(password, kdf), wrappedPrivate);
    }
    catch {
        throw new Error('Wrong master password.');
    }
    const recoveryCode = newRecoveryCode();
    const wrappedRecovery = await encryptBytes(await recoveryKey(recoveryCode), pkcs8);
    pkcs8.fill(0);
    return { recoveryCode, wrappedRecovery };
}
// ---- wrapping keys for people, and sealed copies
export async function wrapFor(publicKey, key) {
    const raw = await rawKey(key);
    const k = await subtle().encrypt(RSA, await importPublic(publicKey), buf(raw));
    raw.fill(0);
    return JSON.stringify({ v: 1, k: b64(k) });
}
export async function unwrapKey(privateKey, blob) {
    const o = parse(blob);
    if (!o.k)
        throw new Error('Not a wrapped key.');
    let raw;
    try {
        raw = new Uint8Array(await subtle().decrypt(RSA, privateKey, buf(unb64(o.k))));
    }
    catch {
        throw new Error('This key was not given to you.');
    }
    const k = await aesKey(raw);
    raw.fill(0);
    return k;
}
export async function sealFor(publicKey, v) {
    const k = await newAesKey();
    const box = parse(await encryptJson(k, v));
    return JSON.stringify({ v: 1, k: parse(await wrapFor(publicKey, k)).k, iv: box.iv, ct: box.ct });
}
export async function openSealed(privateKey, blob) {
    const o = parse(blob);
    const k = await unwrapKey(privateKey, JSON.stringify({ v: 1, k: o.k }));
    return decryptJson(k, JSON.stringify({ v: 1, iv: o.iv, ct: o.ct }));
}
// ---- send links
/** The key that goes after the # of a send link; with a passcode, the passcode is mixed in (so the link alone is not enough). */
export async function sendKey(fragment, passcode) {
    if (!passcode)
        return aesKey(fragment, false);
    const base = await subtle().importKey('raw', buf(te.encode(passcode.normalize('NFKC'))), 'PBKDF2', false, ['deriveKey']);
    return subtle().deriveKey({ name: 'PBKDF2', hash: 'SHA-256', salt: buf(fragment), iterations: 100_000 }, base, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}
/** What the server compares before a passcode-protected link uses up a view. It depends on the link's secret part, so the server cannot guess the passcode from it. */
export async function sendCheck(fragment, passcode) {
    return b64u(await subtle().digest('SHA-256', te.encode('deskask-send-check:' + passcode.normalize('NFKC') + ':' + b64u(fragment))));
}
// ---- password helpers
export async function sha1Hex(s) { return [...new Uint8Array(await subtle().digest('SHA-1', te.encode(s)))].map((b) => b.toString(16).padStart(2, '0')).join('').toUpperCase(); }
/** How many times a password appears in known breaches, from a range answer (lines "SUFFIX:COUNT"). */
export function breachCount(rangeBody, hash) {
    const suffix = hash.slice(5);
    for (const line of rangeBody.split('\n')) {
        const [s, c] = line.trim().split(':');
        if (s === suffix)
            return Number(c) || 0;
    }
    return 0;
}
export const DEFAULT_GEN = { length: 20, upper: true, lower: true, digits: true, symbols: true, avoidAmbiguous: true };
/** A random password with at least one character of each chosen kind; no bias (rejection sampling). */
export function generate(o) {
    const amb = /[O0Il1|`'"]/g;
    const sets = [o.upper && 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', o.lower && 'abcdefghijklmnopqrstuvwxyz', o.digits && '0123456789', o.symbols && '!#$%&*+-=?@^_~.,;:'].filter(Boolean)
        .map((s) => (o.avoidAmbiguous ? s.replace(amb, '') : s));
    if (sets.length === 0)
        sets.push('abcdefghijkmnopqrstuvwxyz');
    const all = sets.join('');
    const len = Math.max(Math.min(o.length, 128), sets.length, 4);
    const pick = (s) => { const lim = 256 - (256 % s.length); for (;;) {
        const b = random(1)[0];
        if (b < lim)
            return s[b % s.length];
    } };
    const out = sets.map(pick);
    while (out.length < len)
        out.push(pick(all));
    for (let i = out.length - 1; i > 0; i--) {
        const lim = 256 - (256 % (i + 1));
        let b;
        do
            b = random(1)[0];
        while (b >= lim);
        const j = b % (i + 1);
        [out[i], out[j]] = [out[j], out[i]];
    }
    return out.join('');
}
const COMMON = ['password', 'passw0rd', 'qwerty', 'letmein', 'welcome', 'admin', 'login', 'abc123', 'iloveyou', 'monkey', 'dragon', 'master', 'sunshine', 'princess', 'football', 'secret', 'changeme', 'default', 'summer', 'winter', 'spring', 'autumn', 'india', 'company'];
/** A rough strength: 0 very weak … 4 strong, from length, character variety and well-known patterns. */
export function strength(pw) {
    if (!pw)
        return { score: 0, label: 'Empty', bits: 0 };
    let pool = 0;
    if (/[a-z]/.test(pw))
        pool += 26;
    if (/[A-Z]/.test(pw))
        pool += 26;
    if (/[0-9]/.test(pw))
        pool += 10;
    if (/[^A-Za-z0-9]/.test(pw))
        pool += 33;
    let bits = pw.length * Math.log2(Math.max(pool, 2));
    const low = pw.toLowerCase().replace(/[@4]/g, 'a').replace(/3/g, 'e').replace(/[1!]/g, 'i').replace(/0/g, 'o').replace(/[$5]/g, 's').replace(/7/g, 't');
    if (COMMON.some((w) => low.includes(w)))
        bits = Math.min(bits, 28);
    if (/^(.)\1+$/.test(pw) || /(0123|1234|2345|3456|4567|5678|6789|abcd|qwer|asdf)/i.test(pw))
        bits = Math.min(bits, 20);
    if (/(19|20)\d\d$/.test(pw))
        bits -= 10;
    const score = bits < 28 ? 0 : bits < 40 ? 1 : bits < 60 ? 2 : bits < 80 ? 3 : 4;
    return { score, label: ['Very weak', 'Weak', 'Fair', 'Good', 'Strong'][score], bits: Math.round(bits) };
}
// ---- one-time codes (TOTP, RFC 6238)
export function parseTotp(input) {
    let s = input.trim(), digits = 6, period = 30, algo = 'SHA-1';
    if (s.toLowerCase().startsWith('otpauth://')) {
        try {
            const u = new URL(s);
            s = u.searchParams.get('secret') ?? '';
            digits = Number(u.searchParams.get('digits') ?? 6) || 6;
            period = Number(u.searchParams.get('period') ?? 30) || 30;
            const a = (u.searchParams.get('algorithm') ?? 'SHA1').toUpperCase();
            algo = a === 'SHA256' ? 'SHA-256' : a === 'SHA512' ? 'SHA-512' : 'SHA-1';
        }
        catch {
            return null;
        }
    }
    const clean = s.toUpperCase().replace(/[\s=-]/g, '');
    if (!/^[A-Z2-7]+$/.test(clean) || clean.length < 8)
        return null;
    const A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
    let bitsAcc = 0, val = 0;
    const out = [];
    for (const ch of clean) {
        val = (val << 5) | A.indexOf(ch);
        bitsAcc += 5;
        if (bitsAcc >= 8) {
            out.push((val >>> (bitsAcc - 8)) & 255);
            bitsAcc -= 8;
        }
    }
    return { secret: new Uint8Array(out), digits: Math.min(Math.max(digits, 6), 8), period: Math.min(Math.max(period, 15), 120), algo };
}
export async function totp(input, at = Date.now()) {
    const t = parseTotp(input);
    if (!t)
        return null;
    const counter = Math.floor(at / 1000 / t.period);
    const msg = new Uint8Array(8);
    let c = counter;
    for (let i = 7; i >= 0; i--) {
        msg[i] = c & 255;
        c = Math.floor(c / 256);
    }
    const key = await subtle().importKey('raw', buf(t.secret), { name: 'HMAC', hash: t.algo }, false, ['sign']);
    const h = new Uint8Array(await subtle().sign('HMAC', key, buf(msg)));
    const o = h[h.length - 1] & 15;
    const bin = ((h[o] & 127) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
    return { code: String(bin % 10 ** t.digits).padStart(t.digits, '0'), left: t.period - (Math.floor(at / 1000) % t.period) };
}
