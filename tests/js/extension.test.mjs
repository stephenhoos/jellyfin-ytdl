import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import test from 'node:test';

class FakeClassList {
  #values = new Set();

  add(value) {
    this.#values.add(value);
  }

  remove(value) {
    this.#values.delete(value);
  }

  contains(value) {
    return this.#values.has(value);
  }

  toggle(value) {
    if (this.#values.has(value)) {
      this.#values.delete(value);
      return false;
    }

    this.#values.add(value);
    return true;
  }

  toString() {
    return [...this.#values].join(' ');
  }
}

class FakeElement {
  constructor(tagName, ownerDocument) {
    this.tagName = tagName;
    this.ownerDocument = ownerDocument;
    this.children = [];
    this.listeners = new Map();
    this.classList = new FakeClassList();
    this.textContent = '';
    this.innerHTML = '';
    this.className = '';
    this.type = '';
    this.value = '';
  }

  set id(value) {
    this._id = value;
    this.ownerDocument.elements.set(value, this);
  }

  get id() {
    return this._id;
  }

  appendChild(child) {
    this.children.push(child);
    return child;
  }

  insertBefore(child, before) {
    const index = this.children.indexOf(before);
    if (index < 0) {
      this.children.unshift(child);
    } else {
      this.children.splice(index, 0, child);
    }

    return child;
  }

  addEventListener(name, callback) {
    this.listeners.set(name, callback);
  }

  querySelector(selector) {
    if (selector === '.ytdl-text') {
      const text = new FakeElement('span', this.ownerDocument);
      text.className = 'ytdl-text';
      return text;
    }

    return null;
  }

  getBoundingClientRect() {
    return { width: 0, height: 0 };
  }

  remove() {
    if (this.id) {
      this.ownerDocument.elements.delete(this.id);
    }
  }
}

class FakeDocument {
  constructor() {
    this.elements = new Map();
    this.body = new FakeElement('body', this);
    this.rightControls = new FakeElement('div', this);
  }

  createElement(tagName) {
    return new FakeElement(tagName, this);
  }

  getElementById(id) {
    return this.elements.get(id) ?? null;
  }

  querySelector(selector) {
    return selector === '.ytp-right-controls' ? this.rightControls : null;
  }

  addEventListener() {}
}

function installChromeStorage(apiToken = 'token') {
  const store = { apiToken };
  const existingRuntime = globalThis.chrome?.runtime;
  globalThis.chrome = {
    runtime: existingRuntime,
    storage: {
      local: {
        get(defaults, callback) {
          callback({ ...defaults, ...store });
        },
        set(values, callback) {
          Object.assign(store, values);
          callback();
        }
      }
    }
  };
  return store;
}

function sendExtensionMessage(listener, message) {
  return new Promise((resolve) => {
    listener(message, {}, resolve);
  });
}

async function importFresh(path) {
  if (path !== './extension/common.js') {
    await import(`${pathToFileURL('./extension/common.js').href}?t=${Date.now()}-${Math.random()}`);
  }

  return import(`${pathToFileURL(path).href}?t=${Date.now()}-${Math.random()}`);
}

async function flushAsync(turns = 12) {
  for (let index = 0; index < turns; index += 1) {
    await Promise.resolve();
  }
}

function pickerItems(picker) {
  return picker.children.flatMap(section => section.children.slice(1));
}

function pickerLabels(picker) {
  return pickerItems(picker).map(item => item.children[1].textContent);
}

test('content script injects the download button on watch pages', async () => {
  const document = new FakeDocument();
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ&ab_channel=YouTube',
    pathname: '/watch'
  };
  globalThis.MutationObserver = class {
    observe() {}
  };
  installChromeStorage();
  globalThis.fetch = async () => ({
    ok: true,
    json: async () => ({
      saveTypes: [
        { label: 'Best to Other', quality: 'best', icon: '*', target: 'other' }
      ]
    })
  });

  await importFresh('./extension/content.js');
  await new Promise(resolve => setTimeout(resolve, 0));

  assert.ok(document.getElementById('ytdl-btn-wrap'));
  assert.equal(document.rightControls.children[0].id, 'ytdl-btn-wrap');
});

test('content script queues downloads and reports completed status', async () => {
  const document = new FakeDocument();
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ&feature=share',
    pathname: '/watch'
  };
  globalThis.MutationObserver = class {
    observe() {}
  };
  globalThis.setInterval = (callback) => {
    callback();
    return 1;
  };
  globalThis.clearInterval = () => {};
  globalThis.setTimeout = () => {};
  installChromeStorage();
  globalThis.fetch = async (url, options) => {
    if (url.endsWith('/save-types')) {
      return {
        ok: true,
        json: async () => ({
          saveTypes: [
            { label: 'MP3 to Music', quality: 'audio', icon: '*', audioFormat: 'mp3', target: 'music' }
          ]
        })
      };
    }

    if (url.endsWith('/download')) {
      const body = JSON.parse(options.body);
      assert.equal(body.url, 'https://www.youtube.com/watch?v=dQw4w9WgXcQ');
      assert.equal(body.quality, 'audio');
      return {
        ok: true,
        json: async () => ({ queued: true, saveTo: '/media/music' })
      };
    }

    return {
      ok: true,
      json: async () => ({
        'https://www.youtube.com/watch?v=dQw4w9WgXcQ': {
          status: 'done',
          title: 'Saved video'
        }
      })
    };
  };

  await importFresh('./extension/content.js');
  await flushAsync();
  const picker = document.getElementById('ytdl-picker');
  const item = picker.children[0].children[1];
  item.listeners.get('click')({ stopPropagation() {} });
  await flushAsync();

  assert.match(document.getElementById('ytdl-toast').textContent, /Saved video|Downloading/);
});

test('content script renders every save type as a reachable menu option', async () => {
  const document = new FakeDocument();
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
    pathname: '/watch'
  };
  globalThis.MutationObserver = class {
    observe() {}
  };
  globalThis.setInterval = () => 1;
  globalThis.clearInterval = () => {};
  globalThis.setTimeout = () => {};
  installChromeStorage();

  const saveTypes = [
    { label: 'Best to Other', quality: 'best', icon: '*', target: 'other' },
    { label: '1080p to Other', quality: '1080', icon: 'HD', target: 'other' },
    { label: '720p to Other', quality: '720', icon: 'HD', target: 'other' },
    { label: '480p to Other', quality: '480', icon: 'SD', target: 'other' },
    { label: 'MP3 to Music', quality: 'audio', icon: '*', audioFormat: 'mp3', target: 'music' },
    { label: 'M4A to Music', quality: 'audio', icon: '*', audioFormat: 'm4a', target: 'music' },
    { label: 'Opus to Music', quality: 'audio', icon: '*', audioFormat: 'opus', target: 'music' },
    { label: 'MP3 to Podcast', quality: 'audio', icon: '*', audioFormat: 'mp3', target: 'podcast' },
    { label: 'M4A to Podcast', quality: 'audio', icon: '*', audioFormat: 'm4a', target: 'podcast' },
    { label: 'M4B Audiobook', quality: 'audio', icon: '*', audioFormat: 'm4b', target: 'audiobook' },
    { label: 'M4B Audiobook 10% chapters', quality: 'audio', icon: '*', audioFormat: 'm4b', target: 'audiobook', chapterPercent: 10 },
    { label: 'M4B Audiobook 20% chapters', quality: 'audio', icon: '*', audioFormat: 'm4b', target: 'audiobook', chapterPercent: 20 },
    { label: 'M4A to Audiobooks', quality: 'audio', icon: '*', audioFormat: 'm4a', target: 'audiobook' }
  ];
  const downloads = [];
  globalThis.fetch = async (url, options) => {
    if (url.endsWith('/save-types')) {
      return {
        ok: true,
        json: async () => ({ saveTypes })
      };
    }

    if (url.endsWith('/download')) {
      downloads.push(JSON.parse(options.body));
      return {
        ok: true,
        json: async () => ({ queued: true, saveTo: '/media' })
      };
    }

    return {
      ok: true,
      json: async () => ({})
    };
  };

  await importFresh('./extension/content.js');
  await flushAsync();

  const picker = document.getElementById('ytdl-picker');
  assert.equal(picker.children.length, 4);
  assert.deepEqual(picker.children.map(section => section.children[0].textContent), [
    'Video',
    'Music',
    'Podcast',
    'Audiobook'
  ]);
  assert.equal(pickerItems(picker).length, saveTypes.length);
  assert.deepEqual(pickerLabels(picker), [
    'Best',
    '1080p',
    '720p',
    '480p',
    'MP3',
    'M4A',
    'Opus',
    'MP3',
    'M4A',
    'M4B',
    'M4B 10% chapters',
    'M4B 20% chapters',
    'M4A'
  ]);

  for (const item of pickerItems(picker)) {
    item.listeners.get('click')({ stopPropagation() {} });
  }
  await flushAsync();

  assert.equal(downloads.length, saveTypes.length);
  assert.equal(downloads.at(-1).target, 'audiobook');
  assert.equal(downloads.at(-1).audioFormat, 'm4a');
});

test('popup script reports a healthy server and saves tokens', async () => {
  const document = new FakeDocument();
  for (const id of ['dot', 'status-title', 'status-sub', 'btn-check', 'api-token', 'btn-save-token', 'btn-show-token']) {
    const element = document.createElement(id === 'api-token' ? 'input' : 'div');
    element.id = id;
  }

  globalThis.document = document;
  installChromeStorage('secret-token');
  globalThis.fetch = async () => ({
    ok: true,
    json: async () => ({
      ytdlp: '2026.01.01',
      jellyfin: {
        enabled: true,
        musicLibraryName: 'YT-Music',
        podcastLibraryName: 'YT-Podcast',
        audiobookLibraryName: 'YT-Audiobooks',
        otherLibraryName: 'YT-Other'
      }
    })
  });

  await importFresh('./extension/popup.js');

  assert.equal(document.getElementById('api-token').value, 'secret-token');
  assert.equal(document.getElementById('status-title').textContent, 'Server is running ✓');
  assert.match(document.getElementById('status-sub').textContent, /YT-Music/);

  document.getElementById('btn-show-token').listeners.get('click')();
  assert.equal(document.getElementById('api-token').type, 'text');

  document.getElementById('api-token').value = 'new-token';
  document.getElementById('btn-save-token').listeners.get('click')();
  await flushAsync();
  assert.equal(document.getElementById('status-title').textContent, 'Server is running ✓');
});

test('popup script explains missing token responses', async () => {
  const document = new FakeDocument();
  for (const id of ['dot', 'status-title', 'status-sub', 'btn-check', 'api-token', 'btn-save-token', 'btn-show-token']) {
    const element = document.createElement(id === 'api-token' ? 'input' : 'div');
    element.id = id;
  }

  globalThis.document = document;
  installChromeStorage('');
  globalThis.fetch = async () => ({
    ok: false,
    status: 401,
    json: async () => ({ error: 'token required' })
  });

  await importFresh('./extension/popup.js');

  assert.equal(document.getElementById('status-title').textContent, 'Token required');
  assert.match(document.getElementById('status-sub').textContent, /Browser API token/);
});

test('background script returns cached and refreshed browser tokens', async () => {
  let listener;
  const store = { apiToken: 'cached-token' };
  globalThis.chrome = {
    runtime: {
      onMessage: {
        addListener(callback) {
          listener = callback;
        }
      }
    },
    storage: {
      local: {
        get(defaults, callback) {
          callback({ ...defaults, ...store });
        },
        set(values, callback) {
          Object.assign(store, values);
          callback();
        }
      }
    }
  };

  let tokenFetches = 0;
  globalThis.fetch = async (url) => {
    assert.equal(url, 'http://localhost:9876/browser-token');
    tokenFetches += 1;
    return {
      ok: true,
      json: async () => ({ apiToken: 'fresh-token' })
    };
  };

  await importFresh('./extension/background.js');

  assert.equal(listener({ type: 'ignored' }, {}, () => {}), false);
  assert.deepEqual(
    await sendExtensionMessage(listener, { type: 'ytdlArchive.getBrowserApiToken' }),
    { apiToken: 'cached-token' });
  assert.equal(tokenFetches, 0);

  assert.deepEqual(
    await sendExtensionMessage(listener, { type: 'ytdlArchive.getBrowserApiToken', forceRefresh: true }),
    { apiToken: 'fresh-token' });
  assert.equal(store.apiToken, 'fresh-token');
  assert.equal(tokenFetches, 1);
});

test('background script loads bundled LAN connection config', async () => {
  let listener;
  const store = {};
  globalThis.chrome = {
    runtime: {
      getURL(path) {
        return `chrome-extension://test/${path}`;
      },
      onMessage: {
        addListener(callback) {
          listener = callback;
        }
      }
    },
    storage: {
      local: {
        get(defaults, callback) {
          callback({ ...defaults, ...store });
        },
        set(values, callback) {
          Object.assign(store, values);
          callback();
        }
      }
    }
  };

  globalThis.fetch = async (url) => {
    assert.equal(url, 'chrome-extension://test/config.json');
    return {
      ok: true,
      json: async () => ({
        serverUrl: 'http://192.168.1.25:9876/',
        apiToken: 'lan-token'
      })
    };
  };

  await importFresh('./extension/background.js');

  assert.deepEqual(
    await sendExtensionMessage(listener, { type: 'ytdlArchive.getConnectionSettings' }),
    { apiToken: 'lan-token', serverUrl: 'http://192.168.1.25:9876' });
  assert.equal(store.serverUrl, 'http://192.168.1.25:9876');
  assert.equal(store.apiToken, 'lan-token');
});
