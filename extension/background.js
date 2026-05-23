'use strict';

const SERVER = 'http://localhost:9876';

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
  const response = await fetch(`${SERVER}/browser-token`);
  const payload = await response.json().catch(() => null);
  if (!response.ok || !payload?.apiToken) {
    throw new Error(response.status === 401 ? 'Token required' : (payload?.error || response.statusText || 'Could not pair browser API token'));
  }

  await storageSet({ apiToken: payload.apiToken });
  return payload.apiToken;
}

async function browserApiToken(forceRefresh) {
  const settings = await storageGet({ apiToken: '' });
  if (settings.apiToken && !forceRefresh) {
    return settings.apiToken;
  }

  return fetchBrowserApiToken();
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type !== 'ytdlArchive.getBrowserApiToken') {
    return false;
  }

  browserApiToken(message.forceRefresh === true)
    .then((apiToken) => sendResponse({ apiToken }))
    .catch((error) => sendResponse({ error: error.message || 'Could not pair browser API token' }));

  return true;
});
