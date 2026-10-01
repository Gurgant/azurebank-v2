targetScope = 'resourceGroup'

param location string = 'italynorth'
@description('Full commit SHA shared by all three public GHCR images.')
@minLength(40)
@maxLength(40)
param imageTag string
param deployApp bool = false
param entraAdminObjectId string
param entraAdminLogin string
@minValue(1)
@maxValue(1800)
param replicaTimeout int = 600

@secure()
param sqlAdminPassword string
@secure()
param appSqlPassword string
@secure()
param migratorSqlPassword string
@secure()
param jwtSecret string
@secure()
param idempotencyHashKey string
@secure()
param stepUpBindingKey string
@secure()
param serviceCredentialBffKey string
@secure()
param auditChainKey string
@secure()
param auditAnchorKey string
@secure()
param securityPinPepper string

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: 'azurebank-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'none'
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

resource sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: 'azurebank-${uniqueString(resourceGroup().id)}'
  location: location
  properties: {
    version: '12.0'
    administratorLogin: 'azurebank_sql_admin'
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: false
      login: entraAdminLogin
      sid: entraAdminObjectId
      tenantId: subscription().tenantId
    }
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sql
  name: 'AzureBank'
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    maxSizeBytes: 2147483648
  }
}

resource azureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sql
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'azurebank-deploy'
  location: location
}

resource githubFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployIdentity
  name: 'github-demo'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:Gurgant/azurebank-v2:environment:demo'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

// Quote passwords for SqlClient; SQL bootstrap uses the restricted format in README.md.
var appConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=AzureBank;User ID=azurebank_app;Password="${replace(appSqlPassword, '"', '""')}";Encrypt=True;TrustServerCertificate=False'
// Enforce the ADR-0058 job budget even if the future tools host does not supply defaults.
var migrationConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=AzureBank;User ID=azurebank_migrator;Password="${replace(migratorSqlPassword, '"', '""')}";Encrypt=True;TrustServerCertificate=False;Connect Timeout=10;ConnectRetryCount=0;Max Pool Size=5;Pool Blocking Period=NeverBlock'

resource app 'Microsoft.App/containerApps@2025-01-01' = if (deployApp) {
  name: 'azurebank'
  location: location
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        transport: 'auto'
      }
      secrets: [
        { name: 'app-connection', value: appConnection }
        { name: 'jwt-secret', value: jwtSecret }
        { name: 'idempotency-hash-key', value: idempotencyHashKey }
        { name: 'stepup-binding-key', value: stepUpBindingKey }
        { name: 'service-key', value: serviceCredentialBffKey }
        { name: 'audit-chain-key', value: auditChainKey }
        { name: 'audit-anchor-key', value: auditAnchorKey }
        { name: 'pin-pepper', value: securityPinPepper }
      ]
    }
    template: {
      containers: [
        {
          name: 'bff'
          image: 'ghcr.io/gurgant/azurebank-bff:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'BackendApi__BaseUrl', value: 'http://localhost:5068' }
            { name: 'ReverseProxy__Clusters__backend-api__Destinations__primary__Address', value: 'http://localhost:5068' }
            { name: 'ServiceCredential__BffKey', secretRef: 'service-key' }
          ]
          probes: [for probe in [
            { type: 'Startup', path: '/health/live' }
            { type: 'Liveness', path: '/health/live' }
            { type: 'Readiness', path: '/health/ready' }
          ]: {
            type: probe.type
            httpGet: {
              path: probe.path
              port: 8080
              scheme: 'HTTP'
            }
            periodSeconds: 3
            initialDelaySeconds: 1
            failureThreshold: 10
          }]
        }
        {
          name: 'api'
          image: 'ghcr.io/gurgant/azurebank-api:${imageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://127.0.0.1:5068' }
            { name: 'ConnectionStrings__DefaultConnection', secretRef: 'app-connection' }
            { name: 'Jwt__Secret', secretRef: 'jwt-secret' }
            { name: 'Idempotency__HashKey', secretRef: 'idempotency-hash-key' }
            { name: 'StepUp__BindingKey', secretRef: 'stepup-binding-key' }
            { name: 'ServiceCredential__BffKey', secretRef: 'service-key' }
            { name: 'Audit__ChainKey', secretRef: 'audit-chain-key' }
            { name: 'Audit__AnchorKey', secretRef: 'audit-anchor-key' }
            { name: 'Security__PinPepper', secretRef: 'pin-pepper' }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

resource migrate 'Microsoft.App/jobs@2025-01-01' = if (deployApp) {
  name: 'azurebank-migrate'
  location: location
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaRetryLimit: 0
      replicaTimeout: replicaTimeout
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: [
        { name: 'migration-connection', value: migrationConnection }
      ]
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: 'ghcr.io/gurgant/azurebank-tools:${imageTag}'
          args: [
            'migrate'
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'ConnectionStrings__DefaultConnection', secretRef: 'migration-connection' }
          ]
        }
      ]
    }
  }
}

// Built-in role IDs are public constants, not subscription or identity IDs.
var appRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '358470bc-b998-42bd-ab17-a7e34c199c0f')
var jobRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4e3d2b60-56ae-4dc6-a233-09c8e5a82e68')

resource appRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployApp) {
  name: guid(app.id, deployIdentity.id, appRoleId)
  scope: app
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: appRoleId
  }
}

resource jobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployApp) {
  name: guid(migrate.id, deployIdentity.id, jobRoleId)
  scope: migrate
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: jobRoleId
  }
}

output sqlServerFqdn string = sql.properties.fullyQualifiedDomainName
output deploymentClientId string = deployIdentity.properties.clientId
output appUrl string = deployApp ? 'https://${app!.properties.configuration!.ingress!.fqdn}' : ''
