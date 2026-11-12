import '@fontsource-variable/sora';
import '@fontsource-variable/figtree';
import { createRoot } from 'react-dom/client';
import App from './App';
import { ensureSignedIn } from './auth';
import './styles.css';
import { applyLook } from './look';

applyLook();

const root = createRoot(document.getElementById('root')!);
void ensureSignedIn().then((ready) => { if (ready) root.render(<App />); else root.render(<p style={{ padding: 24 }}>Signing in…</p>); });
