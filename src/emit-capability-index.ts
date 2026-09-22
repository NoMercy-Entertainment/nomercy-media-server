import type { Capability } from './contract.js';
import { slugOf } from './contract.js';

const TRUST_ORDER: string[] = ['low', 'medium', 'high'];

const TRUST_HEADING: Record<string, string> = {
  low: 'Granted without a second question',
  medium: 'The owner is asked, and the grant names its scope',
  high: 'The owner is asked, and the prompt says what it exposes',
};

/**
 * The page every capability page links back to, and the one an owner reads
 * before granting anything.
 *
 * Generated so a capability cannot enter the contract and stay off this list.
 */
export function emitCapabilityIndex(capabilities: Capability[]): string {
  const sections = TRUST_ORDER.map(trust => {
    const mine = capabilities.filter(capability => capability.trust === trust);

    if (mine.length === 0) {
      return '';
    }

    const rows = mine
      .map(
        capability =>
          `| [\`${capability.name}\`](/nomercy-plugins/capabilities/${slugOf(capability.name)}) | ${capability.summary} | ${capability.scope} |`,
      )
      .join('\n');

    return `## ${TRUST_HEADING[trust]}

| Capability | What it does | Scope |
| --- | --- | --- |
${rows}
`;
  })
    .filter(section => section !== '')
    .join('\n');

  const irreversible = capabilities.filter(capability => !capability.reversible);

  return `---
title: Capabilities
description: Every capability a plugin can ask for, what it opens, and how far it reaches.
tags: [capability, reference]
---

{/* Generated from tools/plugin-contract/contract/capabilities.json. Do not edit. */}

# Capabilities

A plugin reaches nothing by default. It names what it needs in its manifest, the owner grants it, and every call is checked against that grant before it runs.

There are ${capabilities.length} of them. Each has a scope, so a grant says which hosts, which ports, or which folders, not just which kind of thing.

${sections}
## What a restart costs

${irreversible.length} of the ${capabilities.length} cannot be taken back while the plugin runs. Removing one of these stops the plugin instead of the next call, because what it already holds, a listening socket or a loaded library, cannot be handed back mid-run.

${irreversible.map(capability => `- [\`${capability.name}\`](/nomercy-plugins/capabilities/${slugOf(capability.name)})`).join('\n')}
`;
}
