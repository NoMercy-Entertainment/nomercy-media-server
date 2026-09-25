import { describe, expect, it } from 'vitest';

import { emitRefusalCodes } from './emit-csharp.js';

const refusals = [
  { code: 'PLUGIN_CAPABILITY_NOT_DECLARED', severity: 'blocked', capability: null, summary: 'A call needed a capability the manifest does not declare.' },
  { code: 'PLUGIN_MANIFEST_V2_DEPRECATED', severity: 'warning', capability: null, summary: 'The plugin ships a contract v2 manifest.' },
  { code: 'PLUGIN_PROCESS_SPAWN_UNDECLARED', severity: 'blocked', capability: 'process.spawn', summary: 'The plugin started a process without process.spawn.' },
] as const;

describe('the generated C# refusal codes', () => {
  const source = emitRefusalCodes([...refusals]);

  it('drops the PLUGIN_ prefix from the constant name', () => {
    expect(source).toContain('public const string CapabilityNotDeclared = "PLUGIN_CAPABILITY_NOT_DECLARED";');
    expect(source).toContain('public const string ManifestV2Deprecated = "PLUGIN_MANIFEST_V2_DEPRECATED";');
    expect(source).toContain('public const string ProcessSpawnUndeclared = "PLUGIN_PROCESS_SPAWN_UNDECLARED";');
  });

  it('writes one descriptor per code with its severity and capability', () => {
    expect(source).toContain('public static IReadOnlyList<PluginRefusalDescriptor> All { get; }');
    expect(source).toContain('PluginRefusalSeverity.Blocked');
    expect(source).toContain('PluginRefusalSeverity.Warning');
    expect(source).toContain('"process.spawn"');
    expect(source).toContain('null');
  });

  it('carries the license header and never the word var', () => {
    expect(source.startsWith('// ---')).toBe(true);
    expect(source).not.toMatch(/\bvar\b/);
  });
});
