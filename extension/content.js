'use strict';

const SERVER = 'http://localhost:9876';
let injected = false;
let saveTypes = [
  { label: 'Best to Other', quality: 'best', icon: '★', target: 'other' },
  { label: '1080p to Other', quality: '1080', icon: 'HD', target: 'other' },
  { label: '720p to Other', quality: '720', icon: 'HD', target: 'other' },
  { label: '480p to Other', quality: '480', icon: 'SD', target: 'other' },
  { label: 'MP3 to Music', quality: 'audio', icon: '♫', audioFormat: 'mp3', target: 'music' },
  { label: 'M4A to Music', quality: 'audio', icon: '♫', audioFormat: 'm4a', target: 'music' },
  { label: 'Opus to Music', quality: 'audio', icon: '♫', audioFormat: 'opus', target: 'music' },
  { label: 'M4B Audiobook', quality: 'audio', icon: '▣', audioFormat: 'm4b', target: 'audiobook' },
  { label: 'M4B Audiobook 10% chapters', quality: 'audio', icon: '▣', audioFormat: 'm4b', target: 'audiobook', chapterPercent: 10 }
];
let saveTypesLoadPromise = null;

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

function sendRuntimeMessage(message) {
  return new Promise((resolve, reject) => {
    if (!chrome.runtime?.sendMessage) {
      reject(new Error('Chrome runtime messaging is not available'));
      return;
    }

    chrome.runtime.sendMessage(message, (response) => {
      if (chrome.runtime.lastError) {
        reject(new Error(chrome.runtime.lastError.message));
        return;
      }

      resolve(response);
    });
  });
}

async function requestBrowserApiToken(forceRefresh = false) {
  const response = await sendRuntimeMessage({
    type: 'ytdlArchive.getBrowserApiToken',
    forceRefresh
  });
  if (response?.apiToken) {
    await storageSet({ apiToken: response.apiToken });
    return response.apiToken;
  }

  throw new Error(response?.error || 'Could not pair browser API token');
}

async function fetchBrowserApiToken() {
  if (chrome.runtime?.sendMessage) {
    return requestBrowserApiToken(true);
  }

  const response = await fetch(`${SERVER}/browser-token`);
  const payload = await response.json().catch(() => null);
  if (!response.ok || !payload?.apiToken) {
    throw new Error(response.status === 401 ? 'Token required' : (payload?.error || response.statusText || 'Could not pair browser API token'));
  }

  await storageSet({ apiToken: payload.apiToken });
  return payload.apiToken;
}

async function browserApiToken() {
  const settings = await storageGet({ apiToken: '' });
  if (settings.apiToken) {
    return settings.apiToken;
  }

  return fetchBrowserApiToken();
}

async function apiFetch(path, options) {
  const apiToken = await browserApiToken();
  const headers = new Headers(options?.headers);
  headers.set('X-YtdlArchive-Token', apiToken);

  const fetchOptions = options ? { ...options, headers } : { headers };
  let response = await fetch(`${SERVER}${path}`, fetchOptions);
  if (response.status === 401) {
    headers.set('X-YtdlArchive-Token', await requestBrowserApiToken(true));
    response = await fetch(`${SERVER}${path}`, fetchOptions);
  }

  const payload = await response.json().catch(() => null);
  if (!response.ok) {
    const tokenHint = response.status === 401 ? ' Open the extension popup and set the Browser API token from Jellyfin.' : '';
    throw new Error((payload?.error || response.statusText || 'Request failed') + tokenHint);
  }

  return payload;
}

function loadSaveTypes() {
  if (!saveTypesLoadPromise) {
    saveTypesLoadPromise = apiFetch('/save-types')
      .then((data) => {
        if (data && Array.isArray(data.saveTypes) && data.saveTypes.length > 0) {
          saveTypes = data.saveTypes;
        }
      })
      .catch((err) => {
        if (err.message.includes('token')) {
          showToast(`⚠ ${err.message}`);
        }
      })
      .finally(() => {
        saveTypesLoadPromise = null;
      });
  }

  return saveTypesLoadPromise;
}

function targetGroupLabel(target) {
  switch (target) {
    case 'music':
      return 'Music';
    case 'podcast':
      return 'Podcast';
    case 'audiobook':
      return 'Audiobook';
    default:
      return 'Video';
  }
}

function targetIcon(target) {
  switch (target) {
    case 'music':
      return '♪';
    case 'podcast':
      return '◌';
    case 'audiobook':
      return '▣';
    default:
      return '▾';
  }
}

function displayLabel(saveType) {
  return saveType.label
    .replace(/\s+to\s+Music$/i, '')
    .replace(/\s+to\s+Podcast$/i, '')
    .replace(/\s+to\s+Audiobooks?$/i, '')
    .replace(/\s+to\s+Video$/i, '')
    .replace(/\s+to\s+Other$/i, '')
    .replace(/^M4B\s+Audiobook\s*/i, 'M4B ')
    .replace(/\s+/g, ' ')
    .trim();
}

function displayIcon(saveType) {
  return saveType.target === 'podcast'
    ? targetIcon(saveType.target)
    : saveType.icon || targetIcon(saveType.target || 'other');
}

function groupedSaveTypes() {
  const groups = new Map();
  saveTypes.forEach((saveType) => {
    const target = saveType.target || 'other';
    if (!groups.has(target)) {
      groups.set(target, []);
    }

    groups.get(target).push(saveType);
  });

  return groups;
}

// ─── Toast helper ─────────────────────────────────────────────────────────────

function showToast(msg) {
  let el = document.getElementById('ytdl-toast');
  if (!el) {
    el = document.createElement('div');
    el.id = 'ytdl-toast';
    document.body.appendChild(el);
  }
  el.textContent = msg;
  el.className = '';
  el.getBoundingClientRect();
  el.className = 'show';
}

// ─── Inject the download button ───────────────────────────────────────────────

function injectButton() {
  if (document.getElementById('ytdl-btn-wrap')) return; // already injected

  // Find the right-side controls in the YouTube player
  const controls = document.querySelector('.ytp-right-controls');
  if (!controls) return;

  // Build wrapper
  const wrap = document.createElement('div');
  wrap.id = 'ytdl-btn-wrap';

  // Build button
  const btn = document.createElement('button');
  btn.id = 'ytdl-btn';
  btn.title = 'Download this video';
  btn.innerHTML = `<span class="ytdl-icon">⬇</span><span class="ytdl-text">DOWNLOAD</span>`;

  // Build quality picker
  const picker = document.createElement('div');
  picker.id = 'ytdl-picker';
  groupedSaveTypes().forEach((items, target) => {
    const section = document.createElement('div');
    section.className = 'ytdl-pick-section';

    const header = document.createElement('div');
    header.className = 'ytdl-pick-header';
    header.textContent = targetGroupLabel(target);
    section.appendChild(header);

    items.forEach(q => {
      const item = document.createElement('button');
      item.type = 'button';
      item.className = 'ytdl-pick-item';

      const icon = document.createElement('span');
      icon.className = 'ytdl-pick-icon';
      icon.textContent = displayIcon(q);
      item.appendChild(icon);

      const label = document.createElement('span');
      label.className = 'ytdl-pick-label';
      label.textContent = displayLabel(q);
      item.appendChild(label);

      item.addEventListener('click', (e) => {
        e.stopPropagation();
        picker.classList.remove('open');
        startDownload(q.quality || q.value, q.audioFormat, q.target || 'other', q.chapterPercent);
      });
      section.appendChild(item);
    });

    picker.appendChild(section);
  });

  // Click: show picker; click elsewhere: close
  btn.addEventListener('click', (e) => {
    e.stopPropagation();
    if (btn.classList.contains('loading') || btn.classList.contains('done')) return;
    picker.classList.toggle('open');
  });
  document.addEventListener('click', () => picker.classList.remove('open'));

  wrap.appendChild(btn);
  wrap.appendChild(picker);

  // Insert before the first child of right controls (before fullscreen etc.)
  controls.insertBefore(wrap, controls.firstChild);
  injected = true;
}

// ─── Trigger download ─────────────────────────────────────────────────────────

function startDownload(quality, audioFormat, target, chapterPercent) {
  const btn = document.getElementById('ytdl-btn');
  const txt = btn.querySelector('.ytdl-text');
  const url = globalThis.location.href.split('&')[0]; // strip extra params, keep ?v=...

  btn.classList.add('loading');
  txt.textContent = 'QUEUING…';

  apiFetch('/download', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ url, quality, audioFormat, target, chapterPercent })
  })
  .then(data => {
    if (data && (data.queued || data.reason === 'already downloading')) {
      btn.classList.remove('loading');
      btn.classList.add('done');
      txt.textContent = 'DOWNLOADING…';
      const dir = data.saveTo || '~/Downloads/YouTube';
      showToast(`⬇ Downloading to ${dir}`);
      pollStatus(url);
    } else {
      throw new Error(data?.error || 'Server error');
    }
  })
  .catch(err => {
    btn.classList.remove('loading');
    btn.classList.add('error');
    txt.textContent = 'ERROR';
    const msg = err.message.includes('fetch')
      ? '⚠ Downloader server not running. Restart Jellyfin or enable the YtdlArchive plugin.'
      : `⚠ ${err.message}`;
    showToast(msg);
    setTimeout(() => {
      btn.classList.remove('error');
      txt.textContent = 'DOWNLOAD';
    }, 3000);
  });
}

// ─── Poll for completion ──────────────────────────────────────────────────────

function pollStatus(url) {
  const interval = setInterval(() => {
    apiFetch('/status')
    .then(data => {
      if (!data) return;

      const entry = data[url];
      if (!entry) return;

      const btn = document.getElementById('ytdl-btn');
      const txt = btn?.querySelector('.ytdl-text');

      if (entry.status === 'done') {
        clearInterval(interval);
        if (btn) {
          btn.classList.remove('done');
          txt.textContent = '✓ SAVED';
        }
        showToast(`✓ Saved: ${entry.title || 'video'}`);
        // Reset after a few seconds
        setTimeout(() => {
          if (btn) {
            btn.classList.remove('done');
            txt.textContent = 'DOWNLOAD';
          }
        }, 5000);
      } else if (entry.status === 'error') {
        clearInterval(interval);
        if (btn) {
          btn.classList.remove('done');
          btn.classList.add('error');
          txt.textContent = 'ERROR';
        }
        showToast(`⚠ Download failed: ${entry.error || 'unknown error'}`);
        setTimeout(() => {
          if (btn) {
            btn.classList.remove('error');
            txt.textContent = 'DOWNLOAD';
          }
        }, 4000);
      }
    })
    .catch(() => clearInterval(interval));
  }, 2000);
}

// ─── Watch for YouTube's SPA navigation ──────────────────────────────────────
// YouTube is a single-page app — the page doesn't reload between videos.
// We watch for URL changes and re-inject the button each time.

let lastUrl = location.href;

function checkAndInject() {
  if (location.href !== lastUrl) {
    lastUrl = location.href;
    injected = false;
    const old = document.getElementById('ytdl-btn-wrap');
    if (old) old.remove();
  }

  if (!injected && (location.pathname === '/watch' || location.pathname.startsWith('/shorts/'))) {
    loadSaveTypes().finally(injectButton);
  }
}

// Use MutationObserver to detect when the player controls render
const observer = new MutationObserver(() => {
  if (!document.getElementById('ytdl-btn-wrap')) {
    injected = false;
  }
  checkAndInject();
});

observer.observe(document.body, { childList: true, subtree: true });

// Also try immediately
checkAndInject();
