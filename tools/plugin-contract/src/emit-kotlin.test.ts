import { describe, expect, it } from 'vitest';

import { emitCapabilitiesKotlin } from './emit-kotlin.js';

const capabilities = [
  { name: 'network.fetch', scope: 'host glob', trust: 'low', reversible: true, facade: 'IPluginContext.Http', summary: 'outbound HTTP' },
  { name: 'process.spawn', scope: 'binary name', trust: 'high', reversible: false, facade: 'IPluginContext.Process', summary: 'run a binary' },
] as const;

describe('the generated Kotlin vocabulary', () => {
  const source = emitCapabilitiesKotlin([...capabilities], 'tv.nomercy.app.plugins');

  it('declares the package and one constant per capability', () => {
    expect(source).toContain('package tv.nomercy.app.plugins');
    expect(source).toContain('const val NETWORK_FETCH = "network.fetch"');
    expect(source).toContain('const val PROCESS_SPAWN = "process.spawn"');
  });

  it('carries the sets a permissions screen needs', () => {
    expect(source).toContain('val ALL: Set<String>');
    expect(source).toContain('val HIGH: Set<String> = setOf(\n        PROCESS_SPAWN,\n    )');
    expect(source).toContain('val IRREVERSIBLE: Set<String> = setOf(\n        PROCESS_SPAWN,\n    )');
  });
});
