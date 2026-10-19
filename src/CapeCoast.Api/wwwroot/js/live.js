// Live updates over Server-Sent Events, falling back to polling when the stream can't be used.

const POLL_MS = 60_000;
const RETRY_STREAM_MS = 120_000;
const DEBOUNCE_MS = 1500;

export function startLive({ onUpdate, onState }) {
  let source = null;
  let pollTimer = null;
  let retryTimer = null;
  let debounce = null;

  const refresh = () => {
    clearTimeout(debounce);
    debounce = setTimeout(onUpdate, DEBOUNCE_MS);
  };

  const startPolling = () => {
    if (pollTimer) return;
    onState('polling');
    pollTimer = setInterval(onUpdate, POLL_MS);
  };

  const stopPolling = () => {
    clearInterval(pollTimer);
    pollTimer = null;
  };

  const connect = () => {
    if (!('EventSource' in window)) {
      startPolling();
      return;
    }
    source = new EventSource('/api/stream');
    source.addEventListener('hello', () => {
      stopPolling();
      onState('live');
    });
    source.addEventListener('update', refresh);
    source.onerror = () => {
      // EventSource would retry on its own, but a refused stream (for example rate limited) never recovers,
      // so drop to polling and try the stream again later
      source.close();
      source = null;
      startPolling();
      clearTimeout(retryTimer);
      retryTimer = setTimeout(connect, RETRY_STREAM_MS);
    };
  };

  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') onUpdate();
  });

  connect();
}
