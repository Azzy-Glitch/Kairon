/**
 * Preserved entry point.
 *
 * The original screens import the axios instance from here as `API`. That import keeps working
 * unchanged - it now resolves to the shared client in `src/api/client.js` rather than a second,
 * separately configured instance.
 *
 * New code should import the typed service functions instead:
 *   import { incidentsApi } from './api/index';
 */
export { default } from './api/client';
export * from './api/index';
