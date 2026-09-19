import { describe, expect, it } from 'vitest';

import { emitCapabilityNames, emitCapabilityVocabulary } from './emit-csharp.js';

const capabilities = [
  { name: 'network.fetch', scope: 'host glob', trust: 'low', reversible: true, facade: 'IPluginContext.Http', summary: 'outbound HTTP through context.Http' },
  { name: 'process.spawn', scope: 'binary name', trust: 'high', reversible: false, facade: 'IPluginContext.Process', summary: 'run a bundled or allowlisted binary in the plugin sandbox' },
] as const;

function descriptor(...args: string[]): string {
  return ['        new(', ...args.map((argument, index) =>
    `            ${argument}${index < args.length - 1 ? ',' : ''}`), '        ),'].join('\n');
}

describe('the generated C# vocabulary', () => {
  it('writes one descriptor per capability with its trust and reversibility', () => {
    const source = emitCapabilityVocabulary([...capabilities]);

    expect(source).toContain(descriptor(
      '"network.fetch"', '"host glob"', 'PluginTrust.Low', 'true',
      '"IPluginContext.Http"', '"outbound HTTP through context.Http"',
    ));
    expect(source).toContain(descriptor(
      '"process.spawn"', '"binary name"', 'PluginTrust.High', 'false',
      '"IPluginContext.Process"', '"run a bundled or allowlisted binary in the plugin sandbox"',
    ));
    expect(source).toContain('public static IReadOnlyList<PluginCapabilityDescriptor> All { get; }');
  });

  it('writes a constant per capability, dots turned into Pascal case', () => {
    const source = emitCapabilityNames([...capabilities]);

    expect(source).toContain('public const string NetworkFetch = "network.fetch";');
    expect(source).toContain('public const string ProcessSpawn = "process.spawn";');
  });

  it('carries the license header and never the word var', () => {
    const source = emitCapabilityVocabulary([...capabilities]);

    expect(source.startsWith('// ---')).toBe(true);
    expect(source).not.toMatch(/\bvar\b/);
  });
});
