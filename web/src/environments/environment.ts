/**
 * Development configuration.
 *
 * `apiBaseUrl` is one of the four things SPEC.md §19 says must change together: the API
 * port, the Angular dev-server origin, `Cors:Origins`, and this value. A mismatch here
 * fails every request at the network layer and looks convincingly like an application bug.
 */
export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5080/api/v1',
};
