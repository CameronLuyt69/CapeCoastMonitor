// Runs before first paint so the page doesn't flash the wrong theme.
(function () {
  var theme = null;
  try { theme = localStorage.getItem('ccm.theme'); } catch (e) { /* storage blocked */ }
  if (theme !== 'light' && theme !== 'dark') {
    theme = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  document.documentElement.setAttribute('data-theme', theme);
})();
