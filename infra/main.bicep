// Obhijog — the whole Azure footprint. SPEC.md §14 F15, §17, §19.
//
// Scope is a resource group:
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam
//
// Three rules shaped this file, and every awkward part of it follows from one of them:
//
//   1. **No secret literal, anywhere.** The values that are secrets arrive as `@secure()`
//      parameters with no defaults, are written into Key Vault here, and are read back by
//      the Container App as Key Vault references. None is ever emitted as an output — a
//      deployment output is readable by anyone with reader on the resource group.
//   2. **No `0.0.0.0` firewall rule, not even briefly.** The database is VNet-injected, so
//      it has no public endpoint and Azure rejects a firewall rule against it at deploy
//      time. Note what that does *not* say: Bicep will compile a firewall rule here
//      without complaint, so the CI `infra gate` greps the compiled ARM for `0.0.0.0` and
//      for `firewallRules` and fails on either. Azure enforcing it is not the same as this
//      repository enforcing it, and only the second one catches the mistake before it ships.
//   3. **Every §19 key that varies by environment is a parameter**, passed to the container
//      as an environment variable. The image's `appsettings.json` defaults are a fallback
//      for a laptop, not the source of truth for a deployed environment.

targetScope = 'resourceGroup'

// --- naming and placement -------------------------------------------------------------

@description('Short prefix for every resource name. Lowercase letters and digits only.')
@minLength(3)
@maxLength(11)
param namePrefix string = 'obhijog'

@description('Environment moniker, part of every resource name.')
@allowed(['dev', 'stg', 'prod'])
param environmentName string = 'dev'

@description('Region for everything except the Static Web App.')
param location string = resourceGroup().location

@description('''
Region for the Static Web App. Deliberately separate from `location`: Static Web Apps exist
in a short list of regions and asking for one outside it fails the deployment rather than
falling back, so it cannot simply inherit the region everything else uses.
''')
@allowed(['westus2', 'centralus', 'eastus2', 'westeurope', 'eastasia'])
param staticWebAppLocation string = 'westeurope'

@description('Tags applied to every resource.')
param tags object = {
  application: 'obhijog'
  environment: environmentName
  managedBy: 'bicep'
}

// --- the image --------------------------------------------------------------------------

@description('''
Fully qualified image reference for the API, digest-pinned by the deploy workflow. The
default is Microsoft's hello-world container: it exists so that a first `what-if` against an
empty resource group has something to plan, and every real deployment overrides it.
''')
param containerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Registry login server, e.g. ghcr.io. Empty for an anonymously pullable image.')
param containerRegistryServer string = ''

@description('Registry username. Ignored when containerRegistryServer is empty.')
param containerRegistryUsername string = ''

@description('Registry password or token. Ignored when containerRegistryServer is empty.')
@secure()
param containerRegistryPassword string = ''

// --- secrets ------------------------------------------------------------------------------
// No defaults. A deployment that does not supply these fails, which is the rule the seeder
// already follows for SEED_PASSWORD (§19) and for the same reason: a default that reaches a
// deployed environment is a vulnerability, not a convenience.

@description('PostgreSQL administrator login. Not a secret; the password is.')
@minLength(4)
param postgresAdministratorLogin string = 'obhijog'

@description('PostgreSQL administrator password.')
@secure()
@minLength(12)
param postgresAdministratorPassword string

@description('JWT signing key. Startup fails below 32 bytes (§19), so the template refuses it too.')
@secure()
@minLength(32)
param jwtSigningKey string

@description('Password for the seeded accounts. Read by `--seed`, never by the running API.')
@secure()
@minLength(10)
param seedPassword string

// --- §19 configuration ---------------------------------------------------------------------

@description('PostgreSQL major version. 17 matches infra/docker-compose.yml.')
@allowed(['16', '17'])
param postgresVersion string = '17'

@description('Burstable B1ms is the cheapest tier that exists — this is a portfolio project (F15).')
param postgresSkuName string = 'Standard_B1ms'

@description('Data disk size. 32 GiB is the smallest flexible server accepts.')
param postgresStorageSizeGB int = 32

@description('Application database name.')
param databaseName string = 'obhijog'

param jwtIssuer string = 'obhijog'
param jwtAudience string = 'obhijog'

@minValue(1)
param jwtAccessTokenMinutes int = 15

@minValue(1)
param jwtRefreshTokenDays int = 14

param storageContainerName string = 'complaint-attachments'

@minValue(1)
@maxValue(1440)
param storageReadSasMinutes int = 15

param attachmentsMaxSizeBytes int = 5242880
param attachmentsAllowedContentTypes string = 'image/jpeg,image/png,image/webp'
param attachmentsMaxPerComplaint int = 5

@description('''
Sweep cadence in seconds; 0 disables the hosted service. It stays on here because the app
runs as a single replica — see `maxReplicas` below, where the reason lives.
''')
@minValue(0)
param slaSweepIntervalSeconds int = 60

param slaWarningThresholdPercent int = 80
param slaEscalationLevel2Percent int = 150
param slaAutoCloseAfterDays int = 7
param slaSweepBatchSize int = 200

@description('InProcess until M10 introduces the Service Bus transport (F16).')
@allowed(['InProcess', 'ServiceBus'])
param slaTransport string = 'InProcess'

@allowed(['Log', 'Email'])
param notificationsDelivery string = 'Log'

@description('''
Extra allowed origins. The Static Web App's own hostname is added automatically below, so
this is for a custom domain or a second front end, not for the ordinary case.
''')
param additionalCorsOrigins array = []

@description('CPU cores per replica. 0.5 pairs with 1Gi, which is a valid Container Apps combination.')
param containerCpu string = '0.5'

@description('Memory per replica. Must pair with containerCpu from the allowed matrix.')
param containerMemory string = '1Gi'

// --- addressing ------------------------------------------------------------------------------

@description('VNet address space. Must contain both subnets below.')
param vnetAddressPrefix string = '10.20.0.0/16'

@description('''
Container Apps infrastructure subnet. A Consumption-only environment requires at least a /23
and the platform rejects anything smaller at create time.
''')
param appsSubnetPrefix string = '10.20.0.0/23'

@description('Delegated to the flexible server, which owns the subnet exclusively.')
param postgresSubnetPrefix string = '10.20.2.0/24'

// --- names -------------------------------------------------------------------------------
// Storage accounts and key vaults share a global namespace, so they carry a hash of the
// resource group id. Everything else stays readable.

var suffix = uniqueString(resourceGroup().id)
var baseName = '${namePrefix}-${environmentName}'
var storageAccountName = take('${namePrefix}${environmentName}${suffix}', 24)
var keyVaultName = take('kv-${namePrefix}-${environmentName}-${suffix}', 24)

var postgresDnsZoneName = '${baseName}-pg.private.postgres.database.azure.com'

// Built-in role definition ids. Role *names* are display strings and are not stable
// identifiers; the GUIDs are.
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

// --- identity --------------------------------------------------------------------------------

// User-assigned rather than system-assigned. The role assignments and the Key Vault
// references below all need a principal id, and a system-assigned identity does not exist
// until the Container App that owns it has been created — which is after that app has
// already tried to resolve its secrets.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${baseName}'
  location: location
  tags: tags
}

// --- network ------------------------------------------------------------------------------

// Subnets are declared inline rather than as child resources. Azure serialises subnet writes
// against the parent VNet, and separate child resources race each other into a conflict on
// any deployment that touches both.
resource vnet 'Microsoft.Network/virtualNetworks@2024-01-01' = {
  name: 'vnet-${baseName}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressPrefix]
    }
    subnets: [
      {
        name: 'snet-apps'
        properties: {
          addressPrefix: appsSubnetPrefix
          delegations: [
            {
              name: 'container-apps'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'snet-postgres'
        properties: {
          addressPrefix: postgresSubnetPrefix
          delegations: [
            {
              name: 'postgres-flexible'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
    ]
  }
}

resource appsSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-01-01' existing = {
  parent: vnet
  name: 'snet-apps'
}

resource postgresSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-01-01' existing = {
  parent: vnet
  name: 'snet-postgres'
}

// A VNet-injected flexible server is reachable only by its FQDN, and that FQDN resolves only
// inside a VNet linked to this zone. Without the link the Container App gets NXDOMAIN, which
// surfaces as a connection timeout rather than as a DNS error.
resource postgresDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: postgresDnsZoneName
  location: 'global'
  tags: tags
}

resource postgresDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: postgresDnsZone
  name: 'link-${baseName}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

// --- database ---------------------------------------------------------------------------------

// Note what is absent: there is no `flexibleServers/firewallRules` resource in this file.
// Supplying `network.delegatedSubnetResourceId` selects private access, and a privately
// accessed server has no firewall for a rule to attach to, so Azure would reject one.
//
// Adding one here would still *compile*, though — that was checked, not assumed — which is
// why `.github/workflows/ci.yml` greps the compiled ARM for `0.0.0.0` and `firewallRules`
// and fails the build on either. Deleting that step is how this comment becomes untrue.
resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'psql-${baseName}'
  location: location
  tags: tags
  sku: {
    name: postgresSkuName
    tier: 'Burstable'
  }
  properties: {
    version: postgresVersion
    administratorLogin: postgresAdministratorLogin
    administratorLoginPassword: postgresAdministratorPassword
    storage: {
      storageSizeGB: postgresStorageSizeGB
      autoGrow: 'Enabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      // Burstable does not offer HA, and §17 does not ask for it.
      mode: 'Disabled'
    }
    network: {
      delegatedSubnetResourceId: postgresSubnet.id
      privateDnsZoneArmResourceId: postgresDnsZone.id
    }
  }
  dependsOn: [
    postgresDnsLink
  ]
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// --- storage ------------------------------------------------------------------------------------

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    // §17: the container is private and reads go through a short-lived SAS. Disabling public
    // access at the account level means a later `publicAccess: 'Blob'` on some container
    // cannot take effect even if someone adds one.
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource attachmentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: storageContainerName
  properties: {
    publicAccess: 'None'
  }
}

// F15: blob access prefers managed identity. This assignment is what makes that possible.
// The API still reads a shared-key connection string today because `CreateReadUrl` signs a
// service SAS, which needs the account key; swapping it for a user delegation SAS is the
// follow-up F15 names, and this role is what that follow-up will use.
resource blobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identity.id, storageBlobDataContributorRoleId)
  properties: {
    principalId: identity.properties.principalId
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageBlobDataContributorRoleId
    )
    principalType: 'ServicePrincipal'
  }
}

// --- key vault -------------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    // RBAC rather than access policies. The Container App's identity is granted a role
    // below, and mixing the two models is how a policy ends up silently overriding a role.
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleId)
  properties: {
    principalId: identity.properties.principalId
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      keyVaultSecretsUserRoleId
    )
    principalType: 'ServicePrincipal'
  }
}

// `SSL Mode=Require;Trust Server Certificate=false` is the §19 Azure form, and the `false` is
// the load-bearing half: requiring TLS while trusting any certificate authenticates nothing.
resource postgresConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'postgres-connection-string'
  properties: {
    value: join(
      [
        'Host=${postgres.properties.fullyQualifiedDomainName}'
        'Port=5432'
        'Database=${databaseName}'
        'Username=${postgresAdministratorLogin}'
        'Password=${postgresAdministratorPassword}'
        'SSL Mode=Require'
        'Trust Server Certificate=false'
      ],
      ';'
    )
  }
}

resource jwtSigningKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'jwt-signing-key'
  properties: {
    value: jwtSigningKey
  }
}

resource storageConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'storage-connection-string'
  properties: {
    value: join(
      [
        'DefaultEndpointsProtocol=https'
        'AccountName=${storage.name}'
        'AccountKey=${storage.listKeys().keys[0].value}'
        'EndpointSuffix=${environment().suffixes.storage}'
      ],
      ';'
    )
  }
}

// Not read by the running API — `dotnet Obhijog.Api.dll --seed` reads it and exits. It lives
// here so that seeding a deployed environment does not mean inventing a password on the spot,
// which is how a known one ends up written into a runbook.
resource seedPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'seed-password'
  properties: {
    value: seedPassword
  }
}

// --- front end -----------------------------------------------------------------------------

// Declared before the Container App because the API's CORS origins are derived from its
// hostname. §19's "these four change together" rule has no teeth across two clouds unless one
// of the four is computed from the other.
resource staticWebApp 'Microsoft.Web/staticSites@2023-12-01' = {
  name: 'stapp-${baseName}'
  location: staticWebAppLocation
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    // The workflow builds and uploads; there is no GitHub integration configured here, which
    // also keeps a repository token out of the template.
    allowConfigFileUpdates: true
    stagingEnvironmentPolicy: 'Enabled'
  }
}

// --- container apps -------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${baseName}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${baseName}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: appsSubnet.id
      // The API is public; only its route to the database is private. `internal: true` would
      // also hide the ingress, which would leave the Static Web App unable to reach it.
      internal: false
    }
  }
}

var corsOrigins = union(['https://${staticWebApp.properties.defaultHostname}'], additionalCorsOrigins)

var useRegistryCredentials = !empty(containerRegistryServer) && !empty(containerRegistryPassword)

var keyVaultSecretRefs = [
  {
    name: 'postgres-connection-string'
    keyVaultUrl: postgresConnectionStringSecret.properties.secretUri
    identity: identity.id
  }
  {
    name: 'jwt-signing-key'
    keyVaultUrl: jwtSigningKeySecret.properties.secretUri
    identity: identity.id
  }
  {
    name: 'storage-connection-string'
    keyVaultUrl: storageConnectionStringSecret.properties.secretUri
    identity: identity.id
  }
]

var registrySecret = useRegistryCredentials
  ? [
      {
        name: 'registry-password'
        value: containerRegistryPassword
      }
    ]
  : []

var configEnv = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }

  { name: 'Jwt__Issuer', value: jwtIssuer }
  { name: 'Jwt__Audience', value: jwtAudience }
  { name: 'Jwt__AccessTokenMinutes', value: string(jwtAccessTokenMinutes) }
  { name: 'Jwt__RefreshTokenDays', value: string(jwtRefreshTokenDays) }

  { name: 'Storage__Container', value: storageContainerName }
  { name: 'Storage__ReadSasMinutes', value: string(storageReadSasMinutes) }

  { name: 'Attachments__MaxSizeBytes', value: string(attachmentsMaxSizeBytes) }
  { name: 'Attachments__AllowedContentTypes', value: attachmentsAllowedContentTypes }
  { name: 'Attachments__MaxPerComplaint', value: string(attachmentsMaxPerComplaint) }

  { name: 'Sla__SweepIntervalSeconds', value: string(slaSweepIntervalSeconds) }
  { name: 'Sla__WarningThresholdPercent', value: string(slaWarningThresholdPercent) }
  { name: 'Sla__EscalationLevel2Percent', value: string(slaEscalationLevel2Percent) }
  { name: 'Sla__AutoCloseAfterDays', value: string(slaAutoCloseAfterDays) }
  { name: 'Sla__SweepBatchSize', value: string(slaSweepBatchSize) }
  { name: 'Sla__Transport', value: slaTransport }

  { name: 'Notifications__Delivery', value: notificationsDelivery }
]

// The three Key Vault references. Double underscore is ASP.NET's separator for a nested
// configuration key on every platform, Linux included (§19).
var secretEnv = [
  { name: 'ConnectionStrings__Postgres', secretRef: 'postgres-connection-string' }
  { name: 'Jwt__SigningKey', secretRef: 'jwt-signing-key' }
  { name: 'Storage__ConnectionString', secretRef: 'storage-connection-string' }
]

// `Cors:Origins` binds an array, and the configuration provider reads an array from indexed
// keys. One variable per origin is the only spelling that survives environment variables.
var corsEnv = map(range(0, length(corsOrigins)), i => {
  name: 'Cors__Origins__${i}'
  value: corsOrigins[i]
})

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${baseName}-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: useRegistryCredentials
        ? [
            {
              server: containerRegistryServer
              username: containerRegistryUsername
              passwordSecretRef: 'registry-password'
            }
          ]
        : []
      secrets: concat(keyVaultSecretRefs, registrySecret)
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: {
            cpu: json(containerCpu)
            memory: containerMemory
          }
          env: concat(secretEnv, configEnv, corsEnv)
          probes: [
            {
              // Liveness is the bare `/health`: it answers as soon as the process is up and
              // deliberately does not consult the database, because restarting the container
              // does not fix a database that is down.
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              // Readiness is `/health/ready`, which does check PostgreSQL and the blob
              // container. A replica that cannot reach them is taken out of rotation rather
              // than killed.
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 15
              failureThreshold: 6
            }
          ]
        }
      ]
      scale: {
        // Pinned to exactly one replica, and this is not a cost decision. The SLA sweeper is
        // a hosted service inside this process and §17 says one instance owns it — scaling
        // out would run the ladder N times per interval. §11.3 makes that harmless rather
        // than wrong, but it is still N times the work and N times the log noise. Raising
        // maxReplicas means setting Sla__SweepIntervalSeconds to 0 here and running the
        // sweep from somewhere singular.
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    // Both role assignments must exist before the first revision starts, or resolving the
    // Key Vault references fails and the revision is marked unhealthy with an error that
    // never mentions RBAC.
    keyVaultSecretsUser
    blobRoleAssignment
    attachmentsContainer
    database
  ]
}

// --- outputs ---------------------------------------------------------------------------------
// Names and hostnames only. No connection string, no key, no versioned secret URI — a
// deployment's outputs are readable by anyone with reader on the resource group.

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output apiName string = api.name
output webUrl string = 'https://${staticWebApp.properties.defaultHostname}'
output staticWebAppName string = staticWebApp.name
output keyVaultName string = keyVault.name
output storageAccountName string = storage.name
output postgresServerName string = postgres.name
output managedIdentityClientId string = identity.properties.clientId
