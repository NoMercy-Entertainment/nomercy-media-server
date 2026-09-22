import type { Capability } from './contract.js';
import { slugOf } from './contract.js';

/**
 * The capability half of the docs sidebar.
 *
 * `check:nav` fails the docs build when a non-draft page is missing from the
 * manifest, so a generated page needs a generated manifest entry beside it.
 * Hand-maintained, the list went stale the first time the contract grew.
 */
export function emitCapabilityNav(capabilities: Capability[]): string {
  const slugs = capabilities
    .map(capability => `      'capabilities/${slugOf(capability.name)}',`)
    .join('\n');

  return `// Generated from tools/plugin-contract/contract/capabilities.json. Do not edit.
//
// A capability page without a manifest entry fails \`npm run check:nav\`, so this
// is generated beside the pages rather than kept by hand.

import type { NavGroupDef } from './nav-structure';

export const pluginCapabilityNav: NavGroupDef[] = [
  {
    group: 'Capabilities',
    pages: [
      'capabilities',
${slugs}
    ],
  },
];
`;
}
