import { describe, expect, it } from 'vitest';

import { emitManifestSchema } from './emit-csharp.js';

const capabilities = [
  { name: 'network.fetch', scope: 'host glob', trust: 'low', reversible: true, facade: 'IPluginContext.Http', summary: 'outbound HTTP' },
  { name: 'media.proxy', scope: 'host glob', trust: 'low', reversible: true, facade: 'IPluginContext.Media.Proxy', summary: 'proxy media' },
] as const;

const schema = {
  type: 'object',
  $defs: { capabilityGrant: { properties: { name: { type: 'string', enum: [] } } } },
};

describe('the generated manifest schema', () => {
  const source = emitManifestSchema(schema, [...capabilities]);

  it('fills the capability name enum from the vocabulary', () => {
    expect(source).toContain('"network.fetch"');
    expect(source).toContain('"media.proxy"');
  });

  it('carries the schema on a raw string literal the C# compiler accepts', () => {
    expect(source).toContain('public const string Json = """');
    expect(source).toContain('public static class PluginManifestSchema');
  });

  it('carries the license header and never the word var', () => {
    expect(source.startsWith('// ---')).toBe(true);
    expect(source).not.toMatch(/\bvar\b/);
  });
});
