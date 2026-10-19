const KEY = 'ccm.theme';

function stored() {
  try { return localStorage.getItem(KEY); } catch { return null; }
}

function store(value) {
  try { localStorage.setItem(KEY, value); } catch { /* private mode or blocked storage, fine */ }
}

export function currentTheme() {
  const saved = stored();
  if (saved === 'light' || saved === 'dark') return saved;
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function initTheme(button) {
  const apply = (theme) => {
    document.documentElement.dataset.theme = theme;
    button.textContent = theme === 'dark' ? 'Light theme' : 'Dark theme';
    button.setAttribute('aria-pressed', String(theme === 'dark'));
  };
  apply(currentTheme());
  button.addEventListener('click', () => {
    const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    store(next);
    apply(next);
  });
}
