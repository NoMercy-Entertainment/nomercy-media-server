import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import { KMP_SINK_FIXTURES, SINK_FIXTURES } from './paths.js';

/**
 * The web and Compose suites render the same kitchen sink payloads from two
 * copies on disk.
 *
 * Two clients asserting against payloads they each wrote themselves can agree
 * forever while neither draws what the server sends, which is the failure both
 * test files were written for. The copies differ only by the response envelope
 * the web suite mocks, so unwrap that and they have to match exactly.
 */
function unwrap(text: string): unknown {
  const parsed: unknown = JSON.parse(text);

  if (parsed !== null && typeof parsed === 'object' && 'data' in parsed) {
    return (parsed as { data: unknown }).data;
  }

  return parsed;
}

let checked = 0;
let drifted = 0;

for (const file of readdirSync(SINK_FIXTURES)) {
  if (!file.endsWith('.json')) {
    continue;
  }

  const beside = join(KMP_SINK_FIXTURES, file);

  if (!existsSync(beside)) {
    console.error(`MISSING ${file} in the Compose resources`);
    drifted += 1;
    continue;
  }

  checked += 1;

  const web = JSON.stringify(unwrap(readFileSync(join(SINK_FIXTURES, file), 'utf8')));
  const compose = JSON.stringify(unwrap(readFileSync(beside, 'utf8')));

  if (web !== compose) {
    console.error(`DRIFTED ${file}`);
    drifted += 1;
  }
}

if (drifted > 0) {
  console.error(
    `check-fixtures: ${drifted} kitchen sink payload(s) differ between the web and Compose copies. `
      + 'Re-run the kitchen sink plugin and copy its output to both.',
  );
  process.exit(1);
}

console.log(`check-fixtures: ${checked} kitchen sink payloads agree across both clients.`);
