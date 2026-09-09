/**
 * Production configuration. Swapped in by the `fileReplacements` rule in angular.json.
 *
 * The API is served from the same origin in Azure, so the base URL is relative — there
 * is no deployed hostname baked into the bundle.
 */
export const environment = {
  production: true,
  apiBaseUrl: '/api/v1',
};
