using './main.bicep'

param location = 'italynorth'
// Placeholder SHA: replace with the commit whose three images have been published.
param imageTag = '0000000000000000000000000000000000000000'
param deployApp = false
param entraAdminObjectId = 'REPLACE_WITH_ENTRA_ADMIN_OBJECT_ID'
param entraAdminLogin = 'REPLACE_WITH_ENTRA_ADMIN_DISPLAY_NAME'
param replicaTimeout = 600
param sqlAdminPassword = 'REPLACE_WITH_GENERATED_SQL_ADMIN_PASSWORD'
param appSqlPassword = 'REPLACE_WITH_GENERATED_APP_PASSWORD'
param migratorSqlPassword = 'REPLACE_WITH_GENERATED_MIGRATOR_PASSWORD'
param jwtSecret = 'REPLACE_WITH_GENERATED_JWT_SECRET'
param idempotencyHashKey = 'REPLACE_WITH_GENERATED_IDEMPOTENCY_KEY'
param stepUpBindingKey = 'REPLACE_WITH_GENERATED_STEPUP_KEY'
param serviceCredentialBffKey = 'REPLACE_WITH_GENERATED_SHARED_SERVICE_KEY'
param auditChainKey = 'REPLACE_WITH_GENERATED_AUDIT_CHAIN_KEY'
param auditAnchorKey = 'REPLACE_WITH_GENERATED_AUDIT_ANCHOR_KEY'
param securityPinPepper = 'REPLACE_WITH_GENERATED_PIN_PEPPER'
