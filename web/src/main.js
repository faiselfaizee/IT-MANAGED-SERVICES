import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import '@fontsource-variable/sora';
import '@fontsource-variable/figtree';
import { createRoot } from 'react-dom/client';
import App from './App';
import { ensureSignedIn } from './auth';
import './styles.css';
import { applyLook } from './look';
import { loadBrand } from './brand';
applyLook();
void loadBrand(); // the customer's logo, colours and theme; no sign-in needed
const root = createRoot(document.getElementById('root'));
// a send link opens without signing in; its key stays in the address after the #
const send = location.hash.match(/^#\/send\/([A-Za-z0-9_-]{22})\/([A-Za-z0-9_-]{40,})$/);
const store = location.hash.match(/^#\/store(?:\/(.*))?$/);
if (store)
    void import('./store/StorePage').then(({ default: StorePage }) => {
        const draw = () => { const m = location.hash.match(/^#\/store(?:\/(.*))?$/); root.render(_jsx(StorePage, { path: (m?.[1] ?? '').split('/').filter(Boolean) })); };
        window.addEventListener('hashchange', draw);
        draw();
    });
else if (send || location.hash === '#/send/opened')
    void import('./vault/SendOpen').then(({ default: SendOpen }) => root.render(send ? _jsx(SendOpen, { id: send[1], frag: send[2] }) : _jsx("main", { className: "send-page", children: _jsxs("div", { className: "send-card", children: [_jsx("h1", { children: "Opened" }), _jsx("p", { children: "This link has been opened. If you did not save what it showed, ask the sender for a new one." })] }) })));
else
    void ensureSignedIn().then((ready) => { if (ready)
        root.render(_jsx(App, {}));
    else
        root.render(_jsx("p", { style: { padding: 24 }, children: "Signing in\u2026" })); });
