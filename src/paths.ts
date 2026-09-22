import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here: string = dirname(fileURLToPath(import.meta.url));

// tools/plugin-contract/src → repo root is three levels up.
export const REPO_ROOT: string = resolve(here, '..', '..', '..');
export const CONTRACT_DIR: string = resolve(here, '..', 'contract');

export const MEDIA_SERVER: string = resolve(REPO_ROOT, 'apps', 'nomercy-media-server');
export const ABSTRACTIONS: string = resolve(MEDIA_SERVER, 'src', 'NoMercy.PluginSdk.Abstractions');
export const ABSTRACTIONS_GENERATED: string = resolve(ABSTRACTIONS, 'Generated');
export const ANALYZERS: string = resolve(MEDIA_SERVER, 'src', 'NoMercy.PluginSdk.Analyzers');
export const ANALYZERS_GENERATED: string = resolve(ANALYZERS, 'Generated');

export const WEB_CAPABILITIES: string = resolve(
  REPO_ROOT, 'apps', 'nomercy-app-web', 'src', 'types', 'pluginCapabilities.ts',
);

export const KMP_CAPABILITIES: string = resolve(
  REPO_ROOT, 'apps', 'nomercy-app-kmp', 'shared', 'src', 'commonMain',
  'kotlin', 'tv', 'nomercy', 'app', 'plugins', 'PluginCapabilities.kt',
);

export const DOCS_CAPABILITY_PAGES: string = resolve(
  REPO_ROOT, 'docs', 'nomercy-docs', 'src', 'content', 'nomercy-plugins', 'en', 'capabilities',
);

export const DOCS_INDEX: string = resolve(CONTRACT_DIR, 'docs-index.json');
