import type { AnalyzerRule, Capability, Refusal } from './contract.js';
import { slugOf } from './contract.js';

const TRUST_SENTENCE: Record<string, string> = {
  low: 'The owner grants this without being asked a second time.',
  medium: 'The owner is asked before this is granted, and the grant names its scope.',
  high: 'The owner is asked before this is granted, and the prompt says what it exposes.',
};

/**
 * One capability's reference page.
 *
 * Generated rather than written, because a capability added to the contract
 * and left undocumented is a refusal whose Docs link 404s. Every refusal
 * message the server sends points at one of these URLs.
 */
export function emitCapabilityPage(
  capability: Capability,
  refusals: Refusal[],
  analyzers: AnalyzerRule[],
): string {
  const slug = slugOf(capability.name);
  const mine = refusals.filter(refusal => refusal.capability === capability.name);
  const rules = analyzers.filter(rule => rule.capability === capability.name);

  return `---
title: ${capability.name}
description: ${capability.summary}
tags: [capability, ${slug}]
---

{/* Generated from tools/plugin-contract/contract/capabilities.json. Do not edit. */}

# \`${capability.name}\`

${sentence(capability.summary)}

## What it covers

| | |
| --- | --- |
| Facade | \`${capability.facade}\` |
| Scope | ${capability.scope} |
| Trust | ${capability.trust} |
| Revocable while the plugin runs | ${capability.reversible ? 'yes' : 'no'} |

${TRUST_SENTENCE[capability.trust] ?? ''}

${capability.reversible
    ? 'Taking this grant away stops the next call. The plugin keeps running.'
    : 'Taking this grant away needs the plugin restarted. What it already holds cannot be handed back mid-run.'}

## Asking for it

Name it in the manifest:

\`\`\`json
{
  "capabilities": [
    { "name": "${capability.name}", "scope": "${scopeExample(capability)}" }
  ]
}
\`\`\`

The scope is ${capability.scope}. A call outside it is refused with the grant's own name in the message, so the author reads which scope to widen rather than that something went wrong.

${refusalSection(mine)}
${analyzerSection(rules)}
## Related

- [Every capability](/nomercy-plugins/capabilities)
- [Runtime and isolation](/nomercy-plugins/handbook/runtime-and-isolation)
`;
}

function sentence(summary: string): string {
  const first = summary.charAt(0).toUpperCase() + summary.slice(1);

  return first.endsWith('.') ? first : `${first}.`;
}

function scopeExample(capability: Capability): string {
  if (capability.scope === 'host glob') return '*.example.com';
  if (capability.scope === 'port or range') return '8000-8100';
  if (capability.scope === 'protocol') return 'mdns';
  if (capability.scope.includes('path')) return 'D:/media/films';

  return '*';
}

function refusalSection(refusals: Refusal[]): string {
  if (refusals.length === 0) {
    return '';
  }

  const rows = refusals
    .map(refusal => `| \`${refusal.code}\` | ${refusal.severity} | ${refusal.summary} |`)
    .join('\n');

  return `## When it refuses

| Code | Severity | What happened |
| --- | --- | --- |
${rows}

Every refusal carries what the plugin did, why the server said no, and how to fix it. Read all three before changing code.

`;
}

function analyzerSection(rules: AnalyzerRule[]): string {
  if (rules.length === 0) {
    return '';
  }

  const rows = rules.map(rule => `| \`${rule.id}\` | ${rule.title} |`).join('\n');

  return `## What the analyzer catches first

| Rule | What it says |
| --- | --- |
${rows}

These fire at build time, so the author sees them before the server ever refuses.

`;
}
