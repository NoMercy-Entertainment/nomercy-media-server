import { join } from 'node:path';

import { loadContract } from './contract.js';
import { emitCapabilityNames, emitCapabilityVocabulary } from './emit-csharp.js';
import { emitDocsIndex } from './emit-docs-index.js';
import { emitCapabilitiesKotlin } from './emit-kotlin.js';
import { emitCapabilitiesTypescript } from './emit-typescript.js';
import { ABSTRACTIONS_GENERATED, DOCS_INDEX, KMP_CAPABILITIES, WEB_CAPABILITIES } from './paths.js';

export interface GeneratedFile { path: string; content: string }

export const KMP_PACKAGE: string = 'tv.nomercy.app.plugins';

/**
 * Every file this tool owns, in one place.
 *
 * Writing and drift-checking read the same list on purpose. When each kept its
 * own, a newly emitted file was written and never checked.
 */
export function generatedFiles(): GeneratedFile[] {
  const contract = loadContract();

  return [
    { path: join(ABSTRACTIONS_GENERATED, 'PluginCapabilityVocabulary.cs'), content: emitCapabilityVocabulary(contract.capabilities) },
    { path: join(ABSTRACTIONS_GENERATED, 'PluginCapabilityNames.cs'), content: emitCapabilityNames(contract.capabilities) },
    { path: WEB_CAPABILITIES, content: emitCapabilitiesTypescript(contract.capabilities) },
    { path: KMP_CAPABILITIES, content: emitCapabilitiesKotlin(contract.capabilities, KMP_PACKAGE) },
    { path: DOCS_INDEX, content: emitDocsIndex(contract.capabilities) },
  ];
}
