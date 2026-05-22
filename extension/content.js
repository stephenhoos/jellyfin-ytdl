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

async function apiFetch(path, options) {
  const settings = await storageGet({ apiToken: '' });
  const headers = new Headers(options?.headers);
  if (settings.apiToken) {
    headers.set('X-YtdlArchive-Token', settings.apiToken);
  }

  const fetchOptions = options ? { ...options, headers } : { headers };
  const response = await fetch(`${SERVER}${path}`, fetchOptions);
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
  saveTypes.forEach(q => {
    const item = document.createElement('div');
    item.className = 'ytdl-pick-item';
    item.innerHTML = `<span class="ytdl-pick-icon">${q.icon}</span>${q.label}`;
    item.addEventListener('click', (e) => {
      e.stopPropagation();
      picker.classList.remove('open');
      startDownload(q.quality || q.value, q.audioFormat, q.target || 'other', q.chapterPercent);
    });
    picker.appendChild(item);
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
