'use strict';

const SERVER = 'http://localhost:9876';

const dot        = document.getElementById('dot');
const statusTitle = document.getElementById('status-title');
const statusSub   = document.getElementById('status-sub');
const btnCheck    = document.getElementById('btn-check');
const tokenInput  = document.getElementById('api-token');
const btnSaveToken = document.getElementById('btn-save-token');
const btnShowToken = document.getElementById('btn-show-token');

function storageGet(defaults) {
  return new Promise((resolve) => {
    chrome.storage.local.get(defaults, resolve);
  });
}

function storageSet(values) {
  return new Promise((resolve) => {
    chrome.storage.local.set(values, resolve);
  });
}

async function fetchBrowserApiToken() {
  const response = await fetch(`${SERVER}/browser-token`, { signal: AbortSignal.timeout(2500) });
  const payload = await response.json().catch(() => null);
  if (!response.ok || !payload?.apiToken) {
    throw new Error(response.status === 401 ? 'Token required' : (payload?.error || response.statusText || 'Could not pair browser API token'));
  }

  await storageSet({ apiToken: payload.apiToken });
  tokenInput.value = payload.apiToken;
  return payload.apiToken;
}

async function browserApiToken() {
  const settings = await storageGet({ apiToken: '' });
  if (settings.apiToken) {
    return settings.apiToken;
  }

  return fetchBrowserApiToken();
}

async function authHeaders() {
  const apiToken = await browserApiToken();
  const headers = new Headers();
  headers.set('X-YtdlArchive-Token', apiToken);

  return headers;
}

async function checkServer() {
  statusTitle.textContent = 'Checking…';
  statusSub.textContent   = '';
  dot.className = 'dot';

  try {
    const headers = await authHeaders();
    let response = await fetch(`${SERVER}/ping`, { signal: AbortSignal.timeout(2500), headers });
    if (response.status === 401) {
      headers.set('X-YtdlArchive-Token', await fetchBrowserApiToken());
      response = await fetch(`${SERVER}/ping`, { signal: AbortSignal.timeout(2500), headers });
    }

    const data = await response.json().catch(() => null);
    if (!response.ok) {
      throw new Error(response.status === 401 ? 'Token required' : (data?.error || response.statusText || 'Request failed'));
    }

    dot.className = 'dot green';
    statusTitle.textContent = 'Server is running ✓';
    const jellyfin = data.jellyfin?.enabled
      ? `Jellyfin: ${[
          data.jellyfin.musicLibraryName,
          data.jellyfin.podcastLibraryName,
          data.jellyfin.audiobookLibraryName,
          data.jellyfin.otherLibraryName
        ].filter(Boolean).join(' + ')}`
      : 'Jellyfin: not configured';
    statusSub.textContent = `yt-dlp: ${data.ytdlp || 'found'} · ${jellyfin}`;
  } catch (err) {
    const tokenRequired = err?.message === 'Token required';
    dot.className = 'dot red';
    statusTitle.textContent = tokenRequired ? 'Token required' : 'Server not running';
    statusSub.textContent = tokenRequired
      ? 'Open the Jellyfin YtdlArchive settings page once to generate the Browser API token, then try again'
      : 'Restart Jellyfin or enable the YtdlArchive plugin';
  }
}

const settings = await storageGet({ apiToken: '' });
if (settings.apiToken) {
  tokenInput.value = settings.apiToken || '';
}

btnSaveToken.addEventListener('click', () => {
  storageSet({ apiToken: tokenInput.value.trim() }).then(() => {
    statusTitle.textContent = 'Token saved';
    statusSub.textContent = 'Checking server…';
    void checkServer();
  });
});

btnShowToken.addEventListener('click', () => {
  const showing = tokenInput.type === 'text';
  tokenInput.type = showing ? 'password' : 'text';
  btnShowToken.textContent = showing ? 'Show' : 'Hide';
});

btnCheck.addEventListener('click', checkServer);
await checkServer(); // auto-check on open
