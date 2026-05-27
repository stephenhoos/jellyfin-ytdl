'use strict';

if (typeof importScripts === 'function' && !globalThis.YtdlArchiveCommon) {
  importScripts('common.js');
}

const {
  DEFAULT_SERVER,
  normalizeServerUrl,
  storageGet,
  storageSet
} = globalThis.YtdlArchiveCommon;
let configLoadPromise = null;

function isLocalServerUrl(serverUrl) {
  try {
    const url = new URL(serverUrl);
    return url.hostname === 'localhost' || url.hostname === '127.0.0.1' || url.hostname === '[::1]';
  } catch {
    return false;
  }
}

async function loadBundledConfig() {
  if (!chrome.runtime?.getURL) {
    return;
  }

  const response = await fetch(chrome.runtime.getURL('config.json')).catch(() => null);
  if (!response?.ok) {
    return;
  }

  const config = await response.json().catch(() => null);
  const values = {};
  if (config?.serverUrl) {
    values.serverUrl = normalizeServerUrl(config.serverUrl);
  }

  if (config?.apiToken) {
    values.apiToken = String(config.apiToken);
  }

  if (Object.keys(values).length > 0) {
    await storageSet(values);
  }
}

async function ensureBundledConfigLoaded() {
  configLoadPromise ??= loadBundledConfig();
  await configLoadPromise;
}

async function connectionSettings() {
  await ensureBundledConfigLoaded();
  const settings = await storageGet({ apiToken: '', serverUrl: DEFAULT_SERVER });
  return {
    apiToken: settings.apiToken || '',
    serverUrl: normalizeServerUrl(settings.serverUrl)
  };
}

async function fetchBrowserApiToken() {
  const settings = await connectionSettings();
  if (!isLocalServerUrl(settings.serverUrl)) {
    throw new Error('Configured token missing; update the Chrome extension config from Jellyfin');
  }

  const response = await fetch(`${settings.serverUrl}/browser-token`);
  const payload = await response.json().catch(() => null);
  if (!response.ok || !payload?.apiToken) {
    throw new Error(response.status === 401 ? 'Token required' : (payload?.error || response.statusText || 'Could not pair browser API token'));
  }

  await storageSet({ apiToken: payload.apiToken });
  return payload.apiToken;
}

async function browserApiToken(forceRefresh) {
  const settings = await connectionSettings();
  if (settings.apiToken && !forceRefresh) {
    return settings.apiToken;
  }

  return fetchBrowserApiToken();
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === 'ytdlArchive.getConnectionSettings') {
    connectionSettings()
      .then((settings) => sendResponse(settings))
      .catch((error) => sendResponse({ error: error.message || 'Could not load connection settings' }));

    return true;
  }

  if (message?.type !== 'ytdlArchive.getBrowserApiToken') {
    return false;
  }

  browserApiToken(message.forceRefresh === true)
    .then((apiToken) => sendResponse({ apiToken }))
    .catch((error) => sendResponse({ error: error.message || 'Could not pair browser API token' }));

  return true;
});
