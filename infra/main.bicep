targetScope = 'resourceGroup'

param location string = 'italynorth'
@description('Full commit SHA shared by the three public GHCR images. Needed only when deployApp is true.')
param imageTag string = ''
@description('False creates what needs no image: the environment, the log workspace, SQL, the three identities and the deployment role. True adds the app, the migrate job and the two role assignments.')
param deployApp bool = false
param entraAdminObjectId string
param entraAdminLogin string
@description('Seconds a migrate run may take. Kept under 15 minutes.')
@minValue(60)
@maxValue(840)
param replicaTimeout int = 600

@description('Where the notify-only alerts send their e-mail. Needed only when deployApp is true; never committed.')
param alertEmail string = ''
@description('False leaves the Deny policy out: the fallback if this subscription refuses a custom policy definition.')
param denyPolicy bool = true
@description('Trigger types the Deny policy lets every job have. A job that runs on a schedule is not added here: it is named in scheduledJobs.')
param allowedJobTriggers array = [
  'Manual'
]
@description('Names of the jobs the Deny policy lets run on a schedule. Every other job is started by hand.')
param scheduledJobs array = [
  'azurebank-pool'
]
@description('False keeps no logs: no workspace, no diagnostic setting, and what the containers print goes nowhere. It deletes neither a workspace nor a setting that already exists.')
param keepLogs bool = true
@description('Daily cap of the log workspace, in GB. Text, because the property is typed as a whole number: a fraction has to pass through json().')
param logDailyCapGb string = '0.05'
@description('True adds the alert on the log workspace. Left out since 2026-10-03: at step 20 of the runbook its metric had no time series for an hour in which the workspace ingested 446 rows. True is for whoever measures again.')
param logVolumeAlert bool = false

// The seven values below are needed only when deployApp is true; infra/secrets.ps1 supplies them.
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

// Bicep 0.47.16 has no types for this API version (BCP081): test_scripts.py checks the properties instead.
#disable-next-line BCP081
resource environment 'Microsoft.App/managedEnvironments@2026-07-01' = {
  name: 'azurebank-env'
  location: location
  properties: {
    // Named, on an API version that has the property: a request that names no mode was taken as
    // Express on this subscription and refused. Express has no Azure Monitor logs, no second
    // container and no jobs (README.md, "Measured on Azure").
    environmentMode: 'WorkloadProfiles'
    appLogsConfiguration: {
      // With 'azure-monitor' the diagnostic setting below says where the logs go, and no workspace
      // key is held by the environment.
      destination: keepLogs ? 'azure-monitor' : 'none'
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

// What the app and the job print, kept 30 days. The daily cap is the one bound on this meter, and
// not a hard one: the workspace stops taking lines some time after the cap is reached, and what
// got through by then is billed. No shared key: nothing here sends or reads with one.
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (keepLogs) {
  name: 'azurebank-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: json(logDailyCapGb)
    }
    features: {
      disableLocalAuth: true
    }
  }
}

// Console and system logs. The ingress (HTTP) category is left out: it would record every path
// with its query string, and every caller's address.
resource environmentLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (keepLogs) {
  scope: environment
  name: 'to-azurebank-logs'
  properties: {
    workspaceId: logs.id
    logs: [
      { category: 'ContainerAppConsoleLogs', enabled: true }
      { category: 'ContainerAppSystemLogs', enabled: true }
    ]
  }
}

// Microsoft Entra sign-ins only. The template gives the server no SQL administrator login and no
// password (sent by hand, such a request got an administrator name the service made up, and no
// password was sent for it), and Entra-only refuses every SQL sign-in: the server's sign-in,
// which every Azure customer can reach (the firewall rule below), has no password to guess. The
// administrators block is what creates a server that way. The reference says this API version
// reads it at creation only; sent by hand a second time, the same request was accepted and the
// server read back as before (README.md, "Measured on Azure").
resource sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: 'azurebank-${uniqueString(resourceGroup().id)}'
  location: location
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: entraAdminLogin
      sid: entraAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
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

// What the app and the migrate job sign in to the database as. Neither has a role on any Azure
// resource: each is only a user inside the database, created by infra/sql-principals.ps1.
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'azurebank-app'
  location: location
}

resource migrateIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'azurebank-migrate'
  location: location
}

// Each string names the identity to ask a token for and holds no credential. The two are secrets
// all the same, referenced by the api container and by the job only. An identity can be used by
// every container of the app, the bff included, and the bff faces the internet: it is handed
// neither the server's name nor the client ID. That is not a lock, because neither is a secret;
// it keeps both out of that container, and out of every read of the app and of an execution.
// The connection limits (connect timeout, retries, pool size) are the hosts' own defaults (ADR-0058): none is set here.
var appConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=AzureBank;Authentication=Active Directory Managed Identity;User ID=${appIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False'
var migrationConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=AzureBank;Authentication=Active Directory Managed Identity;User ID=${migrateIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False'

// Refuses deployApp=true without the image tag, the alerts' address or one of the seven secrets:
// the checks are the parameters of app-inputs.bicep, and the app, the job and the action group
// wait for it. The app's name stays a plain value, which a what-if can work out (README.md,
// "Measured on Azure").
module appInputs 'app-inputs.bicep' = if (deployApp) {
  name: 'azurebank-app-inputs'
  params: {
    imageTag: imageTag
    alertEmail: alertEmail
    jwtSecret: jwtSecret
    idempotencyHashKey: idempotencyHashKey
    stepUpBindingKey: stepUpBindingKey
    serviceCredentialBffKey: serviceCredentialBffKey
    auditChainKey: auditChainKey
    auditAnchorKey: auditAnchorKey
    securityPinPepper: securityPinPepper
  }
}

resource app 'Microsoft.App/containerApps@2025-01-01' = if (deployApp) {
  name: 'azurebank'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${appIdentity.id}': {}
    }
  }
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
            // Serilog's one line per request, written at Information for every page, file and call,
            // is not kept: it is most of what a day writes and the cheapest way to fill the log's
            // daily cap. Every warning stays, and so does the request line of a 5xx (an Error).
            { name: 'Serilog__MinimumLevel__Override__Serilog', value: 'Warning' }
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
  dependsOn: [
    appInputs
  ]
}

resource migrate 'Microsoft.App/jobs@2025-01-01' = if (deployApp) {
  name: 'azurebank-migrate'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${migrateIdentity.id}': {}
    }
  }
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
  dependsOn: [
    appInputs
  ]
}

// What a deployment needs: read and write the app and the job, start the job, read its executions,
// read the app's revisions and replicas (why a revision is not ready). It cannot list secrets,
// delete, stop, or touch the environment or the resource group. It CAN write the whole app and the
// whole job, so it can run any image with the secrets in its environment and with the database
// identity attached to it: see the README.
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
    displayName: 'AzureBank: one small replica, manual jobs, the pool job scheduled'
    policyDefinitionId: shape!.outputs.definitionId
    enforcementMode: 'Default'
    parameters: {
      allowedJobTriggers: {
        value: allowedJobTriggers
      }
      scheduledJobs: {
        value: scheduledJobs
      }
    }
  }
}

// Notify only: nothing here stops the app. One e-mail receiver; three rules on the app, one per
// meter that traffic can move (requests, bytes out, replica time). A fourth, on the log workspace,
// is built only when logVolumeAlert is true: see the comment on it below.
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
  dependsOn: [
    appInputs
  ]
}

var alerts = [
  // 66,667 requests is one day of the free monthly grant (2 million / 30), here in one hour.
  { name: 'azurebank-requests', onLogs: false, metric: 'Requests', aggregation: 'Total', threshold: 66667, window: 'PT1H', every: 'PT15M', text: 'More than 66,667 requests in one hour.' }
  // 3.3 GiB is one day of the free 100 GB a month.
  { name: 'azurebank-bytes-out', onLogs: false, metric: 'TxBytes', aggregation: 'Total', threshold: 3543348019, window: 'P1D', every: 'PT1H', text: 'More than 3.3 GiB sent in one day.' }
  // 0.093 is 66.7 free replica-hours a month, as a daily average of the replica count. It assumes
  // the metric reports 0 while the app is scaled to zero, which nobody has seen: if it reports
  // nothing then, the average is 1 on any day of use and this rule fires every such day.
  { name: 'azurebank-replica-time', onLogs: false, metric: 'Replicas', aggregation: 'Average', threshold: json('0.093'), window: 'P1D', every: 'PT1H', text: 'The replica ran more than 2.2 hours in one day.' }
  // 50,000 records ingested in one hour. Microsoft's page on the workspace's metrics (2026-07-31)
  // calls 'Ingestion Volume' the number of records ingested into a workspace or a table, with Count
  // as its default aggregation. A record is a row, and in the trial a line printed was one row; but
  // whether one measurement of the metric is one record is said nowhere. Measured on 2026-10-03,
  // at the runbook's step 20 (README.md, "Measured on Azure"): for an hour in which the workspace
  // ingested 446 rows, 395 of them in its middle forty minutes, the metric had no time series at
  // all, while another metric of the same workspace, 'Query Count', had one. A rule on it cannot
  // count lines there, so this one is built only when logVolumeAlert is true, which is for
  // whoever measures again. Until that day this comment said "Not measured yet" and the rule was
  // built by default; the first deployment created it, and it was deleted after the measurement.
  // The threshold is what a rule that counts lines would use: 50,000 lines at the 438 bytes that
  // a line of a job was billed are 22 MB of the 50 MB daily cap; at the 768 computed for one of
  // the app's warnings, 38 MB. Without it nothing warns of the log's volume, and nothing warns
  // that the cap itself was reached.
  { name: 'azurebank-log-volume', onLogs: true, metric: 'Ingestion Volume', aggregation: 'Count', threshold: 50000, window: 'PT1H', every: 'PT15M', text: 'More than 50,000 log lines in one hour.' }
]

resource notify 'Microsoft.Insights/metricAlerts@2018-03-01' = [for alert in alerts: if (deployApp && (!alert.onLogs || (keepLogs && logVolumeAlert))) {
  name: alert.name
  location: 'global'
  properties: {
    description: alert.text
    severity: 2
    enabled: true
    scopes: [
      alert.onLogs ? logs.id : app.id
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
          metricNamespace: alert.onLogs ? 'Microsoft.OperationalInsights/workspaces' : 'Microsoft.App/containerApps'
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
// The client ID names an identity in a connection string and in its database user.
// No script reads these: the users script asks `az identity show` for the IDs it needs.
output appIdentityClientId string = appIdentity.properties.clientId
output migrateIdentityClientId string = migrateIdentity.properties.clientId
// The ID a log query is sent to. Empty when no logs are kept.
output logWorkspaceCustomerId string = keepLogs ? logs!.properties.customerId : ''
output appUrl string = deployApp ? 'https://${app!.properties.configuration!.ingress!.fqdn}' : ''
