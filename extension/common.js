'use strict';

(function initializeYtdlArchiveCommon(globalScope) {
  const DEFAULT_SERVER = 'http://localhost:9876';

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

  function normalizeServerUrl(value) {
    const serverUrl = String(value || DEFAULT_SERVER).trim();
    return serverUrl.endsWith('/') ? serverUrl.slice(0, -1) : serverUrl;
  }

  async function requestBrowserApiToken(forceRefresh = false, options = {}) {
    const response = await sendRuntimeMessage({
      type: 'ytdlArchive.getBrowserApiToken',
      forceRefresh
    });
    if (response?.apiToken) {
      await storageSet({ apiToken: response.apiToken });
      options.onToken?.(response.apiToken);
      return response.apiToken;
    }

    throw new Error(response?.error || 'Could not pair browser API token');
  }

  async function serverUrl() {
    if (chrome.runtime?.sendMessage) {
      const response = await sendRuntimeMessage({ type: 'ytdlArchive.getConnectionSettings' }).catch(() => null);
      if (response?.serverUrl) {
        return response.serverUrl;
      }
    }

    const settings = await storageGet({ serverUrl: DEFAULT_SERVER });
    return normalizeServerUrl(settings.serverUrl);
  }

  async function fetchBrowserApiToken(options = {}) {
    if (chrome.runtime?.sendMessage) {
      return requestBrowserApiToken(true, options);
    }

    const response = await fetch(`${await serverUrl()}/browser-token`, options.fetchOptions);
    const payload = await response.json().catch(() => null);
    if (!response.ok || !payload?.apiToken) {
      throw new Error(response.status === 401 ? 'Token required' : (payload?.error || response.statusText || 'Could not pair browser API token'));
    }

    await storageSet({ apiToken: payload.apiToken });
    options.onToken?.(payload.apiToken);
    return payload.apiToken;
  }

  async function browserApiToken(options = {}) {
    const settings = await storageGet({ apiToken: '' });
    if (settings.apiToken) {
      return settings.apiToken;
    }

    return fetchBrowserApiToken(options);
  }

  async function apiFetch(path, options = {}, tokenOptions = {}) {
    const headers = new Headers(options.headers);
    headers.set('X-YtdlArchive-Token', await browserApiToken(tokenOptions));

    const fetchOptions = { ...options, headers };
    const baseUrl = await serverUrl();
    let response = await fetch(`${baseUrl}${path}`, fetchOptions);
    if (response.status === 401) {
      headers.set('X-YtdlArchive-Token', await requestBrowserApiToken(true, tokenOptions));
      response = await fetch(`${baseUrl}${path}`, fetchOptions);
    }

    const payload = await response.json().catch(() => null);
    if (!response.ok) {
      const tokenHint = tokenOptions.includeTokenHint && response.status === 401
        ? ' Open the extension popup and set the Browser API token from Jellyfin.'
        : '';
      throw new Error((response.status === 401 ? 'Token required' : (payload?.error || response.statusText || 'Request failed')) + tokenHint);
    }

    return payload;
  }

  globalScope.YtdlArchiveCommon = {
    DEFAULT_SERVER,
    apiFetch,
    browserApiToken,
    fetchBrowserApiToken,
    normalizeServerUrl,
    requestBrowserApiToken,
    sendRuntimeMessage,
    serverUrl,
    storageGet,
    storageSet
  };
})(globalThis);
