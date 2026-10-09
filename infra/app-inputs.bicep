// What a run with deployApp=true must be given, checked by Azure on the values main.bicep hands
// over: the full commit SHA of the images, the address the alerts write to, and the eight
// application secrets (infra/secrets.ps1 -DeployApp writes them all). main.bicep deploys this only
// when deployApp is true, and the app, its jobs and the action group wait for it. It creates
// nothing.
//
// The check is not in the app's name, made to fail() when a value is missing: a what-if works
// out no expression that reads a secure parameter, so with the check there it could name neither
// the app nor the role assignment on it, and listed both as Unsupported (README.md, "Measured on
// Azure"). Here the name is a plain value and the check is in these parameters, which no
// resource's ID reads.
//
// The parameters are read by that check and by nothing else in this file.
#disable-diagnostics no-unused-params

@minLength(40)
@maxLength(40)
param imageTag string

@minLength(1)
param alertEmail string

@secure()
@minLength(1)
param jwtSecret string

@secure()
@minLength(1)
param idempotencyHashKey string

@secure()
@minLength(1)
param stepUpBindingKey string

@secure()
@minLength(1)
param serviceCredentialBffKey string

@secure()
@minLength(1)
param auditChainKey string

@secure()
@minLength(1)
param auditAnchorKey string

@secure()
@minLength(1)
param securityPinPepper string

// The demo's client key. With the demo on the API does not start on fewer than 32 characters
// of it, so a shorter one is expected to be refused here, before a revision is made that could
// not start. Expected, not seen: the tests read the length from the compiled check
// (infra/test_scripts.py), and no engine has been seen refusing a shorter key.
@secure()
@minLength(32)
param demoClientKeySecret string
