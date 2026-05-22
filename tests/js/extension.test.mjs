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
  globalThis.chrome = {
    storage: {
      local: {
        get(defaults, callback) {
          callback({ ...defaults, apiToken });
        },
        set(_values, callback) {
          callback();
        }
      }
    }
  };
}

function importFresh(path) {
  return import(`${pathToFileURL(path).href}?t=${Date.now()}-${Math.random()}`);
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
});
