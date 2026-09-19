import { join } from 'node:path';

import { loadContract } from './contract.js';
import { emitCapabilityNames, emitCapabilityVocabulary } from './emit-csharp.js';
import { ABSTRACTIONS_GENERATED } from './paths.js';

export interface GeneratedFile { path: string; content: string }

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
  ];
}
