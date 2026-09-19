import { dirname } from 'node:path';
import { mkdirSync, writeFileSync } from 'node:fs';

import { generatedFiles } from './outputs.js';
import { REPO_ROOT } from './paths.js';

for (const file of generatedFiles()) {
  mkdirSync(dirname(file.path), { recursive: true });
  writeFileSync(file.path, file.content);
  console.log(file.path.replace(REPO_ROOT, '.').replace(/\\/g, '/'));
}
