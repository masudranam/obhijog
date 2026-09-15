/**
 * Production configuration. Swapped in by the `fileReplacements` rule in angular.json.
 *
 * **This relative URL is a placeholder, not a working configuration.** The deployed front
 * end is a Static Web App and the deployed API is a Container App, so they are *not* the
 * same origin — `.github/workflows/deploy.yml` overwrites this file with the API's absolute
 * URL, read from the deployment's own output, before it runs the build. The hostname is not
 * knowable until the infrastructure exists, which is why it is not committed here.
 *
 * Two origins means CORS: `infra/main.bicep` sets the API's `Cors:Origins` from the Static
 * Web App's hostname, so the two cannot drift apart by hand. SPEC §19.
 *
 * The relative value survives because `npm run build` in CI has no deployed API to point at
 * and must still produce a bundle.
 */
export const environment = {
  production: true,
  apiBaseUrl: '/api/v1',
};
