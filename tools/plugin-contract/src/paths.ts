import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here: string = dirname(fileURLToPath(import.meta.url));

// server/plugin-contract/src → repo root is three levels up.
export const REPO_ROOT: string = resolve(here, '..', '..', '..');
export const CONTRACT_DIR: string = resolve(here, '..', 'contract');

export const MEDIA_SERVER: string = resolve(REPO_ROOT, 'server', 'nomercy-media-server');
export const ABSTRACTIONS: string = resolve(MEDIA_SERVER, 'src', 'NoMercy.PluginSdk.Abstractions');
export const ABSTRACTIONS_GENERATED: string = resolve(ABSTRACTIONS, 'Generated');
export const ANALYZERS: string = resolve(MEDIA_SERVER, 'src', 'NoMercy.PluginSdk.Analyzers');
export const ANALYZERS_GENERATED: string = resolve(ANALYZERS, 'Generated');

export const WEB_CAPABILITIES: string = resolve(
  REPO_ROOT, 'clients', 'nomercy-app-web', 'src', 'types', 'pluginCapabilities.ts',
);

export const KMP_CAPABILITIES: string = resolve(
  REPO_ROOT, 'clients', 'nomercy-app-kmp', 'shared', 'src', 'commonMain',
  'kotlin', 'tv', 'nomercy', 'app', 'plugins', 'PluginCapabilities.kt',
);

export const DOCS_CAPABILITY_PAGES: string = resolve(
  REPO_ROOT, 'docs', 'nomercy-docs', 'src', 'content', 'nomercy-plugins', 'en', 'capabilities',
);

export const DOCS_INDEX: string = resolve(CONTRACT_DIR, 'docs-index.json');

export const DOCS_PLUGINS: string = resolve(
  REPO_ROOT, 'docs', 'nomercy-docs', 'src', 'content', 'nomercy-plugins', 'en',
);

export const DOCS_PLUGIN_NAV: string = resolve(
  REPO_ROOT, 'docs', 'nomercy-docs', 'src', 'lib', 'nav-structure.plugins.ts',
);

export const FIXTURES: string = resolve(CONTRACT_DIR, 'fixtures.json');

export const WEB_FIXTURES: string = resolve(
  REPO_ROOT, 'clients', 'nomercy-app-web', 'src', 'lib', 'plugin', 'fixtures.generated.ts',
);

export const CAST_FIXTURES: string = resolve(
  REPO_ROOT, 'clients', 'nomercy-cast-player', 'src', 'lib', 'plugin', 'fixtures.generated.ts',
);

export const KMP_FIXTURES: string = resolve(
  REPO_ROOT, 'clients', 'nomercy-app-kmp', 'shared', 'src', 'commonTest',
  'kotlin', 'tv', 'nomercy', 'app', 'plugins', 'PluginFixtures.kt',
);

// The one copy of the kitchen sink payloads, written by the plugin's own test
// run. The web suite renders them in place; everything else reads them here.
export const SINK_FIXTURES: string = resolve(
  REPO_ROOT, 'clients', 'nomercy-app-web', 'tests', 'playwright', 'fixture', 'sink',
);

export const CAST_SINK_COMPONENTS: string = resolve(
  REPO_ROOT, 'clients', 'nomercy-cast-player', 'src', 'lib', 'plugin', 'sinkComponents.generated.ts',
);

export const WEB_SINK_COMPONENTS: string = resolve(
  REPO_ROOT, 'clients', 'nomercy-app-web', 'src', 'lib', 'plugin', 'sinkComponents.generated.ts',
);

export const KMP_SINK_FIXTURES: string = resolve(
  REPO_ROOT, 'clients', 'nomercy-app-kmp', 'shared-ui-compose', 'src', 'jvmTest', 'resources', 'plugin',
);
