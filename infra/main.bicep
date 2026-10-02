targetScope = 'resourceGroup'

param location string = 'italynorth'
@description('Full commit SHA shared by the three public GHCR images. Needed only when deployApp is true.')
param imageTag string = ''
@description('False creates what needs no image: the environment, SQL, the deployment identity and its role. True adds the app, the migrate job and the two role assignments.')
param deployApp bool = false
param entraAdminObjectId string
param entraAdminLogin string
@description('Seconds a migrate run may take. Kept under 15 minutes.')
@minValue(60)
@maxValue(840)
param replicaTimeout int = 600

@description('Where the three notify-only alerts send their e-mail. Needed only when deployApp is true; never committed.')
param alertEmail string = ''
@description('False leaves the Deny policy out: the fallback if this subscription refuses a custom policy definition.')
param denyPolicy bool = true
@description('Trigger types the Deny policy lets a job have. The pool job adds Schedule.')
param allowedJobTriggers array = [
  'Manual'
]

@description('Set on every run to a fresh random value that is stored nowhere: nothing signs in with it.')
@secure()
param sqlAdminPassword string

// The nine values below are needed only when deployApp is true; infra/secrets.ps1 supplies them.
@secure()
param appSqlPassword string = ''
@secure()
param migratorSqlPassword string = ''
@secure()
param jwtSecret string = ''
@secure()
param idempotencyHashKey string = ''
@secure()
param stepUpBindingKey string = ''
@secure()
param serviceCredentialBffKey string = ''
@secure()
param auditChainKey string = ''
@secure()
param auditAnchorKey string = ''
@secure()
param securityPinPepper string = ''

var appInputsMissing = length(imageTag) != 40 || empty(alertEmail) || empty(appSqlPassword) || empty(migratorSqlPassword) || empty(jwtSecret) || empty(idempotencyHashKey) || empty(stepUpBindingKey) || empty(serviceCredentialBffKey) || empty(auditChainKey) || empty(auditAnchorKey) || empty(securityPinPepper)
var appName = deployApp && appInputsMissing ? fail('deployApp=true needs a 40-character imageTag, alertEmail and all nine application secrets (run infra/secrets.ps1).') : 'azurebank'

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
  }
}

// Its own resource, not the server's administrators property: that one is read at creation only,
// and a later run of this template would be ignored or refused.
resource entraAdmin 'Microsoft.Sql/servers/administrators@2023-08-01' = {
  parent: sql
  name: 'ActiveDirectory'
  properties: {
    administratorType: 'ActiveDirectory'
    login: entraAdminLogin
    sid: entraAdminObjectId
    tenantId: subscription().tenantId
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
    requestedBackupStorageRedundancy: 'Local'
  }
}

// On the database, not on the server: a lock on the server is inherited by its firewall rules, and
// the users script could then no longer remove the temporary rule it adds.
resource databaseLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: database
  name: 'keep-the-database'
  properties: {
    level: 'CanNotDelete'
    notes: 'Remove this lock before deleting the database on purpose.'
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

// The connection limits (connect timeout, retries, pool size) are the hosts' own defaults (ADR-0058): none is set here.
var appConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=AzureBank;User ID=azurebank_app;Password="${replace(appSqlPassword, '"', '""')}";Encrypt=True;TrustServerCertificate=False'
var migrationConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=AzureBank;User ID=azurebank_migrator;Password="${replace(migratorSqlPassword, '"', '""')}";Encrypt=True;TrustServerCertificate=False'

resource app 'Microsoft.App/containerApps@2025-01-01' = if (deployApp) {
  name: appName
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
            { type: 'Startup', path: '/health/live', timeout: 1 }
            { type: 'Liveness', path: '/health/live', timeout: 1 }
            // Above the 3 s the BFF itself gives the API before it answers Degraded.
            { type: 'Readiness', path: '/health/ready', timeout: 4 }
          ]: {
            type: probe.type
            httpGet: {
              path: probe.path
              port: 8080
              scheme: 'HTTP'
            }
            periodSeconds: 3
            timeoutSeconds: probe.timeout
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

// What a deployment needs: read and write the app and the job, start the job, read its executions,
// read the app's revisions and replicas (why a revision is not ready). It cannot list secrets,
// delete, stop, or touch the environment or the resource group. It CAN write the whole app and the
// whole job, so it can run any image with the secrets in its environment: see the README.
var deployRoleName = guid(resourceGroup().id, 'azurebank-deploy')
// The form every role assignment stores, whatever scope the definition was written at.
var deployRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', deployRoleName)

resource deployRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: deployRoleName
  properties: {
    roleName: 'AzureBank deploy ${uniqueString(resourceGroup().id)}'
    description: 'Move the images of the AzureBank app and of its migrate job, and start that job.'
    type: 'CustomRole'
    assignableScopes: [
      resourceGroup().id
    ]
    permissions: [
      {
        actions: [
          'Microsoft.App/containerApps/read'
          'Microsoft.App/containerApps/write'
          'Microsoft.App/containerApps/revisions/read'
          'Microsoft.App/containerApps/revisions/replicas/read'
          'Microsoft.App/jobs/read'
          'Microsoft.App/jobs/write'
          'Microsoft.App/jobs/start/action'
          'Microsoft.App/jobs/executions/read'
          'Microsoft.App/jobs/execution/read'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
  }
}

resource appRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployApp) {
  name: guid(app.id, deployIdentity.id, deployRoleId)
  scope: app
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: deployRoleId
  }
  dependsOn: [
    deployRole
  ]
}

resource jobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployApp) {
  name: guid(migrate.id, deployIdentity.id, deployRoleId)
  scope: migrate
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: deployRoleId
  }
  dependsOn: [
    deployRole
  ]
}

// The shape the app and its jobs must keep, refused by the platform whoever asks: the deployment
// identity cannot change a policy.
module shape 'guardrails.bicep' = if (denyPolicy) {
  name: 'azurebank-shape-definition'
  scope: subscription()
  params: {
    definitionName: guid(resourceGroup().id, 'azurebank-shape')
  }
}

resource shapeAssignment 'Microsoft.Authorization/policyAssignments@2025-03-01' = if (denyPolicy) {
  name: 'azurebank-shape'
  properties: {
    displayName: 'AzureBank: one small replica, manual jobs'
    policyDefinitionId: shape!.outputs.definitionId
    enforcementMode: 'Default'
    parameters: {
      allowedJobTriggers: {
        value: allowedJobTriggers
      }
    }
  }
}

// Notify only: nothing here stops the app. One e-mail receiver, three rules on the app, one per
// meter that traffic can move: requests, bytes out, replica time.
resource owner 'Microsoft.Insights/actionGroups@2023-01-01' = if (deployApp) {
  name: 'azurebank-owner'
  location: 'global'
  properties: {
    groupShortName: 'azurebank'
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

var alerts = [
  // 66,667 requests is one day of the free monthly grant (2 million / 30), here in one hour.
  { name: 'azurebank-requests', metric: 'Requests', aggregation: 'Total', threshold: 66667, window: 'PT1H', every: 'PT15M', text: 'More than 66,667 requests in one hour.' }
  // 3.3 GiB is one day of the free 100 GB a month.
  { name: 'azurebank-bytes-out', metric: 'TxBytes', aggregation: 'Total', threshold: 3543348019, window: 'P1D', every: 'PT1H', text: 'More than 3.3 GiB sent in one day.' }
  // 0.093 is 66.7 free replica-hours a month, as a daily average of the replica count.
  { name: 'azurebank-replica-time', metric: 'Replicas', aggregation: 'Average', threshold: json('0.093'), window: 'P1D', every: 'PT1H', text: 'The replica ran more than 2.2 hours in one day.' }
]

resource notify 'Microsoft.Insights/metricAlerts@2018-03-01' = [for alert in alerts: if (deployApp) {
  name: alert.name
  location: 'global'
  properties: {
    description: alert.text
    severity: 2
    enabled: true
    scopes: [
      app.id
    ]
    evaluationFrequency: alert.every
    windowSize: alert.window
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'threshold'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'Microsoft.App/containerApps'
          metricName: alert.metric
          operator: 'GreaterThan'
          threshold: alert.threshold
          timeAggregation: alert.aggregation
        }
      ]
    }
    actions: [
      {
        actionGroupId: owner.id
      }
    ]
  }
}]

output sqlServerFqdn string = sql.properties.fullyQualifiedDomainName
output sqlServerName string = sql.name
output deploymentClientId string = deployIdentity.properties.clientId
output deploymentPrincipalId string = deployIdentity.properties.principalId
output appUrl string = deployApp ? 'https://${app!.properties.configuration!.ingress!.fqdn}' : ''
