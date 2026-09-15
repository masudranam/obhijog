// Parameters for infra/main.bicep. SPEC.md §19.
//
// **The three secure parameters come from the environment and have no fallback at all.**
// `readEnvironmentVariable(name)` with no second argument fails the build when the variable is
// unset, so forgetting to export one is a compile error naming the variable rather than an
// environment stood up with a blank signing key. That is the posture the seeder already takes
// toward SEED_PASSWORD (§19).
//
// A fallback of `''` was the obvious first attempt and it does not work: the compiler proves
// statically that an empty string can never satisfy the `@minLength` on these parameters and
// rejects the file (BCP333). Which is the right answer — the weaker spelling would have
// deferred a certain failure to deployment time.
//
// `.github/workflows/deploy.yml` exports all three from repository secrets. The CI infra gate
// has no secrets, so it generates throwaway values to type-check against and discards them;
// nothing it generates is ever written down or deployed.

using './main.bicep'

param environmentName = 'dev'
param location = 'westeurope'
param staticWebAppLocation = 'westeurope'

// --- secrets: supplied by the environment, never written here ---------------------------

param postgresAdministratorPassword = readEnvironmentVariable('OBHIJOG_POSTGRES_PASSWORD')
param jwtSigningKey = readEnvironmentVariable('OBHIJOG_JWT_SIGNING_KEY')
param seedPassword = readEnvironmentVariable('OBHIJOG_SEED_PASSWORD')

// --- the image ----------------------------------------------------------------------------
// The workflow exports a digest-pinned reference. The fallback is main.bicep's own default,
// which is Microsoft's hello-world container — enough for `what-if` to have something to plan.

param containerImage = readEnvironmentVariable(
  'OBHIJOG_CONTAINER_IMAGE',
  'mcr.microsoft.com/k8se/quickstart:latest'
)

param containerRegistryServer = readEnvironmentVariable('OBHIJOG_REGISTRY_SERVER', '')
param containerRegistryUsername = readEnvironmentVariable('OBHIJOG_REGISTRY_USERNAME', '')
param containerRegistryPassword = readEnvironmentVariable('OBHIJOG_REGISTRY_PASSWORD', '')

// --- §19 configuration ----------------------------------------------------------------------
// Spelled out rather than left to the template's defaults. The point of this file is that
// reading it tells you what a deployed environment is actually configured with, without
// cross-referencing main.bicep and appsettings.json.

param postgresVersion = '17'
param databaseName = 'obhijog'

param jwtIssuer = 'obhijog'
param jwtAudience = 'obhijog'
param jwtAccessTokenMinutes = 15
param jwtRefreshTokenDays = 14

param storageContainerName = 'complaint-attachments'
param storageReadSasMinutes = 15

param attachmentsMaxSizeBytes = 5242880
param attachmentsAllowedContentTypes = 'image/jpeg,image/png,image/webp'
param attachmentsMaxPerComplaint = 5

// One replica owns the sweep (§17). Raising the replica count means setting this to 0.
param slaSweepIntervalSeconds = 60
param slaWarningThresholdPercent = 80
param slaEscalationLevel2Percent = 150
param slaAutoCloseAfterDays = 7
param slaSweepBatchSize = 200
param slaTransport = 'InProcess'

param notificationsDelivery = 'Log'

// The Static Web App's own hostname is added by the template. This list is empty until there
// is a custom domain.
param additionalCorsOrigins = []
