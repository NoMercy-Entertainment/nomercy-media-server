import { describe, expect, it } from 'vitest';

import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import { loadContract } from './contract.js';
import { MEDIA_SERVER } from './paths.js';

describe('the capability vocabulary', () => {
  const contract = loadContract();

  it('carries every capability the design lists', () => {
    expect(contract.capabilities).toHaveLength(54);
  });

  it('names each capability once', () => {
    const names = contract.capabilities.map(capability => capability.name);
    expect(new Set(names).size).toBe(names.length);
  });

  it('puts the nine capabilities a guest may not hold at reversible false', () => {
    const irreversible = contract.capabilities
      .filter(capability => !capability.reversible)
      .map(capability => capability.name)
      .sort();

    expect(irreversible).toEqual([
      'auth.claims',
      'library.import',
      'library.write',
      'media.record',
      'native.code',
      'network.discover',
      'network.listen',
      'process.spawn',
      'storage.path',
    ]);
  });

  it('holds every High capability at High and nothing else', () => {
    const high = contract.capabilities
      .filter(capability => capability.trust === 'high')
      .map(capability => capability.name)
      .sort();

    expect(high).toEqual(['auth.claims', 'native.code', 'process.spawn']);
  });

  it('holds the nine Medium capabilities from Section 10 item 14', () => {
    const medium = contract.capabilities
      .filter(capability => capability.trust === 'medium')
      .map(capability => capability.name)
      .sort();

    expect(medium).toEqual([
      'browser.headless',
      'library.write',
      'network.discover',
      'network.dial',
      'network.listen',
      'ui.overlay',
      'ui.webview',
      'user.watch',
      'users.list',
    ].sort());
  });

  it('gives every capability a facade and a summary', () => {
    for (const capability of contract.capabilities) {
      expect(capability.facade, capability.name).not.toBe('');
      expect(capability.summary, capability.name).not.toBe('');
    }
  });
});

describe('the refusal vocabulary', () => {
  const contract = loadContract();

  it('names each refusal code once', () => {
    const codes = contract.refusals.map(refusal => refusal.code);
    expect(new Set(codes).size).toBe(codes.length);
  });

  it('holds every code the server source refuses with', () => {
    const known = new Set(contract.refusals.map(refusal => refusal.code));
    const literal = /"(PLUGIN_[A-Z0-9_]+)"/g;
    const missing = new Set<string>();

    for (const file of csharpFilesUnder(join(MEDIA_SERVER, 'src'))) {
      const text = readFileSync(file, 'utf8');
      for (const match of text.matchAll(literal)) {
        if (!known.has(match[1])) missing.add(match[1]);
      }
    }

    expect([...missing].sort()).toEqual([]);
  });
});

function csharpFilesUnder(root: string): string[] {
  return readdirSync(root, { withFileTypes: true, recursive: true })
    .filter(entry => entry.isFile() && entry.name.endsWith('.cs'))
    .filter(entry => !/[\/](bin|obj)[\/]/.test(entry.parentPath))
    .map(entry => join(entry.parentPath, entry.name));
}
