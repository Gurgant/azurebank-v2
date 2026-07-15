# Azure VNET: BFF-to-API Secure Communication

## Document Purpose
This guide explains how to deploy the AzureBank solution to Azure with:
1. **BFF publicly accessible** (frontend talks to BFF)
2. **API completely private** (only BFF can reach API)
3. **VNET isolation** for maximum security

---

# PART 1: ARCHITECTURE OVERVIEW

## Target State Architecture

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                               INTERNET                                           │
│                                                                                  │
│    ┌──────────────────┐          ┌──────────────────┐                           │
│    │     Browser      │          │    Mobile App    │                           │
│    │   (SPA/React)    │          │                  │                           │
│    └────────┬─────────┘          └────────┬─────────┘                           │
│             │                              │                                     │
│             └──────────────┬───────────────┘                                    │
│                            │                                                     │
│                            │ HTTPS (443)                                        │
│                            ▼                                                     │
└────────────────────────────┼─────────────────────────────────────────────────────┘
                             │
┌────────────────────────────┼─────────────────────────────────────────────────────┐
│                            │         AZURE SUBSCRIPTION                          │
│   ┌────────────────────────┼────────────────────────────────────────────────┐   │
│   │                        │         VIRTUAL NETWORK (10.0.0.0/16)          │   │
│   │                        ▼                                                 │   │
│   │   ┌─────────────────────────────────────────────────────────────────┐   │   │
│   │   │                PUBLIC SUBNET (10.0.1.0/24)                       │   │   │
│   │   │                                                                  │   │   │
│   │   │   ┌─────────────────────────────────────────────────────────┐   │   │   │
│   │   │   │              APP SERVICE (BFF)                          │   │   │   │
│   │   │   │              azurebank-bff.azurewebsites.net            │   │   │   │
│   │   │   │                                                          │   │   │   │
│   │   │   │  ✅ Public Internet Access                              │   │   │   │
│   │   │   │  ✅ VNET Integration (outbound to private subnet)       │   │   │   │
│   │   │   │  ✅ HTTPS only                                          │   │   │   │
│   │   │   └──────────────────────────┬──────────────────────────────┘   │   │   │
│   │   │                              │                                   │   │   │
│   │   └──────────────────────────────┼───────────────────────────────────┘   │   │
│   │                                  │                                       │   │
│   │                                  │ Private Endpoint (10.0.2.10)         │   │
│   │                                  │ HTTPS (443)                          │   │   │
│   │                                  ▼                                       │   │
│   │   ┌─────────────────────────────────────────────────────────────────┐   │   │
│   │   │                PRIVATE SUBNET (10.0.2.0/24)                      │   │   │
│   │   │                                                                  │   │   │
│   │   │   ┌─────────────────────────────────────────────────────────┐   │   │   │
│   │   │   │              APP SERVICE (API)                          │   │   │   │
│   │   │   │              azurebank-api.azurewebsites.net            │   │   │   │
│   │   │   │                                                          │   │   │   │
│   │   │   │  ❌ NO Public Internet Access                           │   │   │   │
│   │   │   │  ✅ Private Endpoint only                               │   │   │   │
│   │   │   │  ✅ Only accessible from VNET                           │   │   │   │
│   │   │   └──────────────────────────┬──────────────────────────────┘   │   │   │
│   │   │                              │                                   │   │   │
│   │   │                              │ Private Endpoint (10.0.2.20)     │   │   │
│   │   │                              ▼                                   │   │   │
│   │   │   ┌─────────────────────────────────────────────────────────┐   │   │   │
│   │   │   │              AZURE SQL DATABASE                         │   │   │   │
│   │   │   │              azurebank-sql.database.windows.net         │   │   │   │
│   │   │   │                                                          │   │   │   │
│   │   │   │  ❌ NO Public Internet Access                           │   │   │   │
│   │   │   │  ✅ Private Endpoint only                               │   │   │   │
│   │   │   └─────────────────────────────────────────────────────────┘   │   │   │
│   │   │                                                                  │   │   │
│   │   │   ┌─────────────────────────────────────────────────────────┐   │   │   │
│   │   │   │              AZURE CACHE FOR REDIS (Garnet alternative) │   │   │   │
│   │   │   │              azurebank-cache.redis.cache.windows.net    │   │   │   │
│   │   │   │                                                          │   │   │   │
│   │   │   │  ❌ NO Public Internet Access                           │   │   │   │
│   │   │   │  ✅ Private Endpoint only                               │   │   │   │
│   │   │   └─────────────────────────────────────────────────────────┘   │   │   │
│   │   │                                                                  │   │   │
│   │   └──────────────────────────────────────────────────────────────────┘   │   │
│   │                                                                          │   │
│   └──────────────────────────────────────────────────────────────────────────┘   │
│                                                                                  │
└──────────────────────────────────────────────────────────────────────────────────┘
```

## Security Flow

```
1. Browser → BFF (Public)
   - User accesses https://azurebank-bff.azurewebsites.net
   - BFF is publicly accessible
   - Returns session cookie (HttpOnly, Secure)

2. BFF → API (Private, VNET only)
   - BFF makes request to API via Private Endpoint
   - Uses internal IP: https://10.0.2.10 or Private DNS
   - API is NOT accessible from internet
   - Only VNET traffic allowed

3. API → SQL (Private, VNET only)
   - API connects to SQL via Private Endpoint
   - SQL has no public endpoint
   - Connection string uses private DNS

4. BFF → Redis (Private, VNET only)
   - BFF stores sessions in Redis
   - Redis has no public endpoint
   - Connection via Private Endpoint
```

---

# PART 2: PREREQUISITE CONFIGURATION

## 2.1 Code Changes Required Before Deployment

### Step 2.1.1: Update BFF Configuration for Azure
**File**: `src/AzureBank.Bff/appsettings.Production.json`

```json
{
  "BackendApi": {
    "BaseUrl": "https://azurebank-api.privatelink.azurewebsites.net"
  },
  "Garnet": {
    "ConnectionString": "${REDIS_CONNECTION_STRING}"
  },
  "Session": {
    "CookieName": ".AzureBank.Session",
    "InactivityTimeoutMinutes": 30,
    "AbsoluteTimeoutMinutes": 60
  },
  "ReverseProxy": {
    "Clusters": {
      "backend-api": {
        "Destinations": {
          "destination1": {
            "Address": "https://azurebank-api.privatelink.azurewebsites.net"
          }
        }
      }
    }
  }
}
```

### Step 2.1.2: Update API for Private Endpoint Access
**File**: `src/AzureBank.Api/appsettings.Production.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "${SQL_CONNECTION_STRING}"
  },
  "Jwt": {
    "Secret": "${JWT_SECRET}",
    "Issuer": "AzureBank.Api",
    "Audience": "AzureBank.Bff",
    "ExpirationMinutes": 15,
    "RefreshTokenExpirationDays": 7
  },
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://+:80"
      },
      "Https": {
        "Url": "https://+:443"
      }
    }
  }
}
```

### Step 2.1.3: Add Service-to-Service Authentication (Optional but Recommended)

For additional security, add a shared secret between BFF and API:

**File**: `src/AzureBank.Api/Middleware/ServiceAuthMiddleware.cs`

```csharp
public class ServiceAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _expectedApiKey;

    public ServiceAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _expectedApiKey = config["ServiceAuth:ApiKey"]
            ?? throw new InvalidOperationException("ServiceAuth:ApiKey not configured");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip for health checks
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        // Check for service API key (BFF→API calls)
        if (context.Request.Headers.TryGetValue("X-Service-Key", out var apiKey))
        {
            if (apiKey != _expectedApiKey)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Invalid service key");
                return;
            }
        }
        // If no service key, request must have valid JWT (direct API access during dev)
        // This is handled by [Authorize] attribute

        await _next(context);
    }
}
```

**File**: `src/AzureBank.Bff/Program.cs` (add service key to requests)

```csharp
builder.Services.AddHttpClient("BackendApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["BackendApi:BaseUrl"]!);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.DefaultRequestHeaders.Add("X-Service-Key",
        builder.Configuration["ServiceAuth:ApiKey"]);
});
```

---

# PART 3: AZURE INFRASTRUCTURE DEPLOYMENT

## Phase 3.1: Create Resource Group and VNET

### Step 3.1.1: Create Resource Group
```bash
# Variables
RESOURCE_GROUP="rg-azurebank-prod"
LOCATION="eastus"

# Create resource group
az group create \
  --name $RESOURCE_GROUP \
  --location $LOCATION
```

### Step 3.1.2: Create Virtual Network
```bash
VNET_NAME="vnet-azurebank"

az network vnet create \
  --resource-group $RESOURCE_GROUP \
  --name $VNET_NAME \
  --address-prefix 10.0.0.0/16 \
  --location $LOCATION
```

### Step 3.1.3: Create Subnets
```bash
# Public subnet for BFF (with service endpoints)
az network vnet subnet create \
  --resource-group $RESOURCE_GROUP \
  --vnet-name $VNET_NAME \
  --name subnet-public \
  --address-prefix 10.0.1.0/24 \
  --delegations Microsoft.Web/serverFarms

# Private subnet for API, SQL, Redis
az network vnet subnet create \
  --resource-group $RESOURCE_GROUP \
  --vnet-name $VNET_NAME \
  --name subnet-private \
  --address-prefix 10.0.2.0/24 \
  --disable-private-endpoint-network-policies true
```

## Phase 3.2: Create Azure SQL Database

### Step 3.2.1: Create SQL Server
```bash
SQL_SERVER="azurebank-sql-server"
SQL_ADMIN="azurebankadmin"
SQL_PASSWORD="<generate-strong-password>"

az sql server create \
  --resource-group $RESOURCE_GROUP \
  --name $SQL_SERVER \
  --admin-user $SQL_ADMIN \
  --admin-password $SQL_PASSWORD \
  --location $LOCATION \
  --enable-public-network false  # Disable public access!
```

### Step 3.2.2: Create Database
```bash
az sql db create \
  --resource-group $RESOURCE_GROUP \
  --server $SQL_SERVER \
  --name AzureBank \
  --service-objective S1 \
  --zone-redundant false
```

### Step 3.2.3: Create Private Endpoint for SQL
```bash
SQL_PE="pe-azurebank-sql"

az network private-endpoint create \
  --resource-group $RESOURCE_GROUP \
  --name $SQL_PE \
  --vnet-name $VNET_NAME \
  --subnet subnet-private \
  --private-connection-resource-id $(az sql server show \
    --resource-group $RESOURCE_GROUP \
    --name $SQL_SERVER \
    --query id -o tsv) \
  --group-id sqlServer \
  --connection-name "sql-connection"
```

### Step 3.2.4: Create Private DNS Zone for SQL
```bash
# Create DNS zone
az network private-dns zone create \
  --resource-group $RESOURCE_GROUP \
  --name privatelink.database.windows.net

# Link to VNET
az network private-dns zone link vnet create \
  --resource-group $RESOURCE_GROUP \
  --zone-name privatelink.database.windows.net \
  --name vnet-link \
  --virtual-network $VNET_NAME \
  --registration-enabled false

# Create DNS record
az network private-endpoint dns-zone-group create \
  --resource-group $RESOURCE_GROUP \
  --endpoint-name $SQL_PE \
  --name sql-dns-group \
  --private-dns-zone privatelink.database.windows.net \
  --zone-name privatelink.database.windows.net
```

## Phase 3.3: Create Azure Cache for Redis

### Step 3.3.1: Create Redis Cache
```bash
REDIS_NAME="azurebank-redis"

az redis create \
  --resource-group $RESOURCE_GROUP \
  --name $REDIS_NAME \
  --location $LOCATION \
  --sku Standard \
  --vm-size C1 \
  --enable-non-ssl-port false \
  --minimum-tls-version 1.2 \
  --public-network-access Disabled  # Private only!
```

### Step 3.3.2: Create Private Endpoint for Redis
```bash
REDIS_PE="pe-azurebank-redis"

az network private-endpoint create \
  --resource-group $RESOURCE_GROUP \
  --name $REDIS_PE \
  --vnet-name $VNET_NAME \
  --subnet subnet-private \
  --private-connection-resource-id $(az redis show \
    --resource-group $RESOURCE_GROUP \
    --name $REDIS_NAME \
    --query id -o tsv) \
  --group-id redisCache \
  --connection-name "redis-connection"
```

### Step 3.3.3: Create Private DNS Zone for Redis
```bash
az network private-dns zone create \
  --resource-group $RESOURCE_GROUP \
  --name privatelink.redis.cache.windows.net

az network private-dns zone link vnet create \
  --resource-group $RESOURCE_GROUP \
  --zone-name privatelink.redis.cache.windows.net \
  --name redis-vnet-link \
  --virtual-network $VNET_NAME \
  --registration-enabled false

az network private-endpoint dns-zone-group create \
  --resource-group $RESOURCE_GROUP \
  --endpoint-name $REDIS_PE \
  --name redis-dns-group \
  --private-dns-zone privatelink.redis.cache.windows.net \
  --zone-name privatelink.redis.cache.windows.net
```

## Phase 3.4: Create App Service for API (Private)

### Step 3.4.1: Create App Service Plan
```bash
ASP_API="asp-azurebank-api"

az appservice plan create \
  --resource-group $RESOURCE_GROUP \
  --name $ASP_API \
  --location $LOCATION \
  --sku P1V3 \
  --is-linux
```

### Step 3.4.2: Create API App Service
```bash
API_APP="azurebank-api"

az webapp create \
  --resource-group $RESOURCE_GROUP \
  --plan $ASP_API \
  --name $API_APP \
  --runtime "DOTNETCORE:8.0"
```

### Step 3.4.3: Disable Public Access to API
```bash
# This is the KEY step - makes API private!
az webapp update \
  --resource-group $RESOURCE_GROUP \
  --name $API_APP \
  --set publicNetworkAccess=Disabled
```

### Step 3.4.4: Create Private Endpoint for API
```bash
API_PE="pe-azurebank-api"

az network private-endpoint create \
  --resource-group $RESOURCE_GROUP \
  --name $API_PE \
  --vnet-name $VNET_NAME \
  --subnet subnet-private \
  --private-connection-resource-id $(az webapp show \
    --resource-group $RESOURCE_GROUP \
    --name $API_APP \
    --query id -o tsv) \
  --group-id sites \
  --connection-name "api-connection"
```

### Step 3.4.5: Create Private DNS Zone for App Service
```bash
az network private-dns zone create \
  --resource-group $RESOURCE_GROUP \
  --name privatelink.azurewebsites.net

az network private-dns zone link vnet create \
  --resource-group $RESOURCE_GROUP \
  --zone-name privatelink.azurewebsites.net \
  --name webapp-vnet-link \
  --virtual-network $VNET_NAME \
  --registration-enabled false

az network private-endpoint dns-zone-group create \
  --resource-group $RESOURCE_GROUP \
  --endpoint-name $API_PE \
  --name api-dns-group \
  --private-dns-zone privatelink.azurewebsites.net \
  --zone-name privatelink.azurewebsites.net
```

### Step 3.4.6: Configure API App Settings
```bash
# Get connection strings
SQL_CONN="Server=tcp:$SQL_SERVER.database.windows.net,1433;Database=AzureBank;User ID=$SQL_ADMIN;Password=$SQL_PASSWORD;Encrypt=True;TrustServerCertificate=False;"

az webapp config appsettings set \
  --resource-group $RESOURCE_GROUP \
  --name $API_APP \
  --settings \
    "ConnectionStrings__DefaultConnection=$SQL_CONN" \
    "Jwt__Secret=<your-production-jwt-secret-min-32-chars>" \
    "Jwt__Issuer=AzureBank.Api" \
    "Jwt__Audience=AzureBank.Bff" \
    "ASPNETCORE_ENVIRONMENT=Production"
```

## Phase 3.5: Create App Service for BFF (Public)

### Step 3.5.1: Create App Service Plan for BFF
```bash
ASP_BFF="asp-azurebank-bff"

az appservice plan create \
  --resource-group $RESOURCE_GROUP \
  --name $ASP_BFF \
  --location $LOCATION \
  --sku P1V3 \
  --is-linux
```

### Step 3.5.2: Create BFF App Service
```bash
BFF_APP="azurebank-bff"

az webapp create \
  --resource-group $RESOURCE_GROUP \
  --plan $ASP_BFF \
  --name $BFF_APP \
  --runtime "DOTNETCORE:8.0"
```

### Step 3.5.3: Enable VNET Integration for BFF (Outbound)
```bash
# This allows BFF to reach private subnet resources
az webapp vnet-integration add \
  --resource-group $RESOURCE_GROUP \
  --name $BFF_APP \
  --vnet $VNET_NAME \
  --subnet subnet-public
```

### Step 3.5.4: Configure BFF to Route All Traffic Through VNET
```bash
az webapp config appsettings set \
  --resource-group $RESOURCE_GROUP \
  --name $BFF_APP \
  --settings \
    "WEBSITE_VNET_ROUTE_ALL=1" \
    "WEBSITE_DNS_SERVER=168.63.129.16"
```

### Step 3.5.5: Configure BFF App Settings
```bash
# Get Redis connection string
REDIS_KEY=$(az redis list-keys \
  --resource-group $RESOURCE_GROUP \
  --name $REDIS_NAME \
  --query primaryKey -o tsv)

REDIS_CONN="$REDIS_NAME.redis.cache.windows.net:6380,password=$REDIS_KEY,ssl=True,abortConnect=False"

az webapp config appsettings set \
  --resource-group $RESOURCE_GROUP \
  --name $BFF_APP \
  --settings \
    "BackendApi__BaseUrl=https://azurebank-api.privatelink.azurewebsites.net" \
    "Garnet__ConnectionString=$REDIS_CONN" \
    "Jwt__Secret=<same-jwt-secret-as-api>" \
    "ASPNETCORE_ENVIRONMENT=Production"
```

## Phase 3.6: Deploy Applications

### Step 3.6.1: Build and Publish API
```bash
cd src/AzureBank.Api
dotnet publish -c Release -o ./publish

# Deploy using Azure CLI or CI/CD
az webapp deploy \
  --resource-group $RESOURCE_GROUP \
  --name $API_APP \
  --src-path ./publish \
  --type zip
```

### Step 3.6.2: Build and Publish BFF
```bash
cd src/AzureBank.Bff
dotnet publish -c Release -o ./publish

az webapp deploy \
  --resource-group $RESOURCE_GROUP \
  --name $BFF_APP \
  --src-path ./publish \
  --type zip
```

---

# PART 4: VERIFICATION

## 4.1 Verify API is Private

### Test 4.1.1: API Should NOT Be Accessible from Internet
```bash
# This should FAIL (timeout or connection refused)
curl https://azurebank-api.azurewebsites.net/health

# Expected: Connection timeout or "Site not found"
```

### Test 4.1.2: API Should Be Accessible from BFF
```bash
# SSH into BFF (or use Kudu console)
# From within BFF, this should work:
curl https://azurebank-api.privatelink.azurewebsites.net/health

# Expected: {"status": "Healthy"}
```

## 4.2 Verify BFF is Public

### Test 4.2.1: BFF Should Be Accessible from Internet
```bash
curl https://azurebank-bff.azurewebsites.net/health/live

# Expected: {"status": "Healthy"}
```

### Test 4.2.2: End-to-End Flow Should Work
```bash
# Login via BFF
curl -X POST https://azurebank-bff.azurewebsites.net/bff/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"test@example.com","password":"Test123!"}' \
  -c cookies.txt

# Expected: 200 OK with user info (no JWT visible!)

# Access API via BFF
curl https://azurebank-bff.azurewebsites.net/api/accounts \
  -b cookies.txt

# Expected: 200 OK with accounts data
```

## 4.3 Verify Private Endpoints

### Check DNS Resolution from BFF
```bash
# From BFF Kudu console:
nslookup azurebank-api.privatelink.azurewebsites.net

# Expected: Resolves to private IP (10.0.2.x)

nslookup azurebank-sql-server.database.windows.net

# Expected: Resolves to private IP (10.0.2.x)
```

---

# PART 5: NETWORK SECURITY GROUPS (Optional Additional Security)

## 5.1 Create NSG for Private Subnet

```bash
NSG_PRIVATE="nsg-private-subnet"

az network nsg create \
  --resource-group $RESOURCE_GROUP \
  --name $NSG_PRIVATE

# Allow inbound from public subnet (BFF)
az network nsg rule create \
  --resource-group $RESOURCE_GROUP \
  --nsg-name $NSG_PRIVATE \
  --name AllowBffSubnet \
  --priority 100 \
  --direction Inbound \
  --source-address-prefixes 10.0.1.0/24 \
  --destination-port-ranges 443 \
  --protocol Tcp \
  --access Allow

# Deny all other inbound
az network nsg rule create \
  --resource-group $RESOURCE_GROUP \
  --nsg-name $NSG_PRIVATE \
  --name DenyAllInbound \
  --priority 1000 \
  --direction Inbound \
  --source-address-prefixes '*' \
  --destination-port-ranges '*' \
  --protocol '*' \
  --access Deny

# Associate NSG with private subnet
az network vnet subnet update \
  --resource-group $RESOURCE_GROUP \
  --vnet-name $VNET_NAME \
  --name subnet-private \
  --network-security-group $NSG_PRIVATE
```

---

# PART 6: INFRASTRUCTURE AS CODE (Bicep)

## Complete Bicep Template

**File**: `infra/main.bicep`

```bicep
@description('Location for all resources')
param location string = resourceGroup().location

@description('Environment name')
param environment string = 'prod'

@description('SQL Admin Password')
@secure()
param sqlAdminPassword string

@description('JWT Secret')
@secure()
param jwtSecret string

var prefix = 'azurebank-${environment}'
var vnetName = 'vnet-${prefix}'
var sqlServerName = 'sql-${prefix}'
var redisName = 'redis-${prefix}'
var apiAppName = 'api-${prefix}'
var bffAppName = 'bff-${prefix}'

// Virtual Network
resource vnet 'Microsoft.Network/virtualNetworks@2023-05-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: ['10.0.0.0/16']
    }
    subnets: [
      {
        name: 'subnet-public'
        properties: {
          addressPrefix: '10.0.1.0/24'
          delegations: [
            {
              name: 'delegation'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'subnet-private'
        properties: {
          addressPrefix: '10.0.2.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

// SQL Server (Private)
resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: 'azurebankadmin'
    administratorLoginPassword: sqlAdminPassword
    publicNetworkAccess: 'Disabled'
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: 'AzureBank'
  location: location
  sku: {
    name: 'S1'
    tier: 'Standard'
  }
}

// Redis Cache (Private)
resource redis 'Microsoft.Cache/redis@2023-08-01' = {
  name: redisName
  location: location
  properties: {
    sku: {
      name: 'Standard'
      family: 'C'
      capacity: 1
    }
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
  }
}

// App Service Plan for API
resource aspApi 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: 'asp-${apiAppName}'
  location: location
  kind: 'linux'
  sku: {
    name: 'P1v3'
    tier: 'PremiumV3'
  }
  properties: {
    reserved: true
  }
}

// API App Service (Private)
resource apiApp 'Microsoft.Web/sites@2023-01-01' = {
  name: apiAppName
  location: location
  properties: {
    serverFarmId: aspApi.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
    }
    publicNetworkAccess: 'Disabled'
  }
}

// App Service Plan for BFF
resource aspBff 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: 'asp-${bffAppName}'
  location: location
  kind: 'linux'
  sku: {
    name: 'P1v3'
    tier: 'PremiumV3'
  }
  properties: {
    reserved: true
  }
}

// BFF App Service (Public with VNET Integration)
resource bffApp 'Microsoft.Web/sites@2023-01-01' = {
  name: bffAppName
  location: location
  properties: {
    serverFarmId: aspBff.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
    }
    virtualNetworkSubnetId: vnet.properties.subnets[0].id
  }
}

// Private Endpoints
resource sqlPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = {
  name: 'pe-${sqlServerName}'
  location: location
  properties: {
    subnet: {
      id: vnet.properties.subnets[1].id
    }
    privateLinkServiceConnections: [
      {
        name: 'sql-connection'
        properties: {
          privateLinkServiceId: sqlServer.id
          groupIds: ['sqlServer']
        }
      }
    ]
  }
}

resource apiPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = {
  name: 'pe-${apiAppName}'
  location: location
  properties: {
    subnet: {
      id: vnet.properties.subnets[1].id
    }
    privateLinkServiceConnections: [
      {
        name: 'api-connection'
        properties: {
          privateLinkServiceId: apiApp.id
          groupIds: ['sites']
        }
      }
    ]
  }
}

resource redisPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = {
  name: 'pe-${redisName}'
  location: location
  properties: {
    subnet: {
      id: vnet.properties.subnets[1].id
    }
    privateLinkServiceConnections: [
      {
        name: 'redis-connection'
        properties: {
          privateLinkServiceId: redis.id
          groupIds: ['redisCache']
        }
      }
    ]
  }
}

// Outputs
output bffUrl string = 'https://${bffApp.properties.defaultHostName}'
output apiPrivateUrl string = 'https://${apiAppName}.privatelink.azurewebsites.net'
```

---

# PART 7: SUMMARY

## What We Achieved

| Component | Public Access | Private Access | Security |
|-----------|---------------|----------------|----------|
| **BFF** | ✅ Yes | ✅ VNET Integration | Entry point for all traffic |
| **API** | ❌ No | ✅ Private Endpoint | Only accessible from BFF |
| **SQL** | ❌ No | ✅ Private Endpoint | Only accessible from API |
| **Redis** | ❌ No | ✅ Private Endpoint | Only accessible from BFF |

## Security Benefits

1. **Defense in Depth**: Multiple layers of security
2. **Reduced Attack Surface**: API has no public IP
3. **Network Isolation**: VNET provides logical separation
4. **Private DNS**: Internal resolution only
5. **NSG Rules**: Fine-grained traffic control

## Development vs Production

| Aspect | Development | Production |
|--------|-------------|------------|
| API Access | Direct (public) | BFF only (private) |
| Session Store | InMemory | Redis (private) |
| Database | Local SQL | Azure SQL (private) |
| JWT Handling | Browser has token | BFF holds token |

---

**End of Azure VNET Guide**
