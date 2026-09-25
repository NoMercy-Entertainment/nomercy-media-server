import { describe, expect, it } from 'vitest';

import { loadContract, slugOf } from './contract.js';
import { emitCapabilityPage } from './emit-capability-page.js';

const fetchCapability = {
  name: 'network.fetch',
  scope: 'host glob',
  trust: 'low' as const,
  reversible: true,
  facade: 'IPluginContext.Http',
  summary: 'outbound HTTP through context.Http',
};

const listen = {
  name: 'network.listen',
  scope: 'port or range',
  trust: 'medium' as const,
  reversible: false,
  facade: 'IPluginContext.Net.ListenAsync',
  summary: 'listening sockets',
};

describe('a capability page', () => {
  it('carries the title the sidebar and search read', () => {
    const page = emitCapabilityPage(fetchCapability, [], []);

    expect(page).toContain('title: network.fetch');
  });

  it('names the facade the capability actually gates', () => {
    const page = emitCapabilityPage(fetchCapability, [], []);

    expect(page).toContain('IPluginContext.Http');
  });

  // A grant that cannot be taken back mid-run and one that can are different
  // promises to the owner, and the page is where that promise is made.
  it('says a revocable grant stops the next call', () => {
    expect(emitCapabilityPage(fetchCapability, [], [])).toContain('keeps running');
  });

  it('says an irrevocable grant needs a restart', () => {
    expect(emitCapabilityPage(listen, [], [])).toContain('needs the plugin restarted');
  });

  // The manifest snippet is the one thing an author copies, so a scope that
  // reads "host glob" there would be pasted verbatim and refused.
  it('shows a scope an author can paste rather than the scope kind', () => {
    const page = emitCapabilityPage(fetchCapability, [], []);

    expect(page).toContain('"scope": "*.example.com"');
    expect(page).not.toContain('"scope": "host glob"');
  });

  it('lists only the refusals that belong to this capability', () => {
    const page = emitCapabilityPage(
      fetchCapability,
      [
        { code: 'NM-P-0101', severity: 'blocked', capability: 'network.fetch', summary: 'host not granted' },
        { code: 'NM-P-0201', severity: 'blocked', capability: 'storage.private', summary: 'file outside grant' },
      ],
      [],
    );

    expect(page).toContain('NM-P-0101');
    expect(page).not.toContain('NM-P-0201');
  });

  it('leaves the refusal section out when the capability has none', () => {
    expect(emitCapabilityPage(fetchCapability, [], [])).not.toContain('When it refuses');
  });

  it('lists only the analyzer rules that belong to this capability', () => {
    const page = emitCapabilityPage(fetchCapability, [], [
      { id: 'NMP001', title: 'undeclared host', capability: 'network.fetch' },
      { id: 'NMP002', title: 'undeclared path', capability: 'storage.private' },
    ]);

    expect(page).toContain('NMP001');
    expect(page).not.toContain('NMP002');
  });

  // Every refusal the server sends points at one of these URLs. A page that
  // links to a slug the index does not know is a 404 the author reaches while
  // already stuck.
  it('links back to the index at a url the contract also publishes', () => {
    expect(emitCapabilityPage(fetchCapability, [], [])).toContain('/nomercy-plugins/capabilities)');
  });

  it('has a page for every capability the contract declares', () => {
    const contract = loadContract();

    for (const capability of contract.capabilities) {
      const page = emitCapabilityPage(capability, contract.refusals, contract.analyzers);

      expect(page, capability.name).toContain(`title: ${capability.name}`);
      expect(slugOf(capability.name)).toMatch(/^[a-z0-9-]+$/);
    }
  });
});
