import { describe, expect, it } from 'vitest';

import { emitDocsIndex } from './emit-docs-index.js';

describe('the docs index', () => {
  it('maps a capability name to its page slug and url', () => {
    const index = JSON.parse(emitDocsIndex([
      { name: 'network.fetch', scope: 'host glob', trust: 'low', reversible: true, facade: 'IPluginContext.Http', summary: 'outbound HTTP' },
    ]));

    expect(index.capabilities[0]).toEqual({
      name: 'network.fetch',
      slug: 'network-fetch',
      url: '/nomercy-plugins/capabilities/network-fetch',
    });
  });
});
