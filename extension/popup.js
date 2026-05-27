'use strict';

const {
  apiFetch,
  storageGet,
  storageSet
} = globalThis.YtdlArchiveCommon;

const dot        = document.getElementById('dot');
const statusTitle = document.getElementById('status-title');
const statusSub   = document.getElementById('status-sub');
const btnCheck    = document.getElementById('btn-check');
const tokenInput  = document.getElementById('api-token');
const btnSaveToken = document.getElementById('btn-save-token');
const btnShowToken = document.getElementById('btn-show-token');

async function checkServer() {
  statusTitle.textContent = 'Checking…';
  statusSub.textContent   = '';
  dot.className = 'dot';

  try {
    const data = await apiFetch('/ping', { signal: AbortSignal.timeout(2500) }, {
      fetchOptions: { signal: AbortSignal.timeout(2500) },
      onToken: (apiToken) => {
        tokenInput.value = apiToken;
      }
    });
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
