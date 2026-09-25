import { readFileSync } from 'node:fs';

import { generatedFiles } from './outputs.js';
import { REPO_ROOT } from './paths.js';

/**
 * The committed output is what every surface builds against. If the contract
 * moves and nobody regenerates, the server keeps compiling against a vocabulary
 * that no longer exists. This turns that into a red build.
 */
let clean = true;

for (const file of generatedFiles()) {
  const label: string = file.path.replace(REPO_ROOT, '.').replace(/\\/g, '/');

  let committed: string;
  try {
    committed = readFileSync(file.path, 'utf8');
  }
  catch {
    console.error(`${label}: missing. Run "npm run generate".`);
    clean = false;
    continue;
  }

  if (committed === file.content) {
    console.log(`${label}: up to date`);
    continue;
  }

  console.error(`${label}: drifted from the contract. Run "npm run generate" and review the diff.`);
  clean = false;
}

if (!clean)
  process.exit(1);
