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
    this._innerHTML = '';
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

  set innerHTML(value) {
    this._innerHTML = value;
    this.children = [];
    if (String(value).includes('ytdl-text')) {
      const icon = new FakeElement('span', this.ownerDocument);
      icon.className = 'ytdl-icon';
      icon.textContent = String(value).includes('＋') ? '＋' : '⬇';
      this.appendChild(icon);

      const text = new FakeElement('span', this.ownerDocument);
      text.className = 'ytdl-text';
      text.textContent = String(value).includes('SUBSCRIBE') ? 'SUBSCRIBE' : 'DOWNLOAD';
      this.appendChild(text);
    }
  }

  get innerHTML() {
    return this._innerHTML;
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
      return this.children.find(child => child.className === 'ytdl-text') ?? null;
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

function importDirect(path) {
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

function installPopupDocument() {
  const document = new FakeDocument();
  for (const id of ['dot', 'status-title', 'status-sub', 'btn-check', 'api-token', 'btn-save-token', 'btn-show-token']) {
    const element = document.createElement(id === 'api-token' ? 'input' : 'div');
    element.id = id;
  }

  globalThis.document = document;
  return document;
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
  globalThis.setTimeout = (callback) => {
    callback();
  };
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

test('content script subscribes to the current channel with selected save type', async () => {
  const document = new FakeDocument();
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ&feature=share',
    pathname: '/watch'
  };
  globalThis.MutationObserver = class {
    observe() {}
  };
  globalThis.setTimeout = (callback) => {
    callback();
  };
  installChromeStorage();
  let subscriptionBody = null;
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

    if (url.endsWith('/subscriptions')) {
      subscriptionBody = JSON.parse(options.body);
      return {
        ok: true,
        json: async () => ({
          subscribed: true,
          subscription: { channelName: 'Test Channel' }
        })
      };
    }

    throw new Error(`Unexpected URL ${url}`);
  };

  await importFresh('./extension/content.js');
  await flushAsync();
  const item = pickerItems(document.getElementById('ytdl-subscribe-picker'))[0];
  item.listeners.get('click')({ stopPropagation() {} });
  await flushAsync();

  assert.deepEqual(subscriptionBody, {
    url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
    quality: 'audio',
    audioFormat: 'mp3',
    target: 'music'
  });
  assert.equal(document.getElementById('ytdl-subscribe-btn').querySelector('.ytdl-text').textContent, 'SUBSCRIBE');
  assert.match(document.getElementById('ytdl-toast').textContent, /Test Channel/);
});

test('content script shows server errors from subscription attempts', async () => {
  const document = new FakeDocument();
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
    pathname: '/watch'
  };
  globalThis.MutationObserver = class {
    observe() {}
  };
  globalThis.setTimeout = (callback) => {
    callback();
  };
  installChromeStorage();
  globalThis.fetch = async (url) => {
    if (url.endsWith('/save-types')) {
      return {
        ok: true,
        json: async () => ({
          saveTypes: [
            { label: 'Best to Other', quality: 'best', icon: '*', target: 'other' }
          ]
        })
      };
    }

    return {
      ok: false,
      statusText: 'Bad Request',
      json: async () => ({ error: 'bad channel' })
    };
  };

  await importFresh('./extension/content.js');
  await flushAsync();
  pickerItems(document.getElementById('ytdl-subscribe-picker'))[0].listeners.get('click')({ stopPropagation() {} });
  await flushAsync();

  assert.equal(document.getElementById('ytdl-subscribe-btn').querySelector('.ytdl-text').textContent, 'SUBSCRIBE');
  assert.match(document.getElementById('ytdl-toast').textContent, /bad channel/);
});

test('content script reports failed download status and resets the button', async () => {
  const document = new FakeDocument();
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
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
  globalThis.setTimeout = (callback) => {
    callback();
  };
  installChromeStorage();
  globalThis.fetch = async (url) => {
    if (url.endsWith('/save-types')) {
      return {
        ok: true,
        json: async () => ({
          saveTypes: [
            { label: 'Best to Other', quality: 'best', icon: '*', target: 'other' }
          ]
        })
      };
    }

    if (url.endsWith('/download')) {
      return {
        ok: true,
        json: async () => ({ queued: true, saveTo: '/media' })
      };
    }

    return {
      ok: true,
      json: async () => ({
        'https://www.youtube.com/watch?v=dQw4w9WgXcQ': {
          status: 'error',
          error: 'disk full'
        }
      })
    };
  };

  await importFresh('./extension/content.js');
  await flushAsync();
  const item = pickerItems(document.getElementById('ytdl-picker'))[0];
  item.listeners.get('click')({ stopPropagation() {} });
  await flushAsync(40);

  assert.equal(document.getElementById('ytdl-btn').querySelector('.ytdl-text').textContent, 'DOWNLOAD');
  assert.match(document.getElementById('ytdl-toast').textContent, /disk full/);
});

test('content script refreshes tokens after authorized requests return 401', async () => {
  const document = new FakeDocument();
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
    pathname: '/watch'
  };
  globalThis.MutationObserver = class {
    observe() {}
  };
  const store = { apiToken: 'old-token' };
  globalThis.chrome = {
    runtime: {
      sendMessage(message, callback) {
        callback(message.type === 'ytdlArchive.getConnectionSettings'
          ? { serverUrl: 'http://localhost:9876' }
          : { apiToken: 'new-token' });
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
  let saveTypeCalls = 0;
  globalThis.fetch = async () => {
    saveTypeCalls += 1;
    return saveTypeCalls === 1
      ? { ok: false, status: 401, json: async () => ({}) }
      : { ok: true, json: async () => ({ saveTypes: [{ label: 'Best to Other', quality: 'best', target: 'other' }] }) };
  };

  await importFresh('./extension/content.js');
  await flushAsync();

  assert.equal(store.apiToken, 'new-token');
  assert.ok(document.getElementById('ytdl-btn-wrap'));
});

test('content script ignores picker clicks while the button is busy', async () => {
  const document = new FakeDocument();
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
    pathname: '/watch'
  };
  globalThis.MutationObserver = class {
    observe() {}
  };
  installChromeStorage();
  globalThis.fetch = async () => ({
    ok: true,
    json: async () => ({ saveTypes: [{ label: 'Best to Other', quality: 'best', target: 'other' }] })
  });

  await importFresh('./extension/content.js');
  await flushAsync();
  const button = document.getElementById('ytdl-btn');
  button.classList.add('loading');
  button.listeners.get('click')({ stopPropagation() {} });

  assert.equal(document.getElementById('ytdl-picker').classList.contains('open'), false);
});

test('content script shows server errors from download queue attempts', async () => {
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
  globalThis.setTimeout = (callback) => {
    callback();
  };
  installChromeStorage();
  globalThis.fetch = async (url) => {
    if (url.endsWith('/save-types')) {
      return {
        ok: true,
        json: async () => ({
          saveTypes: [
            { label: 'Best to Other', quality: 'best', icon: '*', target: 'other' }
          ]
        })
      };
    }

    return {
      ok: false,
      statusText: 'Bad Request',
      json: async () => ({ error: 'bad URL' })
    };
  };

  await importFresh('./extension/content.js');
  await flushAsync();
  pickerItems(document.getElementById('ytdl-picker'))[0].listeners.get('click')({ stopPropagation() {} });
  await flushAsync();

  assert.equal(document.getElementById('ytdl-btn').querySelector('.ytdl-text').textContent, 'DOWNLOAD');
  assert.match(document.getElementById('ytdl-toast').textContent, /bad URL/);
});

test('content script handles unexpected queue responses', async () => {
  const document = new FakeDocument();
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
    pathname: '/watch'
  };
  globalThis.MutationObserver = class {
    observe() {}
  };
  globalThis.setTimeout = (callback) => {
    callback();
  };
  installChromeStorage();
  globalThis.fetch = async (url) => {
    if (url.endsWith('/save-types')) {
      return {
        ok: true,
        json: async () => ({ saveTypes: [{ label: 'Best to Other', quality: 'best', target: 'other' }] })
      };
    }

    return {
      ok: true,
      json: async () => ({})
    };
  };

  await importFresh('./extension/content.js');
  await flushAsync();
  pickerItems(document.getElementById('ytdl-picker'))[0].listeners.get('click')({ stopPropagation() {} });
  await flushAsync();

  assert.match(document.getElementById('ytdl-toast').textContent, /Server error/);
});

test('content script reinjects after YouTube SPA navigation', async () => {
  const document = new FakeDocument();
  let observerCallback;
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=first',
    pathname: '/watch'
  };
  globalThis.MutationObserver = class {
    constructor(callback) {
      observerCallback = callback;
    }

    observe() {}
  };
  installChromeStorage();
  globalThis.fetch = async () => ({
    ok: true,
    json: async () => ({ saveTypes: [{ label: 'Best to Other', quality: 'best', target: 'other' }] })
  });

  await importFresh('./extension/content.js');
  await flushAsync();
  assert.ok(document.getElementById('ytdl-btn-wrap'));

  globalThis.location.href = 'https://www.youtube.com/watch?v=second';
  observerCallback();
  await flushAsync(40);

  assert.ok(document.getElementById('ytdl-btn-wrap'));
});

test('content script resets injection state when the wrapper disappears', async () => {
  const document = new FakeDocument();
  let observerCallback;
  globalThis.document = document;
  globalThis.location = {
    href: 'https://www.youtube.com/watch?v=first',
    pathname: '/watch'
  };
  globalThis.MutationObserver = class {
    constructor(callback) {
      observerCallback = callback;
    }

    observe() {}
  };
  installChromeStorage();
  globalThis.fetch = async () => ({
    ok: true,
    json: async () => ({ saveTypes: [{ label: 'Best to Other', quality: 'best', target: 'other' }] })
  });

  await importFresh('./extension/content.js');
  await flushAsync();
  document.getElementById('ytdl-btn-wrap').remove();
  observerCallback();
  await flushAsync(40);

  assert.ok(document.getElementById('ytdl-btn-wrap'));
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
  const document = installPopupDocument();
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
  const document = installPopupDocument();
  globalThis.chrome = undefined;
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

test('popup script pairs a local browser token when storage is empty', async () => {
  const document = installPopupDocument();
  globalThis.chrome = undefined;
  const store = installChromeStorage('');
  globalThis.fetch = async (url) => {
    if (url.endsWith('/browser-token')) {
      return {
        ok: true,
        json: async () => ({ apiToken: 'paired-token' })
      };
    }

    return {
      ok: true,
      json: async () => ({ ytdlp: 'found', jellyfin: { enabled: false } })
    };
  };

  await importFresh('./extension/popup.js');

  assert.equal(store.apiToken, 'paired-token');
  assert.equal(document.getElementById('api-token').value, 'paired-token');
  assert.match(document.getElementById('status-sub').textContent, /not configured/);
});

test('popup script refreshes the token through runtime messaging after a 401', async () => {
  const document = installPopupDocument();
  const store = { apiToken: 'old-token' };
  globalThis.chrome = {
    runtime: {
      sendMessage(message, callback) {
        if (message.type === 'ytdlArchive.getConnectionSettings') {
          callback({ serverUrl: 'http://lan.example:9876' });
          return;
        }

        callback({ apiToken: 'new-token' });
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
  let pings = 0;
  globalThis.fetch = async () => {
    pings += 1;
    return pings === 1
      ? { ok: false, status: 401, json: async () => ({}) }
      : { ok: true, json: async () => ({ ytdlp: 'found', jellyfin: { enabled: false } }) };
  };

  await importFresh('./extension/popup.js');

  assert.equal(store.apiToken, 'new-token');
  assert.equal(document.getElementById('status-title').textContent, 'Server is running ✓');
});

test('popup script pairs through runtime messaging when storage is empty', async () => {
  const document = installPopupDocument();
  const store = { apiToken: '' };
  globalThis.chrome = {
    runtime: {
      sendMessage(message, callback) {
        callback(message.type === 'ytdlArchive.getConnectionSettings'
          ? { serverUrl: 'http://lan.example:9876' }
          : { apiToken: 'runtime-token' });
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
  globalThis.fetch = async () => ({
    ok: true,
    json: async () => ({ ytdlp: 'found', jellyfin: { enabled: false } })
  });

  await importFresh('./extension/popup.js');

  assert.equal(store.apiToken, 'runtime-token');
  assert.equal(document.getElementById('api-token').value, 'runtime-token');
});

test('popup script reports runtime token pairing failures', async () => {
  const document = installPopupDocument();
  globalThis.chrome = {
    runtime: {
      sendMessage(message, callback) {
        callback(message.type === 'ytdlArchive.getConnectionSettings'
          ? { serverUrl: 'http://lan.example:9876' }
          : { error: 'pairing disabled' });
      }
    },
    storage: {
      local: {
        get(defaults, callback) {
          callback({ ...defaults, apiToken: '' });
        },
        set(_values, callback) {
          callback();
        }
      }
    }
  };
  globalThis.fetch = async () => ({
    ok: true,
    json: async () => ({})
  });

  await importFresh('./extension/popup.js');

  assert.equal(document.getElementById('status-title').textContent, 'Server not running');
});

test('popup script reports non-token server errors', async () => {
  const document = installPopupDocument();
  globalThis.chrome = undefined;
  installChromeStorage('token');
  globalThis.fetch = async () => ({
    ok: false,
    status: 500,
    statusText: 'Internal Server Error',
    json: async () => ({ error: 'boom' })
  });

  await importFresh('./extension/popup.js');

  assert.equal(document.getElementById('status-title').textContent, 'Server not running');
});

test('popup script reports server connectivity errors', async () => {
  const document = installPopupDocument();
  globalThis.chrome = undefined;
  installChromeStorage('token');
  globalThis.fetch = async () => {
    throw new Error('fetch failed');
  };

  await importFresh('./extension/popup.js');

  assert.equal(document.getElementById('status-title').textContent, 'Server not running');
  assert.match(document.getElementById('status-sub').textContent, /Restart Jellyfin/);
});

test('common runtime messaging rejects missing runtime and runtime errors', async () => {
  await importFresh('./extension/common.js');
  globalThis.chrome = {};
  await assert.rejects(
    globalThis.YtdlArchiveCommon.sendRuntimeMessage({ type: 'missing' }),
    /Chrome runtime messaging/
  );

  globalThis.chrome = {
    runtime: {
      lastError: { message: 'runtime exploded' },
      sendMessage(_message, callback) {
        callback();
      }
    }
  };

  await assert.rejects(
    globalThis.YtdlArchiveCommon.sendRuntimeMessage({ type: 'boom' }),
    /runtime exploded/
  );
});

test('background script imports common helpers in a service worker context', async () => {
  let imported;
  delete globalThis.YtdlArchiveCommon;
  globalThis.importScripts = (path) => {
    imported = path;
    globalThis.YtdlArchiveCommon = {
      DEFAULT_SERVER: 'http://localhost:9876',
      normalizeServerUrl(value) {
        return String(value).replace(/\/$/, '');
      },
      storageGet(defaults) {
        return Promise.resolve(defaults);
      },
      storageSet() {
        return Promise.resolve();
      }
    };
  };
  globalThis.chrome = {
    runtime: {
      onMessage: {
        addListener() {}
      }
    }
  };

  await importDirect('./extension/background.js');

  assert.equal(imported, 'common.js');
  delete globalThis.importScripts;
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

test('background script creates context menu save type submenus and queues clicked links', async () => {
  const store = { apiToken: 'menu-token', serverUrl: 'http://localhost:9876' };
  const createdMenus = [];
  const downloads = [];
  const subscriptions = [];
  let clickListener;
  let installedListener;
  let startupListener;
  globalThis.chrome = {
    runtime: {
      lastError: null,
      onInstalled: {
        addListener(callback) {
          installedListener = callback;
        }
      },
      onStartup: {
        addListener(callback) {
          startupListener = callback;
        }
      },
      onMessage: { addListener() {} }
    },
    contextMenus: {
      removeAll(callback) {
        createdMenus.length = 0;
        callback();
      },
      create(options, callback) {
        createdMenus.push(options);
        callback?.();
      },
      onClicked: {
        addListener(callback) {
          clickListener = callback;
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

  const saveTypes = [
    { label: 'MP3 to Music', quality: 'audio', audioFormat: 'mp3', target: 'music' },
    { label: 'M4A to Audiobooks', quality: 'audio', audioFormat: 'm4a', target: 'audiobook' }
  ];
  globalThis.fetch = async (url, options = {}) => {
    if (url.endsWith('/save-types')) {
      assert.equal(options.headers.get('X-YtdlArchive-Token'), 'menu-token');
      return {
        ok: true,
        json: async () => ({ saveTypes })
      };
    }

    if (url.endsWith('/download')) {
      downloads.push(JSON.parse(options.body));
      return {
        ok: true,
        json: async () => ({ queued: true })
      };
    }

    if (url.endsWith('/subscriptions')) {
      subscriptions.push(JSON.parse(options.body));
      return {
        ok: true,
        json: async () => ({ subscribed: true })
      };
    }

    throw new Error(`unexpected fetch ${url}`);
  };

  await importFresh('./extension/background.js');
  clickListener({
    menuItemId: 'ytdlArchive.subscribeChannel.type.0',
    pageUrl: 'https://www.youtube.com/watch?v=first'
  }, {});
  await flushAsync();
  installedListener();
  await flushAsync(40);
  startupListener();
  await flushAsync(40);

  const root = createdMenus.find(menu => menu.id === 'ytdlArchive.sendLink');
  const subscribeRoot = createdMenus.find(menu => menu.id === 'ytdlArchive.subscribeChannel');
  const subscribeDownloadRoot = createdMenus.find(menu => menu.id === 'ytdlArchive.subscribeChannelAndDownload');
  const audiobookGroup = createdMenus.find(menu => menu.title === 'Audiobook');
  const audiobookItem = createdMenus.find(menu => menu.parentId === audiobookGroup.id && menu.title === 'M4A');
  const subscribeAudiobookGroup = createdMenus.find(menu => menu.parentId === subscribeRoot.id && menu.title === 'Audiobook');
  const subscribeAudiobookItem = createdMenus.find(menu => menu.parentId === subscribeAudiobookGroup.id && menu.title === 'M4A');
  const subscribeDownloadAudiobookGroup = createdMenus.find(menu => menu.parentId === subscribeDownloadRoot.id && menu.title === 'Audiobook');
  const subscribeDownloadAudiobookItem = createdMenus.find(menu => menu.parentId === subscribeDownloadAudiobookGroup.id && menu.title === 'M4A');

  assert.equal(root.title, 'Send link to ytdl');
  assert.equal(subscribeRoot.title, 'Subscribe and get new videos');
  assert.equal(subscribeDownloadRoot.title, 'Subscribe and get all videos');
  assert.ok(createdMenus.some(menu => menu.title === 'Music'));
  assert.ok(audiobookItem);
  assert.ok(subscribeAudiobookItem);
  assert.ok(subscribeDownloadAudiobookItem);

  clickListener({
    menuItemId: audiobookItem.id,
    linkUrl: 'https://youtu.be/dQw4w9WgXcQ'
  }, {});
  await flushAsync();

  assert.deepEqual(downloads, [{
    url: 'https://youtu.be/dQw4w9WgXcQ',
    quality: 'audio',
    audioFormat: 'm4a',
    target: 'audiobook'
  }]);

  clickListener({
    menuItemId: subscribeAudiobookItem.id,
    pageUrl: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ'
  }, {});
  await flushAsync();

  assert.deepEqual(subscriptions, [
    {
      url: 'https://www.youtube.com/watch?v=first',
      quality: 'audio',
      audioFormat: 'mp3',
      target: 'music',
      downloadExistingVideos: false
    },
    {
      url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
      quality: 'audio',
      audioFormat: 'm4a',
      target: 'audiobook',
      downloadExistingVideos: false
    }
  ]);

  clickListener({
    menuItemId: subscribeDownloadAudiobookItem.id,
    pageUrl: 'https://www.youtube.com/watch?v=existing'
  }, {});
  await flushAsync();

  assert.deepEqual(subscriptions.at(-1), {
    url: 'https://www.youtube.com/watch?v=existing',
    quality: 'audio',
    audioFormat: 'm4a',
    target: 'audiobook',
    downloadExistingVideos: true
  });
});

test('background script rejects token fetches for invalid configured URLs', async () => {
  let listener;
  const store = { apiToken: '', serverUrl: 'not a url' };
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
  globalThis.fetch = async () => {
    throw new Error('should not fetch');
  };

  await importFresh('./extension/background.js');
  const response = await sendExtensionMessage(listener, {
    type: 'ytdlArchive.getBrowserApiToken',
    forceRefresh: true
  });

  assert.match(response.error, /Configured token missing/);
});

test('background script ignores missing bundled config and failed token payloads', async () => {
  let listener;
  const store = { apiToken: '', serverUrl: 'http://localhost:9876' };
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
  globalThis.fetch = async (url) => url.startsWith('chrome-extension:')
    ? { ok: false, json: async () => null }
    : { ok: false, status: 500, statusText: 'Nope', json: async () => ({}) };

  await importFresh('./extension/background.js');
  const response = await sendExtensionMessage(listener, {
    type: 'ytdlArchive.getBrowserApiToken',
    forceRefresh: true
  });

  assert.match(response.error, /Nope|Could not pair/);
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
