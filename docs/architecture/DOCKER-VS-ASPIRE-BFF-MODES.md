# Docker Compose vs .NET Aspire: BFF Toggle Guide

## Document Purpose
Complete comparison of Docker Compose and .NET Aspire for running AzureBank with **two modes**:
1. **With BFF** (Production-like, secure)
2. **Without BFF** (Direct API, development/testing)

---

# PART 1: ARCHITECTURE OVERVIEW

## Two Operating Modes

### Mode 1: WITH BFF (Recommended for Production)

```
┌─────────────────────────────────────────────────────────────────┐
│                         BROWSER                                  │
│   React App (localhost:5173)                                    │
│   - Only knows about BFF                                        │
│   - Has session cookie (HttpOnly)                               │
│   - NO JWT visible                                              │
└─────────────────────────────┬───────────────────────────────────┘
                              │
                              │ fetch('/bff/auth/login')
                              │ fetch('/api/accounts')  ← proxied
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    BFF (localhost:5001)                         │
│   - Stores JWT server-side                                      │
│   - Issues session cookies                                      │
│   - Proxies /api/* to API                                       │
│   - Adds Authorization header                                   │
└─────────────────────────────┬───────────────────────────────────┘
                              │
                              │ Authorization: Bearer <jwt>
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    API (localhost:7215 or internal)             │
│   - Validates JWT                                               │
│   - Business logic                                              │
│   - Database access                                             │
└─────────────────────────────────────────────────────────────────┘
```

**Security**: ⭐⭐⭐⭐⭐ (JWT never in browser)

---

### Mode 2: WITHOUT BFF (Direct API Access)

```
┌─────────────────────────────────────────────────────────────────┐
│                         BROWSER                                  │
│   React App (localhost:5173)                                    │
│   - Talks directly to API                                       │
│   - Stores JWT in localStorage/memory                           │
│   - Handles token refresh                                       │
└─────────────────────────────┬───────────────────────────────────┘
                              │
                              │ fetch('http://localhost:7215/api/auth/login')
                              │ Authorization: Bearer <jwt>
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    API (localhost:7215)                         │
│   - Issues JWT directly to browser                              │
│   - Validates JWT                                               │
│   - Business logic                                              │
│   - Database access                                             │
└─────────────────────────────────────────────────────────────────┘
```

**Security**: ⭐⭐⭐ (JWT in browser - XSS risk)

---

# PART 2: DOCKER COMPOSE APPROACH

## 2.1 Key Concept: Docker Profiles

> "Service profiles allow you to group services and activate them only when needed. Services assigned to a profile are disabled by default unless explicitly enabled."
> — [Docker Compose Profiles Documentation](https://docs.docker.com/compose/how-tos/profiles/)

### How It Works

| Profile | BFF | API | DB | Redis | Frontend |
|---------|-----|-----|----|----|---------|
| `with-bff` | ✅ Runs | ✅ Internal | ✅ | ✅ | ✅ → BFF |
| `direct-api` | ❌ Disabled | ✅ Exposed | ✅ | ❌ | ✅ → API |
| (default) | ❌ | ✅ Exposed | ✅ | ❌ | ❌ |

---

## 2.2 Complete Docker Compose File

```yaml
# docker-compose.yml
# ══════════════════════════════════════════════════════════════════
#  AzureBank - Docker Compose with BFF Toggle
# ══════════════════════════════════════════════════════════════════
#
#  Usage:
#    WITH BFF:     docker-compose --profile with-bff up -d
#    WITHOUT BFF:  docker-compose --profile direct-api up -d
#    API ONLY:     docker-compose up -d  (just api + db)
#
# ══════════════════════════════════════════════════════════════════

version: '3.8'

services:
  # ════════════════════════════════════════════════════════════════
  # DATABASE - Always runs (no profile = always active)
  # ════════════════════════════════════════════════════════════════
  db:
    image: mcr.microsoft.com/mssql/server:2022-latest
    container_name: azurebank-db
    environment:
      - ACCEPT_EULA=Y
      - MSSQL_SA_PASSWORD=YourStr0ngP@ssword!
    volumes:
      - sqldata:/var/opt/mssql
    networks:
      - backend
    healthcheck:
      test: /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStr0ngP@ssword!" -C -Q "SELECT 1"
      interval: 10s
      timeout: 5s
      retries: 5
    # No 'expose' or 'ports' - only internal access

  # ════════════════════════════════════════════════════════════════
  # API - Behavior changes based on profile
  # ════════════════════════════════════════════════════════════════

  # API for BFF mode (internal only, no ports exposed)
  api-internal:
    build:
      context: .
      dockerfile: src/AzureBank.Api/Dockerfile
    container_name: azurebank-api
    profiles:
      - with-bff                    # 👈 Only runs with BFF profile
    expose:
      - "7215"                       # 👈 Internal only!
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:7215
      - ConnectionStrings__DefaultConnection=Server=db;Database=AzureBank;User Id=sa;Password=YourStr0ngP@ssword!;TrustServerCertificate=True
      - Jwt__Secret=your-super-secret-jwt-key-minimum-32-characters-long-for-production
      - Jwt__Issuer=AzureBank.Api
      - Jwt__Audience=AzureBank.Bff
    networks:
      - backend
    depends_on:
      db:
        condition: service_healthy

  # API for direct access mode (ports exposed to host)
  api-exposed:
    build:
      context: .
      dockerfile: src/AzureBank.Api/Dockerfile
    container_name: azurebank-api
    profiles:
      - direct-api                  # 👈 Only runs with direct-api profile
    ports:
      - "7215:7215"                  # 👈 Exposed to host!
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - ASPNETCORE_URLS=http://+:7215
      - ConnectionStrings__DefaultConnection=Server=db;Database=AzureBank;User Id=sa;Password=YourStr0ngP@ssword!;TrustServerCertificate=True
      - Jwt__Secret=your-super-secret-jwt-key-minimum-32-characters-long-for-production
      - Jwt__Issuer=AzureBank.Api
      - Jwt__Audience=AzureBank.Frontend  # Different audience for direct access
    networks:
      - backend
      - frontend                    # 👈 Also on frontend network for CORS
    depends_on:
      db:
        condition: service_healthy

  # API standalone (default, no profile needed)
  api:
    build:
      context: .
      dockerfile: src/AzureBank.Api/Dockerfile
    container_name: azurebank-api-standalone
    ports:
      - "7215:7215"
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - ASPNETCORE_URLS=http://+:7215
      - ConnectionStrings__DefaultConnection=Server=db;Database=AzureBank;User Id=sa;Password=YourStr0ngP@ssword!;TrustServerCertificate=True
      - Jwt__Secret=your-super-secret-jwt-key-minimum-32-characters-long-for-production
    networks:
      - backend
    depends_on:
      db:
        condition: service_healthy

  # ════════════════════════════════════════════════════════════════
  # BFF - Only runs with 'with-bff' profile
  # ════════════════════════════════════════════════════════════════
  bff:
    build:
      context: .
      dockerfile: src/AzureBank.Bff/Dockerfile
    container_name: azurebank-bff
    profiles:
      - with-bff                    # 👈 Only with BFF profile
    ports:
      - "5001:5001"                  # 👈 Public entry point
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:5001
      - BackendApi__BaseUrl=http://api-internal:7215  # 👈 Internal Docker DNS
      - Garnet__ConnectionString=redis:6379
      - Session__CookieName=.AzureBank.Session
      - Session__InactivityTimeoutMinutes=30
      - Session__AbsoluteTimeoutMinutes=60
    networks:
      - frontend
      - backend                     # 👈 Can reach API internally
    depends_on:
      - api-internal
      - redis

  # ════════════════════════════════════════════════════════════════
  # REDIS - Only with BFF (for session storage)
  # ════════════════════════════════════════════════════════════════
  redis:
    image: redis:7-alpine
    container_name: azurebank-redis
    profiles:
      - with-bff                    # 👈 Only needed with BFF
    expose:
      - "6379"
    volumes:
      - redisdata:/data
    networks:
      - backend
    command: redis-server --appendonly yes

  # ════════════════════════════════════════════════════════════════
  # FRONTEND (React) - Different configs per mode
  # ════════════════════════════════════════════════════════════════

  # Frontend configured for BFF
  frontend-bff:
    build:
      context: ../frontend          # Adjust path to your frontend
      dockerfile: Dockerfile
      args:
        - VITE_API_URL=             # Empty - uses relative URLs to BFF
        - VITE_USE_BFF=true
    container_name: azurebank-frontend
    profiles:
      - with-bff
    ports:
      - "5173:80"
    networks:
      - frontend
    depends_on:
      - bff

  # Frontend configured for direct API
  frontend-direct:
    build:
      context: ../frontend
      dockerfile: Dockerfile
      args:
        - VITE_API_URL=http://localhost:7215
        - VITE_USE_BFF=false
    container_name: azurebank-frontend
    profiles:
      - direct-api
    ports:
      - "5173:80"
    networks:
      - frontend
    depends_on:
      - api-exposed

# ══════════════════════════════════════════════════════════════════
# NETWORKS
# ══════════════════════════════════════════════════════════════════
networks:
  frontend:
    driver: bridge
  backend:
    driver: bridge
    internal: true                  # 👈 Isolated! No external access

# ══════════════════════════════════════════════════════════════════
# VOLUMES
# ══════════════════════════════════════════════════════════════════
volumes:
  sqldata:
  redisdata:
```

---

## 2.3 Usage Commands

### Mode 1: WITH BFF (Secure Mode)

```bash
# Start with BFF
docker-compose --profile with-bff up -d

# What starts:
# ✅ db (database)
# ✅ api-internal (not exposed to host)
# ✅ bff (port 5001 - entry point)
# ✅ redis (session storage)
# ✅ frontend-bff (configured for BFF)

# Test:
curl http://localhost:5001/health/live        # ✅ Works (BFF)
curl http://localhost:7215/health             # ❌ Fails (API not exposed)
curl http://localhost:5001/api/accounts       # ✅ Proxied through BFF

# Stop
docker-compose --profile with-bff down
```

### Mode 2: WITHOUT BFF (Direct API)

```bash
# Start direct API mode
docker-compose --profile direct-api up -d

# What starts:
# ✅ db (database)
# ✅ api-exposed (port 7215 - exposed)
# ❌ bff (not running)
# ❌ redis (not needed)
# ✅ frontend-direct (configured for direct API)

# Test:
curl http://localhost:7215/health             # ✅ Works (API exposed)
curl http://localhost:5001/health/live        # ❌ Fails (BFF not running)

# Stop
docker-compose --profile direct-api down
```

### Mode 3: API Only (Development)

```bash
# Start just API + database
docker-compose up -d

# What starts:
# ✅ db (database)
# ✅ api (port 7215 - standalone)
# ❌ Everything else

# Stop
docker-compose down
```

---

## 2.4 Docker Compose Visual Summary

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                     docker-compose --profile with-bff up                     │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   FRONTEND NETWORK                     BACKEND NETWORK (internal: true)     │
│   ┌─────────────────┐                 ┌────────────────────────────────┐    │
│   │                 │                 │                                │    │
│   │  frontend:5173 ─┼────► bff:5001 ─┼────► api-internal:7215         │    │
│   │  (browser)      │     (entry)    │      (no host access!)         │    │
│   │                 │        │        │           │                    │    │
│   └─────────────────┘        │        │           ▼                    │    │
│                              │        │        db:1433                 │    │
│                              │        │           │                    │    │
│                              ▼        │           │                    │    │
│                         redis:6379 ◄──┼───────────┘                    │    │
│                                       │                                │    │
│                                       └────────────────────────────────┘    │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│                   docker-compose --profile direct-api up                     │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   FRONTEND NETWORK                     BACKEND NETWORK                       │
│   ┌─────────────────┐                 ┌────────────────────────────────┐    │
│   │                 │                 │                                │    │
│   │  frontend:5173 ─┼────────────────►│ api-exposed:7215              │    │
│   │  (browser)      │   (direct!)     │  (host accessible!)           │    │
│   │                 │                 │           │                    │    │
│   └─────────────────┘                 │           ▼                    │    │
│                                       │        db:1433                 │    │
│          ❌ BFF not running           │                                │    │
│          ❌ Redis not running         │                                │    │
│                                       └────────────────────────────────┘    │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

# PART 3: .NET ASPIRE APPROACH

## 3.1 Key Concepts

> ".NET Aspire is a polyglot local dev-time orchestration tool chain for building, running, debugging, and deploying distributed applications."
> — [Microsoft .NET Aspire Documentation](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/aspire-overview)

### Key Differences from Docker

| Aspect | Docker Compose | .NET Aspire |
|--------|----------------|-------------|
| Configuration | YAML file | C# code |
| Conditional logic | Profiles (static) | if/else (dynamic) |
| Service discovery | Manual DNS | Automatic |
| Dashboard | None (add Portainer) | Built-in |
| Debugging | Attach to container | F5 in IDE |
| Hot reload | Rebuild container | Automatic |

---

## 3.2 Project Structure

```
AzureBank/
├── backend/
│   ├── src/
│   │   ├── AzureBank.Api/
│   │   ├── AzureBank.Bff/
│   │   ├── AzureBank.Shared/
│   │   └── AzureBank.Infrastructure/
│   │
│   └── AzureBank.AppHost/              # 👈 NEW: Aspire orchestrator
│       ├── AzureBank.AppHost.csproj
│       ├── Program.cs                   # 👈 Orchestration logic
│       └── appsettings.json
│
├── frontend/
│   └── azurebank-web/
│
└── AzureBank.sln
```

---

## 3.3 AppHost Project File

```xml
<!-- AzureBank.AppHost/AzureBank.AppHost.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <IsAspireHost>true</IsAspireHost>
  </PropertyGroup>

  <ItemGroup>
    <!-- Aspire SDK -->
    <PackageReference Include="Aspire.Hosting.AppHost" Version="9.0.0" />
    <PackageReference Include="Aspire.Hosting.Redis" Version="9.0.0" />
    <PackageReference Include="Aspire.Hosting.SqlServer" Version="9.0.0" />
  </ItemGroup>

  <ItemGroup>
    <!-- Project references -->
    <ProjectReference Include="..\src\AzureBank.Api\AzureBank.Api.csproj" />
    <ProjectReference Include="..\src\AzureBank.Bff\AzureBank.Bff.csproj" />
  </ItemGroup>

</Project>
```

---

## 3.4 AppHost Program.cs - The Magic

```csharp
// AzureBank.AppHost/Program.cs
// ══════════════════════════════════════════════════════════════════
//  .NET Aspire Orchestration with BFF Toggle
// ══════════════════════════════════════════════════════════════════
//
//  Usage:
//    WITH BFF:     dotnet run --launch-profile "WithBff"
//    WITHOUT BFF:  dotnet run --launch-profile "DirectApi"
//
//  Or set environment variable:
//    USE_BFF=true dotnet run
//    USE_BFF=false dotnet run
//
// ══════════════════════════════════════════════════════════════════

var builder = DistributedApplication.CreateBuilder(args);

// ════════════════════════════════════════════════════════════════
// CONFIGURATION: Determine which mode to run
// ════════════════════════════════════════════════════════════════

// Option 1: From environment variable
var useBff = builder.Configuration.GetValue<bool>("USE_BFF", defaultValue: true);

// Option 2: From launch profile (set ASPNETCORE_ENVIRONMENT or custom var)
// var useBff = builder.Environment.EnvironmentName == "WithBff";

Console.WriteLine($"🚀 Starting AzureBank in {(useBff ? "BFF" : "Direct API")} mode");

// ════════════════════════════════════════════════════════════════
// INFRASTRUCTURE: Always needed
// ════════════════════════════════════════════════════════════════

// SQL Server
var sqlServer = builder.AddSqlServer("sql")
    .WithDataVolume("azurebank-sqldata");

var database = sqlServer.AddDatabase("AzureBank");

// ════════════════════════════════════════════════════════════════
// MODE: WITH BFF
// ════════════════════════════════════════════════════════════════
if (useBff)
{
    Console.WriteLine("📦 Configuring BFF mode...");

    // Redis for session storage
    var redis = builder.AddRedis("redis")
        .WithDataVolume("azurebank-redisdata");

    // API - Internal only (no external endpoints!)
    var api = builder.AddProject<Projects.AzureBank_Api>("api")
        .WithReference(database)
        .WaitFor(database);
        // 👆 Note: NO .WithExternalHttpEndpoints() = internal only!

    // BFF - External (the entry point)
    var bff = builder.AddProject<Projects.AzureBank_Bff>("bff")
        .WithReference(api)           // Can discover API
        .WithReference(redis)         // For sessions
        .WithExternalHttpEndpoints()  // 👈 PUBLIC: Browser accesses this
        .WaitFor(api)
        .WaitFor(redis);

    // Frontend - Points to BFF
    builder.AddNpmApp("frontend", "../frontend/azurebank-web")
        .WithReference(bff)
        .WithEnvironment("VITE_API_URL", "")        // Empty = relative to BFF
        .WithEnvironment("VITE_USE_BFF", "true")
        .WithHttpEndpoint(port: 5173, env: "PORT")
        .WithExternalHttpEndpoints()
        .PublishAsDockerFile();
}
// ════════════════════════════════════════════════════════════════
// MODE: WITHOUT BFF (Direct API)
// ════════════════════════════════════════════════════════════════
else
{
    Console.WriteLine("📦 Configuring Direct API mode...");

    // API - External (browser talks directly)
    var api = builder.AddProject<Projects.AzureBank_Api>("api")
        .WithReference(database)
        .WithExternalHttpEndpoints()  // 👈 PUBLIC: Browser accesses this
        .WaitFor(database);

    // Frontend - Points directly to API
    builder.AddNpmApp("frontend", "../frontend/azurebank-web")
        .WithReference(api)
        .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
        .WithEnvironment("VITE_USE_BFF", "false")
        .WithHttpEndpoint(port: 5173, env: "PORT")
        .WithExternalHttpEndpoints()
        .PublishAsDockerFile();

    // ❌ No BFF
    // ❌ No Redis
}

// ════════════════════════════════════════════════════════════════
// BUILD AND RUN
// ════════════════════════════════════════════════════════════════
builder.Build().Run();
```

---

## 3.5 Launch Profiles (launchSettings.json)

```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "WithBff": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "https://localhost:17180;http://localhost:15180",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:21180",
        "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:22180",
        "USE_BFF": "true"
      }
    },
    "DirectApi": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "https://localhost:17181;http://localhost:15181",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:21181",
        "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:22181",
        "USE_BFF": "false"
      }
    }
  }
}
```

---

## 3.6 Usage Commands

### Mode 1: WITH BFF

```bash
# Using launch profile
dotnet run --project AzureBank.AppHost --launch-profile WithBff

# Or using environment variable
USE_BFF=true dotnet run --project AzureBank.AppHost

# What starts:
# ✅ SQL Server (container)
# ✅ Redis (container)
# ✅ API (project - internal)
# ✅ BFF (project - external)
# ✅ Frontend (npm app)

# Dashboard opens automatically at https://localhost:17180
# Shows all services, logs, traces, metrics
```

### Mode 2: WITHOUT BFF

```bash
# Using launch profile
dotnet run --project AzureBank.AppHost --launch-profile DirectApi

# Or using environment variable
USE_BFF=false dotnet run --project AzureBank.AppHost

# What starts:
# ✅ SQL Server (container)
# ✅ API (project - external)
# ✅ Frontend (npm app)
# ❌ BFF (not registered)
# ❌ Redis (not registered)

# Dashboard shows only running services
```

### Debugging in Visual Studio

1. Set `AzureBank.AppHost` as startup project
2. Select launch profile from dropdown:
   - `WithBff`
   - `DirectApi`
3. Press F5
4. All services start with debugger attached!
5. Set breakpoints in API, BFF, anywhere

---

## 3.7 .NET Aspire Visual Summary

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                     dotnet run --launch-profile WithBff                      │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   ┌─────────────────────────────────────────────────────────────────────┐   │
│   │                    ASPIRE DASHBOARD (localhost:17180)                │   │
│   │  ┌──────────┬──────────┬──────────┬──────────┬──────────┐          │   │
│   │  │ frontend │   bff    │   api    │  redis   │   sql    │          │   │
│   │  │  ✅ 5173  │  ✅ 5001  │  ✅ 7215  │  ✅ 6379  │  ✅ 1433  │          │   │
│   │  │ (public) │ (public) │(internal)│(internal)│(internal)│          │   │
│   │  └──────────┴──────────┴──────────┴──────────┴──────────┘          │   │
│   │                                                                      │   │
│   │  📊 Metrics | 📝 Logs | 🔍 Traces | 🏥 Health                        │   │
│   └─────────────────────────────────────────────────────────────────────┘   │
│                                                                              │
│   SERVICE DISCOVERY (automatic):                                            │
│   ┌───────────────────────────────────────────────────────────────────┐     │
│   │  frontend ──► bff ──► api ──► sql                                 │     │
│   │      │                 │                                          │     │
│   │      └────────────────► redis                                     │     │
│   │                                                                   │     │
│   │  URLs injected automatically via configuration!                   │     │
│   └───────────────────────────────────────────────────────────────────┘     │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│                   dotnet run --launch-profile DirectApi                      │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   ┌─────────────────────────────────────────────────────────────────────┐   │
│   │                    ASPIRE DASHBOARD (localhost:17181)                │   │
│   │  ┌──────────┬──────────┬──────────┐                                 │   │
│   │  │ frontend │   api    │   sql    │                                 │   │
│   │  │  ✅ 5173  │  ✅ 7215  │  ✅ 1433  │                                 │   │
│   │  │ (public) │ (public) │(internal)│                                 │   │
│   │  └──────────┴──────────┴──────────┘                                 │   │
│   │                                                                      │   │
│   │  ❌ BFF - Not registered                                            │   │
│   │  ❌ Redis - Not registered                                          │   │
│   └─────────────────────────────────────────────────────────────────────┘   │
│                                                                              │
│   SERVICE DISCOVERY:                                                         │
│   ┌───────────────────────────────────────────────────────────────────┐     │
│   │  frontend ──────────────────► api ──► sql                         │     │
│   │            (direct connection)                                    │     │
│   └───────────────────────────────────────────────────────────────────┘     │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

# PART 4: SIDE-BY-SIDE COMPARISON

## 4.1 Feature Comparison

| Feature | Docker Compose | .NET Aspire |
|---------|----------------|-------------|
| **Configuration** | YAML | C# code |
| **Mode switching** | `--profile` flag | Launch profile or env var |
| **Service discovery** | Manual (container names) | Automatic (injected) |
| **Dashboard** | ❌ (add Portainer) | ✅ Built-in (beautiful!) |
| **Debugging** | Attach to container | F5 in IDE |
| **Hot reload** | Rebuild container | ✅ Automatic |
| **Logs** | `docker-compose logs` | Dashboard (structured) |
| **Traces** | Manual (add Jaeger) | ✅ Built-in OpenTelemetry |
| **Health checks** | Manual | ✅ Automatic |
| **Works without .NET** | ✅ Yes | ❌ .NET only |
| **Production deployment** | ✅ Direct | Needs publish step |
| **Learning curve** | Low (YAML) | Medium (C# + concepts) |

## 4.2 Commands Comparison

| Action | Docker Compose | .NET Aspire |
|--------|----------------|-------------|
| **Start with BFF** | `docker-compose --profile with-bff up -d` | `dotnet run --launch-profile WithBff` |
| **Start without BFF** | `docker-compose --profile direct-api up -d` | `dotnet run --launch-profile DirectApi` |
| **Stop** | `docker-compose down` | Ctrl+C |
| **View logs** | `docker-compose logs -f` | Dashboard |
| **Rebuild** | `docker-compose up -d --build` | Auto (or `dotnet build`) |
| **Debug** | Attach to container | F5 |

## 4.3 Code Comparison

### Enabling BFF

**Docker Compose:**
```yaml
services:
  bff:
    profiles:
      - with-bff     # Service only runs with this profile
```

**Aspire:**
```csharp
if (useBff)
{
    builder.AddProject<Projects.AzureBank_Bff>("bff")
        .WithExternalHttpEndpoints();
}
```

### Making API Internal vs External

**Docker Compose:**
```yaml
# Internal (BFF mode)
api-internal:
  expose:
    - "7215"        # No host mapping

# External (Direct mode)
api-exposed:
  ports:
    - "7215:7215"   # Host can access
```

**Aspire:**
```csharp
// Internal (BFF mode)
var api = builder.AddProject<Projects.AzureBank_Api>("api");
// No .WithExternalHttpEndpoints() = internal

// External (Direct mode)
var api = builder.AddProject<Projects.AzureBank_Api>("api")
    .WithExternalHttpEndpoints();
```

### Service Discovery

**Docker Compose:**
```yaml
bff:
  environment:
    - BackendApi__BaseUrl=http://api-internal:7215  # Manual DNS
```

**Aspire:**
```csharp
var bff = builder.AddProject<Projects.AzureBank_Bff>("bff")
    .WithReference(api);  // Automatic! URL injected via config
```

---

# PART 5: FRONTEND CONFIGURATION

## 5.1 React/Vite Environment Setup

### Environment Files

```
frontend/
├── .env                    # Default (development)
├── .env.bff               # BFF mode
├── .env.direct            # Direct API mode
└── src/
    └── config/
        └── api.ts          # API configuration
```

### .env Files

```bash
# .env.bff
VITE_USE_BFF=true
VITE_API_URL=
VITE_BFF_URL=http://localhost:5001

# .env.direct
VITE_USE_BFF=false
VITE_API_URL=http://localhost:7215
VITE_BFF_URL=
```

### API Configuration (TypeScript)

```typescript
// src/config/api.ts

const useBff = import.meta.env.VITE_USE_BFF === 'true';

export const apiConfig = {
  useBff,

  // Base URL depends on mode
  baseUrl: useBff
    ? (import.meta.env.VITE_BFF_URL || '')  // Empty = same origin
    : import.meta.env.VITE_API_URL,

  // Endpoints differ between modes
  endpoints: {
    login: useBff ? '/bff/auth/login' : '/api/auth/login',
    logout: useBff ? '/bff/auth/logout' : '/api/auth/logout',
    me: useBff ? '/bff/auth/me' : '/api/auth/me',
    accounts: '/api/accounts',  // Same path, different base
  },

  // Credentials handling
  credentials: useBff ? 'include' : 'same-origin' as RequestCredentials,
};

// Fetch wrapper
export async function apiFetch(endpoint: string, options: RequestInit = {}) {
  const url = `${apiConfig.baseUrl}${endpoint}`;

  const response = await fetch(url, {
    ...options,
    credentials: apiConfig.credentials,
    headers: {
      'Content-Type': 'application/json',
      ...options.headers,
      // JWT header only in direct mode
      ...(apiConfig.useBff ? {} : {
        'Authorization': `Bearer ${getStoredToken()}`
      }),
    },
  });

  return response;
}
```

### Auth Hook

```typescript
// src/hooks/useAuth.ts

import { apiConfig, apiFetch } from '../config/api';

export function useAuth() {
  const login = async (email: string, password: string) => {
    const response = await apiFetch(apiConfig.endpoints.login, {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    });

    const data = await response.json();

    if (apiConfig.useBff) {
      // BFF mode: Cookie is set automatically (HttpOnly)
      // Just store user info in state
      return data.data.user;
    } else {
      // Direct mode: Store JWT in memory/localStorage
      localStorage.setItem('token', data.data.token);
      return data.data.user;
    }
  };

  const logout = async () => {
    await apiFetch(apiConfig.endpoints.logout, { method: 'POST' });

    if (!apiConfig.useBff) {
      localStorage.removeItem('token');
    }
  };

  return { login, logout };
}
```

---

# PART 6: RECOMMENDATION

## When to Use Each

| Scenario | Recommendation |
|----------|----------------|
| **Quick local testing** | Docker Compose |
| **Team with mixed tech** | Docker Compose |
| **Best .NET dev experience** | .NET Aspire |
| **Need debugging (F5)** | .NET Aspire |
| **Want built-in dashboard** | .NET Aspire |
| **CI/CD pipelines** | Docker Compose |
| **Production deployment** | Docker Compose |
| **Learning distributed apps** | .NET Aspire |

## My Recommendation for You

### Start With: **Docker Compose**

**Why:**
- ⭐ Simpler to understand
- ⭐ Works immediately
- ⭐ Industry standard
- ⭐ Easy to deploy anywhere
- ⭐ Profiles are straightforward

### Later Consider: **.NET Aspire**

**When you need:**
- Better debugging experience
- Built-in observability
- Automatic service discovery
- The beautiful dashboard

---

# PART 7: QUICK REFERENCE

## Docker Compose Commands

```bash
# WITH BFF (secure, production-like)
docker-compose --profile with-bff up -d
docker-compose --profile with-bff down

# WITHOUT BFF (direct API)
docker-compose --profile direct-api up -d
docker-compose --profile direct-api down

# API only
docker-compose up -d
docker-compose down

# View logs
docker-compose logs -f bff
docker-compose logs -f api
```

## .NET Aspire Commands

```bash
# WITH BFF
dotnet run --project AzureBank.AppHost --launch-profile WithBff

# WITHOUT BFF
dotnet run --project AzureBank.AppHost --launch-profile DirectApi

# Or with environment variable
USE_BFF=true dotnet run --project AzureBank.AppHost
USE_BFF=false dotnet run --project AzureBank.AppHost
```

---

## Sources

### Docker Compose
- [Docker Compose Profiles Documentation](https://docs.docker.com/compose/how-tos/profiles/)
- [Docker Compose Ports vs Expose](https://www.baeldung.com/ops/docker-compose-expose-vs-ports)
- [Disabling Services with Docker Profiles](https://intodot.net/disabling-services-with-docker-profiles/)

### .NET Aspire
- [Microsoft .NET Aspire Overview](https://learn.microsoft.com/en-us/dotnet/aspire/get-started/aspire-overview)
- [Aspire Launch Profiles](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/launch-profiles)
- [Aspire AppHost Configuration](https://learn.microsoft.com/en-us/dotnet/aspire/app-host/configuration)
- [Aspire Service Discovery](https://learn.microsoft.com/en-us/dotnet/aspire/service-discovery/overview)
- [Aspire Networking Overview](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/networking-overview)
- [Dave Brock - .NET Aspire Series](https://www.daveabrock.com/2025/09/16/net-aspire-5-orchestration-and-service-discovery/)

### BFF Pattern
- [Building ASP.NET Core BFF with React](https://dev.to/appie2go/ultimate-guide-to-building-your-first-aspnet-core-bff-with-a-react-app-4oeh)
- [YARP BFF Implementation](https://medium.com/@amhemanth/implementing-the-backends-for-frontends-bff-pattern-with-microsofts-yarp-and-net-minimal-apis-41c391974f43)
- [Duende BFF with React](https://wrapt.dev/blog/using-duende-bff-with-react)

---

**End of Docker vs Aspire Comparison**
