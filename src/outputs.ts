import { join } from 'node:path';

import { loadContract, slugOf } from './contract.js';
import { emitCapabilityNames, emitCapabilityVocabulary, emitManifestSchema, emitSettingsSchema, emitSlots, emitSlotVocabulary, emitAnalyzerDescriptors, emitRefusalCodes } from './emit-csharp.js';
import { emitCapabilityIndex } from './emit-capability-index.js';
import { emitCapabilityNav } from './emit-capability-nav.js';
import { emitCapabilityPage } from './emit-capability-page.js';
import { emitDocsIndex } from './emit-docs-index.js';
import { emitFixturesKotlin, emitFixturesTypescript } from './emit-fixture-clients.js';
import { emitFixtures } from './emit-fixtures.js';
import { emitSinkComponentsTypescript, sinkComponentNames } from './emit-sink-components.js';
import { emitCapabilitiesKotlin } from './emit-kotlin.js';
import { emitCapabilitiesTypescript } from './emit-typescript.js';
import { ABSTRACTIONS_GENERATED, ANALYZERS_GENERATED, CAST_FIXTURES, DOCS_CAPABILITY_PAGES, DOCS_INDEX, DOCS_PLUGIN_NAV, DOCS_PLUGINS, FIXTURES, KMP_CAPABILITIES, CAST_SINK_COMPONENTS, KMP_FIXTURES, SINK_FIXTURES, WEB_CAPABILITIES, WEB_FIXTURES, WEB_SINK_COMPONENTS } from './paths.js';

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
  const fixtures = emitFixtures(contract.slots);
  const sinkComponents = emitSinkComponentsTypescript(sinkComponentNames(SINK_FIXTURES));

  return [
    { path: join(ABSTRACTIONS_GENERATED, 'PluginCapabilityVocabulary.cs'), content: emitCapabilityVocabulary(contract.capabilities) },
    { path: join(ABSTRACTIONS_GENERATED, 'PluginCapabilityNames.cs'), content: emitCapabilityNames(contract.capabilities) },
    { path: join(ABSTRACTIONS_GENERATED, 'PluginRefusalCodes.cs'), content: emitRefusalCodes(contract.refusals) },
    { path: join(ABSTRACTIONS_GENERATED, 'PluginManifestSchema.cs'), content: emitManifestSchema(contract.manifestSchema, contract.capabilities) },
    { path: join(ABSTRACTIONS_GENERATED, 'PluginSettingsSchema.cs'), content: emitSettingsSchema(contract.settingsSchema) },
    { path: join(ABSTRACTIONS_GENERATED, 'PluginSlots.cs'), content: emitSlots(contract.slots) },
    { path: join(ABSTRACTIONS_GENERATED, 'PluginSlot.cs'), content: emitSlotVocabulary(contract.slots) },
    { path: join(ANALYZERS_GENERATED, 'PluginAnalyzerDescriptors.cs'), content: emitAnalyzerDescriptors(contract.analyzers) },
    { path: WEB_CAPABILITIES, content: emitCapabilitiesTypescript(contract.capabilities) },
    { path: KMP_CAPABILITIES, content: emitCapabilitiesKotlin(contract.capabilities, KMP_PACKAGE) },
    { path: DOCS_INDEX, content: emitDocsIndex(contract.capabilities) },

    // One page per capability. A capability added to the contract and left
    // undocumented is a refusal whose Docs link 404s, and the author reads
    // that link while already stuck.
    ...contract.capabilities.map(capability => ({
      path: join(DOCS_CAPABILITY_PAGES, `${slugOf(capability.name)}.mdx`),
      content: emitCapabilityPage(capability, contract.refusals, contract.analyzers),
    })),

    { path: join(DOCS_PLUGINS, 'capabilities.mdx'), content: emitCapabilityIndex(contract.capabilities) },
    { path: DOCS_PLUGIN_NAV, content: emitCapabilityNav(contract.capabilities) },

    // One placement per declared slot, embedded where each client's test
    // runner can reach it. A Kotlin common test has no filesystem, so reading
    // the JSON off disk would leave the one client with the most screens out.
    { path: FIXTURES, content: fixtures },
    { path: WEB_FIXTURES, content: emitFixturesTypescript(fixtures) },
    { path: CAST_FIXTURES, content: emitFixturesTypescript(fixtures) },
    { path: KMP_FIXTURES, content: emitFixturesKotlin(fixtures, KMP_PACKAGE) },

    // What the kitchen sink really puts on the wire, so each client can be
    // asked whether it draws all of it rather than agreeing with a list it
    // wrote itself.
    { path: WEB_SINK_COMPONENTS, content: sinkComponents },
    { path: CAST_SINK_COMPONENTS, content: sinkComponents },
  ];
}
