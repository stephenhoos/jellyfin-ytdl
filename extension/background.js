'use strict';

if (typeof importScripts === 'function' && !globalThis.YtdlArchiveCommon) {
  importScripts('common.js');
}

const {
  DEFAULT_SAVE_TYPES,
  DEFAULT_SERVER,
  normalizeServerUrl,
  storageGet,
  storageSet
} = globalThis.YtdlArchiveCommon;
let configLoadPromise = null;
let contextMenuSaveTypes = [];

const CONTEXT_MENU_ROOT_ID = 'ytdlArchive.sendLink';
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

function stripTargetSuffix(label) {
  const suffixes = [' to Music', ' to Podcast', ' to Audiobook', ' to Audiobooks', ' to Video', ' to Other'];
  const lowerLabel = label.toLocaleLowerCase();
  const suffix = suffixes.find((value) => lowerLabel.endsWith(value.toLocaleLowerCase()));
  return suffix ? label.slice(0, -suffix.length) : label;
}

function contextMenuLabel(saveType) {
  return stripTargetSuffix(String(saveType.label || 'Download'))
    .split(' ')
    .filter(Boolean)
    .join(' ')
    .trim() || 'Download';
}

function groupedSaveTypesForMenu(saveTypes) {
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

  groupedSaveTypesForMenu(contextMenuSaveTypes).forEach((items, target) => {
    const groupId = `${CONTEXT_MENU_ROOT_ID}.${target || 'other'}`;
    contextMenuCreate({
      id: groupId,
      parentId: CONTEXT_MENU_ROOT_ID,
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
    });
  });
}

function contextMenuUrl(info, tab) {
  return info.linkUrl || info.srcUrl || info.pageUrl || tab?.url || '';
}

async function queueContextMenuDownload(info, tab) {
  const match = /\.type\.(\d+)$/.exec(String(info.menuItemId || ''));
  if (!match) {
    return;
  }

  if (contextMenuSaveTypes.length === 0) {
    contextMenuSaveTypes = await loadSaveTypesForMenu();
  }

  const saveType = contextMenuSaveTypes[Number.parseInt(match[1], 10)];
  const url = contextMenuUrl(info, tab);
  if (!saveType || !url) {
    return;
  }

  await authorizedFetch('/download', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      url,
      quality: saveType.quality || saveType.value,
      audioFormat: saveType.audioFormat,
      target: saveType.target || 'other',
      chapterPercent: saveType.chapterPercent
    })
  }).catch((error) => {
    console.warn('YtdlArchive context menu download failed.', error);
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
    queueContextMenuDownload(info, tab).catch((error) => {
      console.warn('YtdlArchive context menu download failed.', error);
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
