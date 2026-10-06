targetScope = 'resourceGroup'

param location string = 'italynorth'
@description('Full commit SHA shared by the three public GHCR images. Needed only when deployApp is true.')
param imageTag string = ''
@description('False creates what needs no image: the environment, the log workspace, SQL, the three identities and the deployment role. True adds the app, the migrate job and the two role assignments; with demo true as well, the pool job and a third role assignment.')
param deployApp bool = false
@description('True turns the public demo on: the flag on both containers of the app, and the pool job with its role assignment. infra/secrets.ps1 writes it: what the deployed app does now, or true with -DemoOn. False removes nothing: a pool job that already exists is expected to stay and to keep its schedule.')
param demo bool = false
param entraAdminObjectId string
param entraAdminLogin string
@description('Seconds a migrate run may take. Kept under 15 minutes.')
@minValue(60)
@maxValue(840)
param replicaTimeout int = 600
@description('Seconds a pool run may take. Kept under 15 minutes, and under the four hours between two runs.')
@minValue(60)
@maxValue(840)
param poolTimeout int = 600

@description('Where the notify-only alerts send their e-mail. Needed only when deployApp is true; never committed.')
param alertEmail string = ''
@description('The e-mail address the Azure mobile app on the owner\'s phone was set up with. Given, the action group gains one receiver of that app, so that an alert is expected to reach the phone as a notification too. Empty, the default, adds none: the group is what it was. infra/secrets.ps1 writes it; never committed.')
param alertPushAccount string = ''
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

// The eight values below are needed only when deployApp is true; infra/secrets.ps1 supplies them.
// The eighth is the demo's: the app holds it whether the demo is on or off, and uses it only when on.
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
@secure()
param demoClientKeySecret string = ''

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

// What the app and its jobs print, kept 30 days. The daily cap is the one bound on this meter, and
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

// What the app and the migrate job sign in to the database as; the pool job signs in as the app
// does. Neither has a role on any Azure resource: each is only a user inside the database, created
// by infra/sql-principals.ps1.
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'azurebank-app'
  location: location
}

resource migrateIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'azurebank-migrate'
  location: location
}

// Each string names the identity to ask a token for and holds no credential. The two are secrets
// all the same, referenced by the api container and by the two jobs only: the app's string by the
// api container and by the pool job, the other by the migrate job. An identity can be used by
// every container of the app, the bff included, and the bff faces the internet: it is handed
// neither the server's name nor the client ID. That is not a lock, because neither is a secret;
// it keeps both out of that container, and out of every read of the app and of an execution.
// The connection limits (connect timeout, retries, pool size) are the hosts' own defaults (ADR-0058): none is set here.
var appConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=AzureBank;Authentication=Active Directory Managed Identity;User ID=${appIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False'
var migrationConnection = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=AzureBank;Authentication=Active Directory Managed Identity;User ID=${migrateIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False'

// Refuses deployApp=true without the image tag, the alerts' address or one of the eight secrets:
// the checks are the parameters of app-inputs.bicep, and the app, the two jobs and the action
// group wait for it. The app's name stays a plain value, which a what-if can work out (README.md,
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
    demoClientKeySecret: demoClientKeySecret
  }
}

// How many copies of the demo one address may claim in a day, as text for a container's setting.
// 1,000 is the range's maximum: until what the BFF sees as a visitor's address behind the ingress
// has been measured, the default of 10 could be ten copies a day for everybody (ADR-0063, decision 14).
var demoClaimsPerClient = '1000'

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
        { name: 'demo-client-key', value: demoClientKeySecret }
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
            // The demo's switch, the same text on both containers. With it on, this one closes
            // registration and marks the page, and the api hands out the copies (ADR-0063).
            { name: 'Demo__Enabled', value: demo ? 'true' : 'false' }
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
            { name: 'Demo__Enabled', value: demo ? 'true' : 'false' }
            // The key a visitor's address is hashed with before a claimed copy's row stores it.
            // With the demo off the api uses neither it nor the cap below; the cap's range is
            // checked when the api starts, on or off.
            { name: 'Demo__ClientKeySecret', secretRef: 'demo-client-key' }
            { name: 'Demo__Claim__MaxPerClientPerDay', value: demoClaimsPerClient }
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

// When the pool job is asked to run: every four hours, on the hour. Azure is expected to read the
// expression in UTC; no run on a schedule has been seen here. A variable and not a parameter: an
// override could make the interval shorter than the job's timeout, and two runs at once can build
// up to twice the pool's target (backend/tools/AzureBank.Seeder/README.md).
var poolSchedule = '0 */4 * * *'

// The job that keeps the demo's pool of copies: `recycle` tops the pool up and deletes the copies
// whose time is over (ADR-0062). Built only with the demo on: a job that carries the flag beside
// an app whose two containers do not is the state ADR-0063 warns of, where visitors register
// beside the pool and every run exits 13. It signs in to the database as the app does, never as
// the identity that changes the schema. A job has a secret list of its own, so its two secrets
// are written here from what the app's are written from: the same connection string and the same
// pepper. No retry: `recycle` ends a run that found something to say with an exit code from 10 to
// 15 (docs/runbooks/demo-pool.md), and with a retry Azure is expected to take such a run for a
// failed one and to try it again.
resource pool 'Microsoft.App/jobs@2025-01-01' = if (deployApp && demo) {
  name: 'azurebank-pool'
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
      triggerType: 'Schedule'
      replicaRetryLimit: 0
      replicaTimeout: poolTimeout
      scheduleTriggerConfig: {
        cronExpression: poolSchedule
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: [
        { name: 'app-connection', value: appConnection }
        { name: 'pin-pepper', value: securityPinPepper }
      ]
    }
    template: {
      containers: [
        {
          name: 'pool'
          image: 'ghcr.io/gurgant/azurebank-tools:${imageTag}'
          args: [
            'recycle'
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'ConnectionStrings__DefaultConnection', secretRef: 'app-connection' }
            { name: 'Security__PinPepper', secretRef: 'pin-pepper' }
            // A plain word and not the switch: this template writes the job only with the demo on,
            // and the pool's commands refuse to run with it off
            // (backend/tools/AzureBank.Seeder/README.md). A later run with demo false sends no job
            // and is not expected to delete this one: it would keep this word, and its schedule,
            // beside an app whose two flags are off, the state named over the job.
            { name: 'Demo__Enabled', value: 'true' }
            { name: 'Demo__Claim__MaxPerClientPerDay', value: demoClaimsPerClient }
          ]
        }
      ]
    }
  }
  // After the app, not beside it. Expected of Azure and not provoked: when the app's own update
  // fails, a job that waits for it is not created, so no scheduled job is left beside an app whose
  // flags are still off. It does not cover a new revision that never gets ready: for Azure the
  // app's update has then succeeded. What is done in that case, before the job's next run, is
  // the runbook's to say (infra/README.md).
  dependsOn: [
    appInputs
    app
  ]
}

// What a deployment needs: read and write the app and its jobs, start a job, read its executions,
// read the app's revisions and replicas (why a revision is not ready). It cannot list secrets,
// delete, stop, or touch the environment or the resource group. It CAN write the whole app and
// each whole job, so it can run any image with the secrets in its environment and with the
// database identity attached to it: see the README.
var deployRoleName = guid(resourceGroup().id, 'azurebank-deploy')
// The form every role assignment stores, whatever scope the definition was written at.
var deployRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', deployRoleName)

resource deployRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: deployRoleName
  properties: {
    roleName: 'AzureBank deploy ${uniqueString(resourceGroup().id)}'
    description: 'Move the images of the AzureBank app and of its jobs, and start a job.'
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

// The same role a third time, on the pool job: without it a deployment could neither read that job
// nor move its image with the commit. No new action. The pool job carries the app's database
// identity and two of the app's secrets, which the role can already run any image with through
// the app (above).
resource poolRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployApp && demo) {
  name: guid(pool.id, deployIdentity.id, deployRoleId)
  scope: pool
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

// Notify only: nothing here stops the app. One e-mail receiver, and beside it, when
// alertPushAccount is given, one receiver of the Azure mobile app, so that an alert is expected to
// reach the owner's phone as a notification too; three rules on the app, one per meter that
// traffic can move (requests, bytes out, replica time). A fourth, on the log workspace, is built
// only when logVolumeAlert is true: see the comment on it below. Until 2026-10-06 this comment
// said "One e-mail receiver" and the group could hold no other.
//
// The phone's receiver is merged in, not written as a list that may be empty: with no account
// the properties worked out are the three they were, and no fourth. Its name is its own, because
// a receiver's name must be unique in its group, and its emailAddress is, in the reference's
// words, "The email address registered for the Azure mobile app"
// (https://learn.microsoft.com/en-us/azure/templates/microsoft.insights/2023-01-01/actiongroups,
// read on 2026-10-06). Nothing of it has been sent to Azure. Not known: what Azure does with a
// push for an account that has no app, and what such a notification costs on this offer.
resource owner 'Microsoft.Insights/actionGroups@2023-01-01' = if (deployApp) {
  name: 'azurebank-owner'
  location: 'global'
  properties: union({
    groupShortName: 'azurebank'
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }, empty(alertPushAccount) ? {} : {
    azureAppPushReceivers: [
      {
        name: 'owner-phone'
        emailAddress: alertPushAccount
      }
    ]
  })
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
