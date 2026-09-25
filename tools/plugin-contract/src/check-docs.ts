import { existsSync } from 'node:fs';
import { join } from 'node:path';

import { loadContract, slugOf } from './contract.js';
import { DOCS_CAPABILITY_PAGES } from './paths.js';

const contract = loadContract();

if (!existsSync(DOCS_CAPABILITY_PAGES)) {
  console.log('check-docs: the nomercy-plugins collection does not exist yet, so there is nothing to check. Phase 6 Task 1 creates it.');
  process.exit(0);
}

let missing = 0;
for (const capability of contract.capabilities) {
  if (!existsSync(join(DOCS_CAPABILITY_PAGES, `${slugOf(capability.name)}.mdx`))) {
    console.error(`MISSING page for ${capability.name}`);
    missing += 1;
  }
}

if (missing > 0) {
  console.error(`check-docs: ${missing} capability page(s) missing. Run npm run build:plugins-reference in docs/nomercy-docs.`);
  process.exit(1);
}

console.log(`check-docs: ${contract.capabilities.length} capabilities have a page.`);
