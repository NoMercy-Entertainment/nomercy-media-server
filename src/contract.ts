import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import { CONTRACT_DIR } from './paths.js';

export type Trust = 'low' | 'medium' | 'high';
export type Severity = 'blocked' | 'degraded' | 'warning';

export interface Capability {
  name: string;
  scope: string;
  trust: Trust;
  reversible: boolean;
  facade: string;
  summary: string;
}

export interface Refusal {
  code: string;
  severity: Severity;
  capability: string | null;
  summary: string;
}

export interface Slot { kind: string; slot: string }
export interface AnalyzerRule { id: string; title: string; capability: string | null }

export interface Contract {
  capabilities: Capability[];
  refusals: Refusal[];
  slots: Slot[];
  analyzers: AnalyzerRule[];
  manifestSchema: unknown;
}

function read<T>(file: string): T {
  return JSON.parse(readFileSync(join(CONTRACT_DIR, file), 'utf8')) as T;
}

/**
 * The contract, read once and checked.
 *
 * A capability naming a refusal that does not exist, or a refusal naming a
 * capability that does not, reads as a working contract on every surface and
 * refuses with a code nobody can look up.
 */
export function loadContract(): Contract {
  const contract: Contract = {
    capabilities: read<Capability[]>('capabilities.json'),
    refusals: read<Refusal[]>('refusals.json'),
    slots: read<Slot[]>('slots.json'),
    analyzers: read<AnalyzerRule[]>('analyzers.json'),
    manifestSchema: read<unknown>('manifest.schema.json'),
  };

  const names = new Set(contract.capabilities.map(capability => capability.name));
  for (const refusal of contract.refusals) {
    if (refusal.capability !== null && !names.has(refusal.capability))
      throw new Error(`${refusal.code} names capability ${refusal.capability}, which the vocabulary does not declare`);
  }
  for (const rule of contract.analyzers) {
    if (rule.capability !== null && !names.has(rule.capability))
      throw new Error(`${rule.id} names capability ${rule.capability}, which the vocabulary does not declare`);
  }

  return contract;
}

export function slugOf(name: string): string {
  return name.replace(/\./g, '-');
}

export function pascalOf(name: string): string {
  return name
    .split('.')
    .map(part => part.charAt(0).toUpperCase() + part.slice(1))
    .join('');
}
