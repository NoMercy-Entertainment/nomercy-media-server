import type { Slot } from './contract.js';

export interface Fixture {
  kind: string;
  slot: string;
  why: string;
  entry: {
    section: string;
    label: string;
    icon: string | null;
    route: string;
    slot: string;
    access: string;
  };
  view: unknown;
}

const PLUGIN_ID: string = '01JBQ0Y1ZQ8W4T7N2M6K5R3H9A';

/**
 * One placement per declared slot, and the view the server answers for it.
 *
 * Generated from the slot vocabulary rather than written per client. Each
 * client renders this same set, so a slot a client silently draws nowhere
 * fails a test instead of looking fine until someone opens that screen. A slot
 * added to the contract arrives here without anyone remembering to add it.
 */
export function emitFixtures(slots: Slot[]): string {
  return `${JSON.stringify(
    {
      plugin: PLUGIN_ID,
      fixtures: slots.map(slot => fixtureFor(slot)),
    },
    null,
    2,
  )}\n`;
}

function fixtureFor(slot: Slot): Fixture {
  const route = `/addons/${PLUGIN_ID}/${slot.kind}/${slot.slot}`;

  return {
    kind: slot.kind,
    slot: slot.slot,
    why: slot.summary,
    entry: {
      section: slot.kind,
      label: labelFor(slot),
      icon: 'radio',
      route,
      slot: slot.slot,
      access: 'shared',
    },
    view: viewFor(slot, route),
  };
}

function labelFor(slot: Slot): string {
  return `${slot.kind} ${slot.slot}`.replace(/\b[a-z]/g, letter => letter.toUpperCase());
}

/**
 * A grid of cards, which is what every slot draws when it draws anything.
 *
 * Plugins describe screens with the app's own components, never their own, so
 * the fixture uses the same two a library screen uses. A client that renders
 * these renders anything the schema can express.
 */
function viewFor(slot: Slot, route: string): unknown {
  return {
    layout: 'standard',
    refreshInterval: 0,
    components: [
      {
        id: `${slot.kind}-${slot.slot}-grid`,
        component: 'NMGrid',
        props: {
          id: `${slot.kind}-${slot.slot}-grid`,
          items: [1, 2].map(index => ({
            id: `${slot.kind}-${slot.slot}-card-${index}`,
            component: 'NMCard',
            props: {
              id: `${slot.kind}-${slot.slot}-card-${index}`,
              data: {
                id: `${slot.kind}-${slot.slot}-card-${index}`,
                name: `Item ${index}`,
                link: `${route}/${index}`,
                cover: index === 1 ? 'https://image.tmdb.org/t/p/w300/sintel.jpg' : null,
                type: 'artist',
              },
              action: {
                kind: 'navigate',
                route: `${route}/${index}`,
              },
            },
          })),
        },
      },
    ],
  };
}
