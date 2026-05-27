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

  globalScope.YtdlArchiveCommon = {
    DEFAULT_SERVER,
    normalizeServerUrl,
    sendRuntimeMessage,
    storageGet,
    storageSet
  };
})(globalThis);
