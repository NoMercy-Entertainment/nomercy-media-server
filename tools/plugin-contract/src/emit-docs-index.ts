import type { Capability } from './contract.js';
import { slugOf } from './contract.js';

export function emitDocsIndex(capabilities: Capability[]): string {
  return `${JSON.stringify({
    capabilities: capabilities.map(capability => ({
      name: capability.name,
      slug: slugOf(capability.name),
      url: `/nomercy-plugins/capabilities/${slugOf(capability.name)}`,
    })),
  }, null, 2)}\n`;
}
