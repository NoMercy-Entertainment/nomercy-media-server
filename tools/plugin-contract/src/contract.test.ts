import { describe, expect, it } from 'vitest';

import { loadContract } from './contract.js';

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
