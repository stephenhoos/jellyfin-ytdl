'use strict';

if (typeof importScripts === 'function' && !globalThis.YtdlArchiveCommon) {
  importScripts('common.js');
}

const {
  DEFAULT_SAVE_TYPES,
  DEFAULT_SERVER,
  groupedSaveTypes,
  normalizeServerUrl,
  storageGet,
  storageSet,
  stripTargetSuffix,
  targetGroupLabel
} = globalThis.YtdlArchiveCommon;
let configLoadPromise = null;
let contextMenuSaveTypes = [];

const CONTEXT_MENU_ROOT_ID = 'ytdlArchive.sendLink';
const CONTEXT_MENU_SUBSCRIBE_ROOT_ID = 'ytdlArchive.subscribeChannel';
const CONTEXT_MENU_SUBSCRIBE_DOWNLOAD_ROOT_ID = 'ytdlArchive.subscribeChannelAndDownload';
const CONTEXT_MENU_CONTEXTS = ['link', 'page', 'video', 'audio'];

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

async function authorizedFetch(path, options = {}, forceRefresh = false) {
  const settings = await connectionSettings();
  const headers = new Headers(options.headers);
  headers.set('X-YtdlArchive-Token', await browserApiToken(forceRefresh));

  const fetchOptions = { ...options, headers };
  let response = await fetch(`${settings.serverUrl}${path}`, fetchOptions);
  if (response.status === 401 && !forceRefresh) {
    headers.set('X-YtdlArchive-Token', await browserApiToken(true));
    response = await fetch(`${settings.serverUrl}${path}`, fetchOptions);
  }

  const payload = await response.json().catch(() => null);
  if (!response.ok) {
    throw new Error(payload?.error || response.statusText || 'Request failed');
  }

  return payload;
}

function contextMenuLabel(saveType) {
  return stripTargetSuffix(String(saveType.label || 'Download'))
    .split(' ')
    .filter(Boolean)
    .join(' ')
    .trim() || 'Download';
}

async function loadSaveTypesForMenu() {
  try {
    const payload = await authorizedFetch('/save-types');
    if (Array.isArray(payload?.saveTypes) && payload.saveTypes.length > 0) {
      return payload.saveTypes;
    }
  } catch (error) {
    console.warn('YtdlArchive could not refresh context menu save types.', error);
  }

  return DEFAULT_SAVE_TYPES;
}

function contextMenuCreate(options) {
  chrome.contextMenus.create(options, () => {
    const error = chrome.runtime.lastError;
    if (error) {
      console.warn('YtdlArchive could not create a context menu item.', error);
    }
  });
}

async function rebuildContextMenus() {
  if (!chrome.contextMenus) {
    return;
  }

  contextMenuSaveTypes = await loadSaveTypesForMenu();

  await new Promise((resolve) => chrome.contextMenus.removeAll(resolve));
  contextMenuCreate({
    id: CONTEXT_MENU_ROOT_ID,
    title: 'Send link to ytdl',
    contexts: CONTEXT_MENU_CONTEXTS
  });
  contextMenuCreate({
    id: CONTEXT_MENU_SUBSCRIBE_ROOT_ID,
    title: 'Subscribe and get new videos',
    contexts: CONTEXT_MENU_CONTEXTS
  });
  contextMenuCreate({
    id: CONTEXT_MENU_SUBSCRIBE_DOWNLOAD_ROOT_ID,
    title: 'Subscribe and get all videos',
    contexts: CONTEXT_MENU_CONTEXTS
  });

  groupedSaveTypes(contextMenuSaveTypes).forEach((items, target) => {
    const groupId = `${CONTEXT_MENU_ROOT_ID}.${target || 'other'}`;
    const subscribeGroupId = `${CONTEXT_MENU_SUBSCRIBE_ROOT_ID}.${target || 'other'}`;
    const subscribeDownloadGroupId = `${CONTEXT_MENU_SUBSCRIBE_DOWNLOAD_ROOT_ID}.${target || 'other'}`;
    contextMenuCreate({
      id: groupId,
      parentId: CONTEXT_MENU_ROOT_ID,
      title: targetGroupLabel(target),
      contexts: CONTEXT_MENU_CONTEXTS
    });
    contextMenuCreate({
      id: subscribeGroupId,
      parentId: CONTEXT_MENU_SUBSCRIBE_ROOT_ID,
      title: targetGroupLabel(target),
      contexts: CONTEXT_MENU_CONTEXTS
    });
    contextMenuCreate({
      id: subscribeDownloadGroupId,
      parentId: CONTEXT_MENU_SUBSCRIBE_DOWNLOAD_ROOT_ID,
      title: targetGroupLabel(target),
      contexts: CONTEXT_MENU_CONTEXTS
    });

    items.forEach((saveType) => {
      const index = contextMenuSaveTypes.indexOf(saveType);
      contextMenuCreate({
        id: `${CONTEXT_MENU_ROOT_ID}.type.${index}`,
        parentId: groupId,
        title: contextMenuLabel(saveType),
        contexts: CONTEXT_MENU_CONTEXTS
      });
      contextMenuCreate({
        id: `${CONTEXT_MENU_SUBSCRIBE_ROOT_ID}.type.${index}`,
        parentId: subscribeGroupId,
        title: contextMenuLabel(saveType),
        contexts: CONTEXT_MENU_CONTEXTS
      });
      contextMenuCreate({
        id: `${CONTEXT_MENU_SUBSCRIBE_DOWNLOAD_ROOT_ID}.type.${index}`,
        parentId: subscribeDownloadGroupId,
        title: contextMenuLabel(saveType),
        contexts: CONTEXT_MENU_CONTEXTS
      });
    });
  });
}

function contextMenuUrl(info, tab) {
  return info.linkUrl || info.srcUrl || info.pageUrl || tab?.url || '';
}

function contextMenuSaveTypeIndex(rootId, menuItemId) {
  const prefix = `${rootId}.type.`;
  if (!menuItemId.startsWith(prefix)) {
    return null;
  }

  return Number.parseInt(menuItemId.slice(prefix.length), 10);
}

function contextMenuRequestBody(url, saveType) {
  return JSON.stringify({
    url,
    quality: saveType.quality || saveType.value,
    audioFormat: saveType.audioFormat,
    target: saveType.target || 'other',
    chapterPercent: saveType.chapterPercent
  });
}

async function queueContextMenuDownload(info, tab) {
  const menuItemId = String(info.menuItemId || '');
  const index = contextMenuSaveTypeIndex(CONTEXT_MENU_ROOT_ID, menuItemId);
  if (index === null) {
    return;
  }

  if (contextMenuSaveTypes.length === 0) {
    contextMenuSaveTypes = await loadSaveTypesForMenu();
  }

  const saveType = contextMenuSaveTypes[index];
  const url = contextMenuUrl(info, tab);
  if (!saveType || !url) {
    return;
  }

  await authorizedFetch('/download', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: contextMenuRequestBody(url, saveType)
  }).catch((error) => {
    console.warn('YtdlArchive context menu download failed.', error);
    throw error;
  });
}

async function queueContextMenuSubscription(info, tab, downloadExistingVideos = false) {
  const menuItemId = String(info.menuItemId || '');
  const rootId = downloadExistingVideos ? CONTEXT_MENU_SUBSCRIBE_DOWNLOAD_ROOT_ID : CONTEXT_MENU_SUBSCRIBE_ROOT_ID;
  const index = contextMenuSaveTypeIndex(rootId, menuItemId);
  if (index === null) {
    return;
  }

  if (contextMenuSaveTypes.length === 0) {
    contextMenuSaveTypes = await loadSaveTypesForMenu();
  }

  const saveType = contextMenuSaveTypes[index];
  const url = contextMenuUrl(info, tab);
  if (!saveType || !url) {
    return;
  }

  await authorizedFetch('/subscriptions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      ...JSON.parse(contextMenuRequestBody(url, saveType)),
      downloadExistingVideos
    })
  }).catch((error) => {
    console.warn('YtdlArchive context menu subscription failed.', error);
    throw error;
  });
}

if (chrome.contextMenus) {
  chrome.runtime.onInstalled?.addListener(() => {
    rebuildContextMenus().catch((error) => {
      console.warn('YtdlArchive could not rebuild context menus.', error);
    });
  });
  chrome.runtime.onStartup?.addListener(() => {
    rebuildContextMenus().catch((error) => {
      console.warn('YtdlArchive could not rebuild context menus.', error);
    });
  });
  chrome.contextMenus.onClicked.addListener((info, tab) => {
    const menuItemId = String(info.menuItemId || '');
    let action;
    if (menuItemId.startsWith(`${CONTEXT_MENU_SUBSCRIBE_ROOT_ID}.type.`)) {
      action = () => queueContextMenuSubscription(info, tab);
    } else if (menuItemId.startsWith(`${CONTEXT_MENU_SUBSCRIBE_DOWNLOAD_ROOT_ID}.type.`)) {
      action = () => queueContextMenuSubscription(info, tab, true);
    } else {
      action = () => queueContextMenuDownload(info, tab);
    }

    action().catch((error) => {
      console.warn('YtdlArchive context menu action failed.', error);
    });
  });
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
