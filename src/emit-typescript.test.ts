import { describe, expect, it } from 'vitest';

import { emitCapabilitiesTypescript } from './emit-typescript.js';

const capabilities = [
  { name: 'network.fetch', scope: 'host glob', trust: 'low', reversible: true, facade: 'IPluginContext.Http', summary: 'outbound HTTP' },
  { name: 'users.list', scope: '(always)', trust: 'medium', reversible: true, facade: 'IPluginContext.Users.ListAsync', summary: 'list server members' },
] as const;

describe('the generated TypeScript vocabulary', () => {
  const source = emitCapabilitiesTypescript([...capabilities]);

  it('exports a frozen record keyed by capability name', () => {
    expect(source).toContain("'network.fetch': { scope: 'host glob', trust: 'low', reversible: true,");
    expect(source).toContain('export type PluginCapabilityName = keyof typeof PluginCapabilities;');
  });

  it('never writes a bare string union a hand edit could widen', () => {
    expect(source).toContain('} as const;');
  });
});
