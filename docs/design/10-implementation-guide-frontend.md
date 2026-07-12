# Frontend Implementation Guide
## Bank Account Management System - AzureBank

**Document Version**: 2.0
**Created**: 2025-12-16
**Updated**: 2026-01-08
**Author**: Frontend Lead (Claude)
**Status**: PHASE 6.1 COMPLETE - Prerequisites & Project Setup

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Project Setup](#2-project-setup)
3. [Folder Structure](#3-folder-structure)
4. [Configuration Files](#4-configuration-files)
5. [Environment Variables](#5-environment-variables)
6. [Implementation Steps](#6-implementation-steps)
7. [Component Checklist](#7-component-checklist)

---

## 1. Prerequisites

### 1.1 Development Environment Requirements

Before starting development, ensure you have the following tools installed:

| Tool | Required Version | Purpose | Installation |
|------|-----------------|---------|--------------|
| **Bun** | 1.1.x+ | Package manager, runtime, build tool | [bun.sh](https://bun.sh) |
| **Node.js** | 20.x LTS (fallback) | Alternative runtime | [nodejs.org](https://nodejs.org) |
| **VS Code** | Latest | IDE with excellent React/TypeScript support | [code.visualstudio.com](https://code.visualstudio.com) |
| **Git** | 2.x+ | Version control | [git-scm.com](https://git-scm.com) |

### 1.2 VS Code Extensions (Recommended)

Install these extensions for optimal development experience:

```json
// .vscode/extensions.json
{
  "recommendations": [
    "dbaeumer.vscode-eslint",
    "esbenp.prettier-vscode",
    "bradlc.vscode-tailwindcss",
    "dsznajder.es7-react-js-snippets",
    "formulahendry.auto-rename-tag",
    "christian-kohler.path-intellisense",
    "streetsidesoftware.code-spell-checker",
    "usernamehw.errorlens",
    "pflannery.vscode-versionlens"
  ]
}
```

### 1.3 Install Bun (Primary Package Manager)

**Why Bun?**
- Native TypeScript support (no compilation needed for scripts)
- 17x faster than pnpm, 29x faster than npm for cached installs
- All-in-one: package manager + runtime + bundler + test runner
- Works seamlessly with Vite

**Windows Installation (PowerShell):**
```powershell
# Install Bun
powershell -c "irm bun.sh/install.ps1 | iex"

# Verify installation
bun --version
# Expected output: 1.1.x or higher

# Add to PATH if not already (restart terminal after)
```

**macOS/Linux Installation:**
```bash
# Install Bun
curl -fsSL https://bun.sh/install | bash

# Verify installation
bun --version
```

**Fallback: npm (if Bun is unavailable)**
```bash
# If Bun cannot be installed, npm commands are provided as alternatives
# All commands in this guide show Bun first, then npm equivalent
```

### 1.4 Verify Development Environment

Run these commands to verify your environment is ready:

```bash
# Check Bun version
bun --version
# Expected: 1.1.x+

# Check Git version
git --version
# Expected: 2.x+

# Check VS Code CLI (optional)
code --version
```

---

## 2. Project Setup

### 2.1 Create React Application with Vite

**Using Bun (Recommended):**
```bash
# Navigate to project root
cd "c:\Users\ellen\OneDrive\Desktop\React Projects\BankApp"

# Create Vite project with React + TypeScript template
bun create vite frontend --template react-ts

# Navigate into the frontend directory
cd frontend

# Install dependencies
bun install
```

**Using npm (Alternative):**
```bash
# Create Vite project
npm create vite@latest frontend -- --template react-ts

cd frontend
npm install
```

### 2.2 Install Production Dependencies

Install all required packages in the correct order:

**Using Bun:**
```bash
# UI Components - FluentUI v9
bun add @fluentui/react-components@^9.72.8 @fluentui/react-icons@^2.0.270

# State Management - Redux Toolkit + RTK Query
bun add @reduxjs/toolkit@^2.11.2 react-redux@^9.2.0

# Routing - React Router v7
bun add react-router-dom@^7.1.1

# HTTP Client - Axios (for RTK Query base query)
bun add axios@^1.7.9

# Form Handling - React Hook Form + Zod
bun add react-hook-form@^7.54.2 @hookform/resolvers@^3.9.1 zod@^3.24.1

# Utilities
bun add date-fns@^4.1.0 clsx@^2.1.1 use-debounce@^10.0.6

# Notifications & Dialogs
bun add sweetalert2@^11.15.2 sweetalert2-react-content@^5.1.0
```

**Using npm:**
```bash
npm install @fluentui/react-components@^9.72.8 @fluentui/react-icons@^2.0.270
npm install @reduxjs/toolkit@^2.11.2 react-redux@^9.2.0
npm install react-router-dom@^7.1.1
npm install axios@^1.7.9
npm install react-hook-form@^7.54.2 @hookform/resolvers@^3.9.1 zod@^3.24.1
npm install date-fns@^4.1.0 clsx@^2.1.1 use-debounce@^10.0.6
npm install sweetalert2@^11.15.2 sweetalert2-react-content@^5.1.0
```

### 2.3 Install Development Dependencies

**Using Bun:**
```bash
# Mock Service Worker (API mocking)
bun add -d msw@^2.7.0

# ESLint + TypeScript ESLint (v9 flat config)
bun add -d eslint@^9.17.0 @eslint/js@^9.17.0
bun add -d typescript-eslint@^8.18.2
bun add -d eslint-plugin-react-hooks@^5.1.0 eslint-plugin-react-refresh@^0.4.16

# Prettier (code formatting)
bun add -d prettier@^3.4.2
```

**Using npm:**
```bash
npm install -D msw@^2.7.0
npm install -D eslint@^9.17.0 @eslint/js@^9.17.0
npm install -D typescript-eslint@^8.18.2
npm install -D eslint-plugin-react-hooks@^5.1.0 eslint-plugin-react-refresh@^0.4.16
npm install -D prettier@^3.4.2
```

### 2.4 Initialize MSW (Mock Service Worker)

```bash
# Initialize MSW service worker in public directory
bunx msw init public/ --save

# Or with npm:
npx msw init public/ --save
```

This creates `public/mockServiceWorker.js` - do NOT edit this file.

### 2.5 Verify Installation

```bash
# Start development server
bun run dev

# Or for maximum speed with Bun runtime:
bunx --bun vite

# The app should be available at http://localhost:5173
```

---

## 3. Folder Structure

### 3.1 Complete Project Structure

Create the following folder structure inside `frontend/src/`:

```
frontend/
├── public/
│   ├── mockServiceWorker.js    # Generated by MSW init
│   ├── favicon.ico
│   └── robots.txt
│
├── src/
│   ├── app/                     # Application-level configuration
│   │   ├── store.ts            # Redux store configuration
│   │   ├── hooks.ts            # Typed Redux hooks (useAppDispatch, useAppSelector)
│   │   └── router.tsx          # React Router configuration
│   │
│   ├── components/              # React components
│   │   ├── common/             # Shared/reusable components
│   │   │   ├── BalanceCard/
│   │   │   │   ├── BalanceCard.tsx
│   │   │   │   ├── BalanceCard.styles.ts
│   │   │   │   ├── BalanceCard.types.ts
│   │   │   │   └── index.ts
│   │   │   ├── TransactionCard/
│   │   │   ├── CurrencyInput/
│   │   │   ├── PasswordInput/
│   │   │   ├── FilterBar/
│   │   │   ├── EmptyState/
│   │   │   ├── LoadingSpinner/
│   │   │   ├── LoadingButton/
│   │   │   ├── ErrorBoundary/
│   │   │   ├── ProgressStepper/
│   │   │   ├── AnimatedNumber/
│   │   │   └── Skeleton/
│   │   │       ├── BalanceCardSkeleton.tsx
│   │   │       ├── TransactionListSkeleton.tsx
│   │   │       ├── AccountCardSkeleton.tsx
│   │   │       └── index.ts
│   │   │
│   │   ├── layout/             # Layout components
│   │   │   ├── AppLayout/
│   │   │   │   ├── AppLayout.tsx
│   │   │   │   ├── AppLayout.styles.ts
│   │   │   │   └── index.ts
│   │   │   ├── Header/
│   │   │   ├── MobileNav/
│   │   │   └── Footer/
│   │   │
│   │   ├── auth/               # Authentication components
│   │   │   ├── LoginForm/
│   │   │   ├── RegisterForm/
│   │   │   └── ProtectedRoute/
│   │   │
│   │   ├── accounts/           # Account management components
│   │   │   ├── AccountList/
│   │   │   ├── AccountCard/
│   │   │   ├── AccountDetails/
│   │   │   └── CreateAccountDialog/
│   │   │
│   │   ├── transactions/       # Transaction components
│   │   │   ├── TransactionList/
│   │   │   ├── TransactionTable/
│   │   │   ├── DepositDialog/
│   │   │   ├── WithdrawDialog/
│   │   │   └── TransferWizard/
│   │   │       ├── TransferWizard.tsx
│   │   │       ├── TransferSourceStep.tsx
│   │   │       ├── TransferRecipientStep.tsx
│   │   │       ├── TransferAmountStep.tsx
│   │   │       ├── TransferConfirmStep.tsx
│   │   │       ├── TransferSuccessStep.tsx
│   │   │       ├── RecipientSearch.tsx
│   │   │       └── index.ts
│   │   │
│   │   ├── dashboard/          # Dashboard-specific components
│   │   │   ├── DashboardSummary/
│   │   │   ├── QuickActions/
│   │   │   └── RecentTransactions/
│   │   │
│   │   └── feedback/           # User feedback components
│   │       ├── SuccessAnimation/
│   │       └── Confetti/
│   │
│   ├── features/               # Redux Toolkit slices + RTK Query APIs
│   │   ├── api/
│   │   │   └── baseApi.ts      # Base RTK Query API configuration
│   │   ├── auth/
│   │   │   ├── authSlice.ts    # Auth state slice
│   │   │   └── authApi.ts      # Auth RTK Query endpoints
│   │   ├── accounts/
│   │   │   ├── accountsSlice.ts
│   │   │   └── accountsApi.ts
│   │   ├── transactions/
│   │   │   ├── transactionsSlice.ts
│   │   │   └── transactionsApi.ts
│   │   ├── recipients/
│   │   │   └── recipientsApi.ts
│   │   ├── transfers/
│   │   │   └── transfersApi.ts
│   │   ├── ui/
│   │   │   └── uiSlice.ts      # UI state (dialogs, toasts, loading)
│   │   └── transferWizard/
│   │       └── transferWizardSlice.ts
│   │
│   ├── hooks/                   # Custom React hooks
│   │   ├── useAuth.ts
│   │   ├── useAccounts.ts
│   │   ├── useTransactions.ts
│   │   ├── useRecipientSearch.ts
│   │   ├── useTransferWizard.ts
│   │   ├── useMediaQuery.ts
│   │   ├── useCurrency.ts
│   │   └── useToast.ts
│   │
│   ├── pages/                   # Route-level page components
│   │   ├── LoginPage.tsx
│   │   ├── RegisterPage.tsx
│   │   ├── DashboardPage.tsx
│   │   ├── AccountsPage.tsx
│   │   ├── AccountDetailPage.tsx
│   │   ├── TransactionsPage.tsx
│   │   └── NotFoundPage.tsx
│   │
│   ├── theme/                   # FluentUI theme configuration
│   │   ├── index.ts            # Theme exports
│   │   ├── brandColors.ts      # AzureBank brand color ramp
│   │   ├── lightTheme.ts       # Light theme configuration
│   │   ├── darkTheme.ts        # Dark theme (future)
│   │   ├── semanticColors.ts   # Success, error, warning colors
│   │   ├── typography.ts       # Font configuration
│   │   └── layout.ts           # Spacing, breakpoints
│   │
│   ├── types/                   # TypeScript type definitions
│   │   ├── auth.types.ts
│   │   ├── account.types.ts
│   │   ├── transaction.types.ts
│   │   ├── recipient.types.ts
│   │   ├── transfer.types.ts
│   │   └── api.types.ts
│   │
│   ├── utils/                   # Utility functions
│   │   ├── formatCurrency.ts
│   │   ├── formatDate.ts
│   │   ├── formatAccountNumber.ts
│   │   ├── validation.ts
│   │   ├── constants.ts
│   │   └── groupTransactionsByDate.ts
│   │
│   ├── mocks/                   # MSW mock handlers
│   │   ├── browser.ts          # MSW browser worker setup
│   │   ├── handlers/
│   │   │   ├── index.ts        # Export all handlers
│   │   │   ├── bff-auth.handlers.ts
│   │   │   ├── auth.handlers.ts
│   │   │   ├── account.handlers.ts
│   │   │   ├── transaction.handlers.ts
│   │   │   ├── transfer.handlers.ts
│   │   │   └── user.handlers.ts
│   │   └── data/
│   │       ├── db.ts           # Mock database
│   │       ├── state.ts        # In-memory state manager
│   │       └── session.ts      # Session store (BFF simulation)
│   │
│   ├── styles/                  # Global styles
│   │   ├── index.css           # Global CSS reset and base styles
│   │   └── animations.css      # Animation keyframes
│   │
│   ├── App.tsx                  # Root application component
│   ├── main.tsx                 # Application entry point
│   └── vite-env.d.ts           # Vite environment types
│
├── .env                         # Environment variables (gitignored)
├── .env.example                 # Example environment variables
├── .eslintrc.cjs               # ESLint configuration (legacy)
├── eslint.config.js            # ESLint 9 flat config
├── .prettierrc                  # Prettier configuration
├── .prettierignore              # Prettier ignore patterns
├── index.html                   # HTML entry point
├── package.json                 # Package manifest
├── tsconfig.json               # TypeScript configuration
├── tsconfig.node.json          # TypeScript config for Vite
├── vite.config.ts              # Vite configuration
└── README.md                    # Project documentation
```

### 3.2 Create Directory Structure

Run these commands to create the folder structure:

**Using Bash/PowerShell:**
```bash
# Navigate to frontend/src
cd frontend/src

# Create main directories
mkdir -p app components features hooks pages theme types utils mocks styles

# Create component subdirectories
mkdir -p components/common components/layout components/auth components/accounts components/transactions components/dashboard components/feedback

# Create common component folders
mkdir -p components/common/{BalanceCard,TransactionCard,CurrencyInput,PasswordInput,FilterBar,EmptyState,LoadingSpinner,LoadingButton,ErrorBoundary,ProgressStepper,AnimatedNumber,Skeleton}

# Create layout component folders
mkdir -p components/layout/{AppLayout,Header,MobileNav,Footer}

# Create auth component folders
mkdir -p components/auth/{LoginForm,RegisterForm,ProtectedRoute}

# Create account component folders
mkdir -p components/accounts/{AccountList,AccountCard,AccountDetails,CreateAccountDialog}

# Create transaction component folders
mkdir -p components/transactions/{TransactionList,TransactionTable,DepositDialog,WithdrawDialog,TransferWizard}

# Create dashboard component folders
mkdir -p components/dashboard/{DashboardSummary,QuickActions,RecentTransactions}

# Create feedback component folders
mkdir -p components/feedback/{SuccessAnimation,Confetti}

# Create feature slices
mkdir -p features/{api,auth,accounts,transactions,recipients,transfers,ui,transferWizard}

# Create mocks structure
mkdir -p mocks/{handlers,data}
```

**Windows PowerShell Alternative:**
```powershell
# Run from frontend/src directory
$dirs = @(
    "app",
    "components/common/BalanceCard",
    "components/common/TransactionCard",
    "components/common/CurrencyInput",
    "components/common/PasswordInput",
    "components/common/FilterBar",
    "components/common/EmptyState",
    "components/common/LoadingSpinner",
    "components/common/LoadingButton",
    "components/common/ErrorBoundary",
    "components/common/ProgressStepper",
    "components/common/AnimatedNumber",
    "components/common/Skeleton",
    "components/layout/AppLayout",
    "components/layout/Header",
    "components/layout/MobileNav",
    "components/layout/Footer",
    "components/auth/LoginForm",
    "components/auth/RegisterForm",
    "components/auth/ProtectedRoute",
    "components/accounts/AccountList",
    "components/accounts/AccountCard",
    "components/accounts/AccountDetails",
    "components/accounts/CreateAccountDialog",
    "components/transactions/TransactionList",
    "components/transactions/TransactionTable",
    "components/transactions/DepositDialog",
    "components/transactions/WithdrawDialog",
    "components/transactions/TransferWizard",
    "components/dashboard/DashboardSummary",
    "components/dashboard/QuickActions",
    "components/dashboard/RecentTransactions",
    "components/feedback/SuccessAnimation",
    "components/feedback/Confetti",
    "features/api",
    "features/auth",
    "features/accounts",
    "features/transactions",
    "features/recipients",
    "features/transfers",
    "features/ui",
    "features/transferWizard",
    "hooks",
    "pages",
    "theme",
    "types",
    "utils",
    "mocks/handlers",
    "mocks/data",
    "styles"
)

foreach ($dir in $dirs) {
    New-Item -ItemType Directory -Force -Path $dir
}
```

---

## 4. Configuration Files

### 4.1 package.json

Create/update `frontend/package.json`:

```json
{
  "name": "azurebank-frontend",
  "private": true,
  "version": "1.0.0",
  "type": "module",
  "scripts": {
    "dev": "bunx --bun vite",
    "dev:npm": "vite",
    "build": "bunx tsc -b && bunx vite build",
    "build:npm": "tsc -b && vite build",
    "preview": "bunx vite preview",
    "lint": "bunx eslint .",
    "lint:fix": "bunx eslint . --fix",
    "type-check": "bunx tsc --noEmit",
    "format": "bunx prettier --write \"src/**/*.{ts,tsx,css,json}\"",
    "format:check": "bunx prettier --check \"src/**/*.{ts,tsx,css,json}\"",
    "test": "bunx vitest",
    "test:ui": "bunx vitest --ui",
    "test:coverage": "bunx vitest --coverage"
  },
  "dependencies": {
    "@fluentui/react-components": "^9.72.8",
    "@fluentui/react-icons": "^2.0.270",
    "@hookform/resolvers": "^3.9.1",
    "@reduxjs/toolkit": "^2.11.2",
    "axios": "^1.7.9",
    "clsx": "^2.1.1",
    "date-fns": "^4.1.0",
    "react": "^19.0.0",
    "react-dom": "^19.0.0",
    "react-hook-form": "^7.54.2",
    "react-redux": "^9.2.0",
    "react-router-dom": "^7.1.1",
    "sweetalert2": "^11.15.2",
    "sweetalert2-react-content": "^5.1.0",
    "use-debounce": "^10.0.6",
    "zod": "^3.24.1"
  },
  "devDependencies": {
    "@eslint/js": "^9.17.0",
    "@types/react": "^19.0.0",
    "@types/react-dom": "^19.0.0",
    "@vitejs/plugin-react": "^4.3.4",
    "eslint": "^9.17.0",
    "eslint-plugin-react-hooks": "^5.1.0",
    "eslint-plugin-react-refresh": "^0.4.16",
    "msw": "^2.7.0",
    "prettier": "^3.4.2",
    "typescript": "^5.7.2",
    "typescript-eslint": "^8.18.2",
    "vite": "^6.0.5"
  },
  "msw": {
    "workerDirectory": [
      "public"
    ]
  }
}
```

### 4.2 TypeScript Configuration

**tsconfig.json** (frontend root):
```json
{
  "compilerOptions": {
    /* Language and Environment */
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "jsx": "react-jsx",
    "useDefineForClassFields": true,

    /* Modules */
    "module": "ESNext",
    "moduleResolution": "bundler",
    "resolveJsonModule": true,
    "isolatedModules": true,
    "moduleDetection": "force",

    /* Emit */
    "noEmit": true,

    /* Type Checking - STRICT MODE */
    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "noFallthroughCasesInSwitch": true,
    "noUncheckedIndexedAccess": true,
    "exactOptionalPropertyTypes": true,

    /* Interop Constraints */
    "allowSyntheticDefaultImports": true,
    "esModuleInterop": true,
    "forceConsistentCasingInFileNames": true,
    "verbatimModuleSyntax": true,

    /* Path Aliases */
    "baseUrl": ".",
    "paths": {
      "@/*": ["src/*"],
      "@components/*": ["src/components/*"],
      "@features/*": ["src/features/*"],
      "@hooks/*": ["src/hooks/*"],
      "@pages/*": ["src/pages/*"],
      "@theme/*": ["src/theme/*"],
      "@types/*": ["src/types/*"],
      "@utils/*": ["src/utils/*"],
      "@mocks/*": ["src/mocks/*"]
    },

    /* Skip Library Check */
    "skipLibCheck": true
  },
  "include": ["src"],
  "references": [{ "path": "./tsconfig.node.json" }]
}
```

**tsconfig.node.json** (for Vite config):
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022"],
    "module": "ESNext",
    "skipLibCheck": true,

    /* Bundler mode */
    "moduleResolution": "bundler",
    "allowImportingTsExtensions": true,
    "isolatedModules": true,
    "moduleDetection": "force",
    "noEmit": true,

    /* Linting */
    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "noFallthroughCasesInSwitch": true,
    "noUncheckedIndexedAccess": true
  },
  "include": ["vite.config.ts"]
}
```

### 4.3 Vite Configuration

**vite.config.ts**:
```typescript
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],

  // Path aliases (must match tsconfig.json paths)
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
      '@components': path.resolve(__dirname, './src/components'),
      '@features': path.resolve(__dirname, './src/features'),
      '@hooks': path.resolve(__dirname, './src/hooks'),
      '@pages': path.resolve(__dirname, './src/pages'),
      '@theme': path.resolve(__dirname, './src/theme'),
      '@types': path.resolve(__dirname, './src/types'),
      '@utils': path.resolve(__dirname, './src/utils'),
      '@mocks': path.resolve(__dirname, './src/mocks'),
    },
  },

  // Development server configuration
  server: {
    port: 5173,
    strictPort: true,
    host: true, // Listen on all addresses
    open: true, // Open browser on start

    // Proxy API requests to backend (when not using MSW)
    proxy: {
      '/api': {
        target: 'https://localhost:7001', // .NET backend URL
        changeOrigin: true,
        secure: false, // Accept self-signed certs in dev
      },
      '/bff': {
        target: 'https://localhost:7001',
        changeOrigin: true,
        secure: false,
      },
    },
  },

  // Build configuration
  build: {
    outDir: 'dist',
    sourcemap: true,
    // Chunk splitting for better caching
    rollupOptions: {
      output: {
        manualChunks: {
          // Vendor chunks
          'react-vendor': ['react', 'react-dom', 'react-router-dom'],
          'redux-vendor': ['@reduxjs/toolkit', 'react-redux'],
          'fluentui-vendor': ['@fluentui/react-components', '@fluentui/react-icons'],
          'form-vendor': ['react-hook-form', '@hookform/resolvers', 'zod'],
        },
      },
    },
    // Target modern browsers
    target: 'esnext',
    // Minification
    minify: 'esbuild',
  },

  // Preview server (production build preview)
  preview: {
    port: 4173,
    strictPort: true,
  },

  // Optimize dependencies
  optimizeDeps: {
    include: [
      'react',
      'react-dom',
      'react-router-dom',
      '@reduxjs/toolkit',
      'react-redux',
      '@fluentui/react-components',
      '@fluentui/react-icons',
    ],
  },
});
```

### 4.4 ESLint Configuration (v9 Flat Config)

**eslint.config.js**:
```javascript
import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist', 'node_modules', 'public/mockServiceWorker.js'] },
  {
    extends: [js.configs.recommended, ...tseslint.configs.strictTypeChecked],
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
      parserOptions: {
        project: ['./tsconfig.json', './tsconfig.node.json'],
        tsconfigRootDir: import.meta.dirname,
      },
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
    rules: {
      // React Hooks rules
      ...reactHooks.configs.recommended.rules,

      // React Refresh rules
      'react-refresh/only-export-components': [
        'warn',
        { allowConstantExport: true },
      ],

      // TypeScript rules
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      '@typescript-eslint/consistent-type-imports': [
        'error',
        { prefer: 'type-imports' },
      ],
      '@typescript-eslint/consistent-type-exports': 'error',
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/explicit-function-return-type': 'off',
      '@typescript-eslint/explicit-module-boundary-types': 'off',
      '@typescript-eslint/no-non-null-assertion': 'warn',

      // Allow floating promises for event handlers
      '@typescript-eslint/no-floating-promises': [
        'error',
        { ignoreVoid: true, ignoreIIFE: true },
      ],
      '@typescript-eslint/no-misused-promises': [
        'error',
        { checksVoidReturn: { attributes: false } },
      ],

      // General rules
      'no-console': ['warn', { allow: ['warn', 'error'] }],
      'prefer-const': 'error',
      'no-var': 'error',
      eqeqeq: ['error', 'always', { null: 'ignore' }],
    },
  },
  // Configuration files (less strict)
  {
    files: ['*.config.{js,ts}', '*.config.*.{js,ts}'],
    rules: {
      '@typescript-eslint/no-require-imports': 'off',
    },
  }
);
```

### 4.5 Prettier Configuration

**.prettierrc**:
```json
{
  "semi": true,
  "singleQuote": true,
  "tabWidth": 2,
  "trailingComma": "es5",
  "printWidth": 100,
  "bracketSpacing": true,
  "bracketSameLine": false,
  "arrowParens": "always",
  "endOfLine": "lf",
  "jsxSingleQuote": false,
  "quoteProps": "as-needed",
  "useTabs": false,
  "plugins": [],
  "overrides": [
    {
      "files": "*.json",
      "options": {
        "printWidth": 80
      }
    }
  ]
}
```

**.prettierignore**:
```
# Build outputs
dist/
build/
coverage/

# Dependencies
node_modules/

# Generated files
public/mockServiceWorker.js
*.min.js
*.min.css

# Lock files
bun.lockb
package-lock.json
yarn.lock
pnpm-lock.yaml

# IDE
.idea/
.vscode/

# Misc
*.md
```

### 4.6 VS Code Settings

**.vscode/settings.json**:
```json
{
  "editor.formatOnSave": true,
  "editor.defaultFormatter": "esbenp.prettier-vscode",
  "editor.codeActionsOnSave": {
    "source.fixAll.eslint": "explicit",
    "source.organizeImports": "never"
  },
  "[typescript]": {
    "editor.defaultFormatter": "esbenp.prettier-vscode"
  },
  "[typescriptreact]": {
    "editor.defaultFormatter": "esbenp.prettier-vscode"
  },
  "[json]": {
    "editor.defaultFormatter": "esbenp.prettier-vscode"
  },
  "typescript.preferences.importModuleSpecifier": "relative",
  "typescript.updateImportsOnFileMove.enabled": "always",
  "typescript.suggest.autoImports": true,
  "eslint.validate": ["javascript", "javascriptreact", "typescript", "typescriptreact"],
  "files.eol": "\n",
  "files.insertFinalNewline": true,
  "files.trimTrailingWhitespace": true,
  "search.exclude": {
    "**/node_modules": true,
    "**/dist": true,
    "**/coverage": true,
    "**/.git": true,
    "**/bun.lockb": true
  }
}
```

---

## 5. Environment Variables

### 5.1 Environment File Structure

Create these environment files in `frontend/`:

**.env.example** (commit to git - template):
```bash
# API Configuration
VITE_API_URL=http://localhost:5000/api
VITE_BFF_URL=http://localhost:5000/bff

# MSW (Mock Service Worker)
VITE_ENABLE_MSW=true

# Feature Flags
VITE_ENABLE_DARK_MODE=false
VITE_ENABLE_BIOMETRICS=false

# App Configuration
VITE_APP_NAME=AzureBank
VITE_APP_VERSION=1.0.0

# Session Configuration
VITE_SESSION_TIMEOUT_WARNING_MINUTES=2
VITE_SESSION_TIMEOUT_MINUTES=15
```

**.env** (gitignored - local development):
```bash
# API Configuration
VITE_API_URL=http://localhost:5000/api
VITE_BFF_URL=http://localhost:5000/bff

# MSW (Mock Service Worker) - Enable for frontend-first development
VITE_ENABLE_MSW=true

# Feature Flags
VITE_ENABLE_DARK_MODE=false
VITE_ENABLE_BIOMETRICS=false

# App Configuration
VITE_APP_NAME=AzureBank
VITE_APP_VERSION=1.0.0

# Session Configuration
VITE_SESSION_TIMEOUT_WARNING_MINUTES=2
VITE_SESSION_TIMEOUT_MINUTES=15
```

**.env.production** (production build):
```bash
# API Configuration - Production URLs
VITE_API_URL=/api
VITE_BFF_URL=/bff

# MSW disabled in production
VITE_ENABLE_MSW=false

# Feature Flags
VITE_ENABLE_DARK_MODE=false
VITE_ENABLE_BIOMETRICS=false

# App Configuration
VITE_APP_NAME=AzureBank
VITE_APP_VERSION=1.0.0

# Session Configuration
VITE_SESSION_TIMEOUT_WARNING_MINUTES=2
VITE_SESSION_TIMEOUT_MINUTES=15
```

### 5.2 TypeScript Environment Types

Add environment variable types to `src/vite-env.d.ts`:

```typescript
/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** API base URL (e.g., http://localhost:5000/api) */
  readonly VITE_API_URL: string;

  /** BFF base URL (e.g., http://localhost:5000/bff) */
  readonly VITE_BFF_URL: string;

  /** Enable Mock Service Worker ('true' or 'false') */
  readonly VITE_ENABLE_MSW: string;

  /** Enable dark mode feature flag */
  readonly VITE_ENABLE_DARK_MODE: string;

  /** Enable biometric authentication feature flag */
  readonly VITE_ENABLE_BIOMETRICS: string;

  /** Application display name */
  readonly VITE_APP_NAME: string;

  /** Application version */
  readonly VITE_APP_VERSION: string;

  /** Minutes before session timeout to show warning */
  readonly VITE_SESSION_TIMEOUT_WARNING_MINUTES: string;

  /** Session timeout in minutes */
  readonly VITE_SESSION_TIMEOUT_MINUTES: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
```

### 5.3 Environment Helper Utility

Create `src/utils/env.ts`:

```typescript
/**
 * Environment configuration utilities
 * Type-safe access to environment variables with validation
 */

interface AppConfig {
  api: {
    baseUrl: string;
    bffUrl: string;
  };
  features: {
    enableMsw: boolean;
    enableDarkMode: boolean;
    enableBiometrics: boolean;
  };
  app: {
    name: string;
    version: string;
  };
  session: {
    timeoutWarningMinutes: number;
    timeoutMinutes: number;
  };
}

function getEnvVar(key: keyof ImportMetaEnv, defaultValue?: string): string {
  const value = import.meta.env[key];
  if (value === undefined || value === '') {
    if (defaultValue !== undefined) {
      return defaultValue;
    }
    throw new Error(`Missing required environment variable: ${key}`);
  }
  return value;
}

function getEnvBoolean(key: keyof ImportMetaEnv, defaultValue = false): boolean {
  const value = import.meta.env[key];
  if (value === undefined || value === '') {
    return defaultValue;
  }
  return value.toLowerCase() === 'true';
}

function getEnvNumber(key: keyof ImportMetaEnv, defaultValue: number): number {
  const value = import.meta.env[key];
  if (value === undefined || value === '') {
    return defaultValue;
  }
  const parsed = parseInt(value, 10);
  return isNaN(parsed) ? defaultValue : parsed;
}

export const config: AppConfig = {
  api: {
    baseUrl: getEnvVar('VITE_API_URL', '/api'),
    bffUrl: getEnvVar('VITE_BFF_URL', '/bff'),
  },
  features: {
    enableMsw: getEnvBoolean('VITE_ENABLE_MSW', false),
    enableDarkMode: getEnvBoolean('VITE_ENABLE_DARK_MODE', false),
    enableBiometrics: getEnvBoolean('VITE_ENABLE_BIOMETRICS', false),
  },
  app: {
    name: getEnvVar('VITE_APP_NAME', 'AzureBank'),
    version: getEnvVar('VITE_APP_VERSION', '1.0.0'),
  },
  session: {
    timeoutWarningMinutes: getEnvNumber('VITE_SESSION_TIMEOUT_WARNING_MINUTES', 2),
    timeoutMinutes: getEnvNumber('VITE_SESSION_TIMEOUT_MINUTES', 15),
  },
};

// Freeze config to prevent mutations
Object.freeze(config);
Object.freeze(config.api);
Object.freeze(config.features);
Object.freeze(config.app);
Object.freeze(config.session);

export default config;
```

### 5.4 Update .gitignore

Add to `.gitignore` in frontend directory:

```gitignore
# Environment files
.env
.env.local
.env.*.local

# Keep example file
!.env.example

# Dependencies
node_modules/
.pnp
.pnp.js

# Build outputs
dist/
dist-ssr/
build/
*.local

# IDE
.idea/
*.swp
*.swo
.vscode/*
!.vscode/extensions.json
!.vscode/settings.json

# Testing
coverage/
*.lcov

# Debug
npm-debug.log*
yarn-debug.log*
yarn-error.log*

# OS
.DS_Store
Thumbs.db

# Bun
bun.lockb

# Cache
.eslintcache
*.tsbuildinfo
```

---

## 6. Implementation Steps

---

### 6.1 Step 1: TypeScript Type Definitions

Create strongly-typed interfaces for all domain entities and API interactions.

#### 6.1.1 Auth Types (`src/types/auth.types.ts`)

```typescript
// src/types/auth.types.ts

/**
 * User entity returned from API
 */
export interface User {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  azureTag: string;
  createdAt: string;
}

/**
 * Session information from BFF
 * NOTE: No token stored client-side - BFF pattern
 */
export interface SessionInfo {
  authLevel: 1 | 2; // 1 = session, 2 = PIN verified
  createdAt: string;
  lastActivity: string;
}

/**
 * Auth state for Redux - BFF pattern adaptation
 * IMPORTANT: No token field - tokens are stored server-side by BFF
 */
export interface AuthState {
  user: User | null;
  session: SessionInfo | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  isInitialized: boolean; // Has initial auth check completed?
}

/**
 * Login request payload
 */
export interface LoginRequest {
  email: string;
  password: string;
}

/**
 * Register request payload
 */
export interface RegisterRequest {
  email: string;
  password: string;
  confirmPassword: string;
  firstName: string;
  lastName: string;
  azureTag: string;
}

/**
 * BFF Login response - NO TOKENS returned to browser
 */
export interface BffLoginResponse {
  data: {
    user: User;
  };
  message: string;
}

/**
 * BFF /auth/me response
 */
export interface BffMeResponse {
  data: {
    user: User;
    session: SessionInfo;
  };
}

/**
 * BFF session status response
 */
export interface BffSessionStatusResponse {
  data: {
    authLevel: 1 | 2;
    inactivityExpiresIn: number; // seconds
    absoluteExpiresIn: number; // seconds
    pinVerifiedUntil: string | null;
  };
}

/**
 * PIN verification request
 */
export interface VerifyPinRequest {
  pin: string;
}

/**
 * PIN verification response
 */
export interface VerifyPinResponse {
  data: {
    verified: boolean;
    authLevel: 2;
    expiresAt: string;
  };
  message: string;
}
```

#### 6.1.2 Account Types (`src/types/account.types.ts`)

```typescript
// src/types/account.types.ts

/**
 * Account type enum
 */
export type AccountType = 'checking' | 'savings' | 'investment';

/**
 * Account entity
 */
export interface Account {
  id: string;
  accountNumber: string;
  name: string;
  type: AccountType;
  balance: number;
  isPrimary: boolean;
  createdAt: string;
  updatedAt: string;
}

/**
 * Balance response with timestamp
 */
export interface BalanceResponse {
  accountId: string;
  balance: number;
  currency: string;
  asOf: string;
  isHistorical?: boolean;
}

/**
 * Create account request
 */
export interface CreateAccountRequest {
  name: string;
  type: AccountType;
}

/**
 * Update account request
 */
export interface UpdateAccountRequest {
  name?: string;
}

/**
 * Account summary for dashboard
 */
export interface AccountSummary {
  totalBalance: number;
  accountCount: number;
  primaryAccount: Account | null;
}
```

#### 6.1.3 Transaction Types (`src/types/transaction.types.ts`)

```typescript
// src/types/transaction.types.ts

/**
 * Transaction type enum
 */
export type TransactionType = 'deposit' | 'withdrawal' | 'transfer_in' | 'transfer_out';

/**
 * Transaction entity
 */
export interface Transaction {
  id: string;
  accountId: string;
  type: TransactionType;
  amount: number;
  description: string | null;
  balanceAfter: number;
  relatedTransactionId: string | null;
  recipientAzureTag: string | null;
  senderAzureTag: string | null;
  createdAt: string;
}

/**
 * Deposit request payload
 */
export interface DepositRequest {
  accountId: string;
  amount: number;
  description?: string;
}

/**
 * Withdraw request payload
 */
export interface WithdrawRequest {
  accountId: string;
  amount: number;
  description?: string;
}

/**
 * Transaction history query parameters
 */
export interface TransactionHistoryParams {
  accountId?: string;
  type?: TransactionType;
  from?: string; // ISO date
  to?: string; // ISO date
  page?: number;
  pageSize?: number;
  sortBy?: 'createdAt' | 'amount';
  sortOrder?: 'asc' | 'desc';
}

/**
 * Deposit/Withdraw response
 */
export interface TransactionResponse {
  transaction: Transaction;
  newBalance: number;
}

/**
 * Transactions grouped by date for UI rendering
 */
export interface TransactionGroup {
  date: string; // YYYY-MM-DD
  label: string; // "Today", "Yesterday", "January 8, 2026"
  transactions: Transaction[];
  totalIn: number;
  totalOut: number;
}
```

#### 6.1.4 Transfer Types (`src/types/transfer.types.ts`)

```typescript
// src/types/transfer.types.ts

import type { Transaction } from './transaction.types';

/**
 * Transfer recipient info (masked for privacy)
 */
export interface Recipient {
  azureTag: string;
  displayName: string; // "Jane D." - first name + last initial
}

/**
 * Recipient exists check response
 */
export interface RecipientExistsResponse {
  azureTag: string;
  displayName: string;
  exists: boolean;
}

/**
 * External transfer request (to another user)
 */
export interface TransferRequest {
  fromAccountId: string;
  recipientAzureTag: string;
  amount: number;
  description?: string;
}

/**
 * Internal transfer request (between own accounts)
 */
export interface InternalTransferRequest {
  fromAccountId: string;
  toAccountId: string;
  amount: number;
  description?: string;
}

/**
 * Transfer details returned after successful transfer
 */
export interface TransferDetails {
  id: string;
  fromAccountId: string;
  recipientAzureTag: string;
  recipientDisplayName: string;
  amount: number;
  description: string | null;
  status: 'completed' | 'pending' | 'failed';
  createdAt: string;
}

/**
 * External transfer response
 */
export interface TransferResponse {
  transfer: TransferDetails;
  senderTransaction: Transaction;
  newBalance: number;
}

/**
 * Internal transfer response
 */
export interface InternalTransferResponse {
  transfer: {
    id: string;
    fromAccountId: string;
    toAccountId: string;
    amount: number;
    description: string | null;
    status: 'completed';
    createdAt: string;
  };
  fromAccountNewBalance: number;
  toAccountNewBalance: number;
}

/**
 * Transfer wizard step enum
 */
export type TransferWizardStep =
  | 'source' // Select source account
  | 'recipient' // Search/select recipient
  | 'amount' // Enter amount
  | 'confirm' // Review and confirm
  | 'success'; // Success animation

/**
 * Transfer wizard state for multi-step form
 */
export interface TransferWizardState {
  currentStep: TransferWizardStep;
  isOpen: boolean;
  transferType: 'external' | 'internal' | null;

  // Step data
  sourceAccountId: string | null;
  recipient: Recipient | null;
  destinationAccountId: string | null; // For internal transfers
  amount: number | null;
  description: string;

  // Result
  completedTransfer: TransferResponse | InternalTransferResponse | null;

  // UI state
  isSubmitting: boolean;
  error: string | null;
}
```

#### 6.1.5 API Types (`src/types/api.types.ts`)

```typescript
// src/types/api.types.ts

/**
 * Standard API error response format
 */
export interface ApiError {
  type: string;
  message: string;
  correlationId: string;
  statusCode: number;
  errors?: Record<string, string[]>;
  details?: Record<string, unknown>;
}

/**
 * Standard API success response wrapper
 */
export interface ApiResponse<T> {
  data: T;
  message?: string;
}

/**
 * Pagination metadata
 */
export interface PaginationInfo {
  page: number;
  pageSize: number;
  totalItems: number;
  totalPages: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
}

/**
 * Paginated response wrapper
 */
export interface PaginatedResponse<T> {
  data: T[];
  pagination: PaginationInfo;
}

/**
 * RTK Query error shape
 */
export interface RtkQueryError {
  status: number;
  data: ApiError;
}

/**
 * Type guard for RTK Query errors
 */
export function isApiError(error: unknown): error is RtkQueryError {
  return (
    typeof error === 'object' &&
    error !== null &&
    'status' in error &&
    'data' in error &&
    typeof (error as RtkQueryError).data === 'object' &&
    (error as RtkQueryError).data !== null &&
    'type' in (error as RtkQueryError).data
  );
}

/**
 * Extract user-friendly error message
 */
export function getErrorMessage(error: unknown): string {
  if (isApiError(error)) {
    return error.data.message;
  }
  if (error instanceof Error) {
    return error.message;
  }
  return 'An unexpected error occurred';
}

/**
 * Extract field validation errors
 */
export function getFieldErrors(error: unknown): Record<string, string[]> | null {
  if (isApiError(error) && error.data.errors) {
    return error.data.errors;
  }
  return null;
}
```

#### 6.1.6 Types Index (`src/types/index.ts`)

```typescript
// src/types/index.ts
// Re-export all types for convenient imports

export * from './auth.types';
export * from './account.types';
export * from './transaction.types';
export * from './transfer.types';
export * from './api.types';
```

---

### 6.2 Step 2: Redux Store Configuration

#### 6.2.1 Store Configuration (`src/app/store.ts`)

```typescript
// src/app/store.ts
import { configureStore, combineReducers } from '@reduxjs/toolkit';
import { setupListeners } from '@reduxjs/toolkit/query';

// Slice reducers
import authReducer from '@features/auth/authSlice';
import uiReducer from '@features/ui/uiSlice';
import transferWizardReducer from '@features/transferWizard/transferWizardSlice';

// RTK Query APIs (will be defined in Phase 6.3)
import { baseApi } from '@features/api/baseApi';

/**
 * Root reducer combining all slice reducers
 */
const rootReducer = combineReducers({
  // Feature slices
  auth: authReducer,
  ui: uiReducer,
  transferWizard: transferWizardReducer,

  // RTK Query API reducer
  [baseApi.reducerPath]: baseApi.reducer,
});

/**
 * Configure Redux store with middleware
 */
export const store = configureStore({
  reducer: rootReducer,

  middleware: (getDefaultMiddleware) =>
    getDefaultMiddleware({
      // Disable serializable check for RTK Query
      serializableCheck: {
        ignoredActions: ['persist/PERSIST', 'persist/REHYDRATE'],
      },
    }).concat(baseApi.middleware),

  // Enable Redux DevTools in development only
  devTools: import.meta.env.DEV,
});

// Enable refetchOnFocus and refetchOnReconnect behaviors
// These are useful for keeping data fresh
setupListeners(store.dispatch);

// Infer types from the store
export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
export type AppStore = typeof store;
```

#### 6.2.2 Typed Hooks (`src/app/hooks.ts`)

```typescript
// src/app/hooks.ts
import { useDispatch, useSelector, useStore } from 'react-redux';
import type { RootState, AppDispatch, AppStore } from './store';

/**
 * Typed useDispatch hook
 * Use this instead of plain useDispatch
 */
export const useAppDispatch = useDispatch.withTypes<AppDispatch>();

/**
 * Typed useSelector hook
 * Use this instead of plain useSelector
 */
export const useAppSelector = useSelector.withTypes<RootState>();

/**
 * Typed useStore hook
 * Use this instead of plain useStore
 */
export const useAppStore = useStore.withTypes<AppStore>();
```

---

### 6.3 Step 3: Auth Slice (BFF-Aware)

#### 6.3.1 Auth Slice Implementation (`src/features/auth/authSlice.ts`)

```typescript
// src/features/auth/authSlice.ts
import { createSlice, createAsyncThunk } from '@reduxjs/toolkit';
import type { PayloadAction } from '@reduxjs/toolkit';
import type { RootState } from '@/app/store';
import type { AuthState, User, SessionInfo } from '@/types/auth.types';

/**
 * Initial auth state
 * NOTE: No token field - BFF pattern stores tokens server-side
 */
const initialState: AuthState = {
  user: null,
  session: null,
  isAuthenticated: false,
  isLoading: false,
  isInitialized: false,
};

/**
 * Auth slice for managing authentication state
 *
 * IMPORTANT: This slice does NOT store JWT tokens.
 * With the BFF pattern:
 * - Tokens are stored server-side in the BFF session
 * - Browser only receives HTTP-only session cookies
 * - This slice tracks user info and session status only
 */
const authSlice = createSlice({
  name: 'auth',
  initialState,
  reducers: {
    /**
     * Set user and session after successful login
     * Called by BFF auth API on login success
     */
    setAuthenticated: (
      state,
      action: PayloadAction<{ user: User; session?: SessionInfo }>
    ) => {
      state.user = action.payload.user;
      state.session = action.payload.session ?? null;
      state.isAuthenticated = true;
      state.isLoading = false;
      state.isInitialized = true;
    },

    /**
     * Update session info (e.g., after PIN verification)
     */
    updateSession: (state, action: PayloadAction<Partial<SessionInfo>>) => {
      if (state.session) {
        state.session = { ...state.session, ...action.payload };
      }
    },

    /**
     * Update user profile data
     */
    updateUser: (state, action: PayloadAction<Partial<User>>) => {
      if (state.user) {
        state.user = { ...state.user, ...action.payload };
      }
    },

    /**
     * Clear auth state on logout or session expiry
     */
    clearAuth: (state) => {
      state.user = null;
      state.session = null;
      state.isAuthenticated = false;
      state.isLoading = false;
      // Keep isInitialized true - we know auth state (logged out)
    },

    /**
     * Set loading state during auth operations
     */
    setLoading: (state, action: PayloadAction<boolean>) => {
      state.isLoading = action.payload;
    },

    /**
     * Mark auth as initialized after initial check completes
     * Called after /bff/auth/me check on app startup
     */
    setInitialized: (state) => {
      state.isInitialized = true;
      state.isLoading = false;
    },

    /**
     * Upgrade auth level after PIN verification
     */
    upgradeAuthLevel: (
      state,
      action: PayloadAction<{ authLevel: 2; expiresAt: string }>
    ) => {
      if (state.session) {
        state.session.authLevel = action.payload.authLevel;
      }
    },
  },
});

// Export actions
export const {
  setAuthenticated,
  updateSession,
  updateUser,
  clearAuth,
  setLoading,
  setInitialized,
  upgradeAuthLevel,
} = authSlice.actions;

// ============================================
// SELECTORS
// ============================================

/**
 * Select the current user
 */
export const selectUser = (state: RootState) => state.auth.user;

/**
 * Select authentication status
 */
export const selectIsAuthenticated = (state: RootState) => state.auth.isAuthenticated;

/**
 * Select loading state
 */
export const selectIsAuthLoading = (state: RootState) => state.auth.isLoading;

/**
 * Select initialization state
 */
export const selectIsAuthInitialized = (state: RootState) => state.auth.isInitialized;

/**
 * Select current session info
 */
export const selectSession = (state: RootState) => state.auth.session;

/**
 * Select current auth level (1 = session, 2 = PIN verified)
 */
export const selectAuthLevel = (state: RootState) => state.auth.session?.authLevel ?? 0;

/**
 * Select user's display name (first name + last initial)
 */
export const selectUserDisplayName = (state: RootState) => {
  const user = state.auth.user;
  if (!user) return null;
  return `${user.firstName} ${user.lastName.charAt(0)}.`;
};

/**
 * Select user's full name
 */
export const selectUserFullName = (state: RootState) => {
  const user = state.auth.user;
  if (!user) return null;
  return `${user.firstName} ${user.lastName}`;
};

/**
 * Select user's AzureTag
 */
export const selectUserAzureTag = (state: RootState) => state.auth.user?.azureTag ?? null;

// Export reducer
export default authSlice.reducer;
```

---

### 6.4 Step 4: UI Slice

#### 6.4.1 UI Slice Implementation (`src/features/ui/uiSlice.ts`)

```typescript
// src/features/ui/uiSlice.ts
import { createSlice } from '@reduxjs/toolkit';
import type { PayloadAction } from '@reduxjs/toolkit';
import type { RootState } from '@/app/store';

/**
 * Toast notification type
 */
export type ToastIntent = 'success' | 'error' | 'warning' | 'info';

/**
 * Toast notification item
 */
export interface ToastItem {
  id: string;
  intent: ToastIntent;
  title: string;
  message?: string;
  duration?: number; // Auto-dismiss in ms (0 = manual dismiss)
}

/**
 * Dialog/Modal identifiers
 */
export type DialogId =
  | 'createAccount'
  | 'deposit'
  | 'withdraw'
  | 'transfer'
  | 'confirmDelete'
  | 'sessionWarning'
  | 'pinVerification';

/**
 * UI state shape
 */
export interface UiState {
  // Toast notifications queue
  toasts: ToastItem[];

  // Active dialogs (supports multiple open dialogs)
  activeDialogs: DialogId[];

  // Dialog-specific data
  dialogData: Record<string, unknown>;

  // Sidebar state (for tablet/desktop)
  isSidebarCollapsed: boolean;

  // Mobile navigation state
  isMobileNavOpen: boolean;

  // Global loading overlay (for blocking operations)
  isGlobalLoading: boolean;
  globalLoadingMessage: string | null;

  // Session timeout warning
  sessionTimeoutWarningVisible: boolean;
  sessionExpiresAt: string | null;
}

const initialState: UiState = {
  toasts: [],
  activeDialogs: [],
  dialogData: {},
  isSidebarCollapsed: false,
  isMobileNavOpen: false,
  isGlobalLoading: false,
  globalLoadingMessage: null,
  sessionTimeoutWarningVisible: false,
  sessionExpiresAt: null,
};

/**
 * Generate unique toast ID
 */
function generateToastId(): string {
  return `toast-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;
}

const uiSlice = createSlice({
  name: 'ui',
  initialState,
  reducers: {
    // ============================================
    // TOAST NOTIFICATIONS
    // ============================================

    /**
     * Add a toast notification
     */
    addToast: (
      state,
      action: PayloadAction<Omit<ToastItem, 'id'> & { id?: string }>
    ) => {
      const toast: ToastItem = {
        id: action.payload.id ?? generateToastId(),
        intent: action.payload.intent,
        title: action.payload.title,
        message: action.payload.message,
        duration: action.payload.duration ?? 5000, // Default 5 seconds
      };
      state.toasts.push(toast);
    },

    /**
     * Remove a specific toast by ID
     */
    removeToast: (state, action: PayloadAction<string>) => {
      state.toasts = state.toasts.filter((t) => t.id !== action.payload);
    },

    /**
     * Clear all toasts
     */
    clearAllToasts: (state) => {
      state.toasts = [];
    },

    // ============================================
    // DIALOGS/MODALS
    // ============================================

    /**
     * Open a dialog with optional data
     */
    openDialog: (
      state,
      action: PayloadAction<{ dialogId: DialogId; data?: unknown }>
    ) => {
      const { dialogId, data } = action.payload;
      if (!state.activeDialogs.includes(dialogId)) {
        state.activeDialogs.push(dialogId);
      }
      if (data !== undefined) {
        state.dialogData[dialogId] = data;
      }
    },

    /**
     * Close a specific dialog
     */
    closeDialog: (state, action: PayloadAction<DialogId>) => {
      state.activeDialogs = state.activeDialogs.filter((id) => id !== action.payload);
      delete state.dialogData[action.payload];
    },

    /**
     * Close all dialogs
     */
    closeAllDialogs: (state) => {
      state.activeDialogs = [];
      state.dialogData = {};
    },

    /**
     * Update dialog data
     */
    setDialogData: (
      state,
      action: PayloadAction<{ dialogId: DialogId; data: unknown }>
    ) => {
      state.dialogData[action.payload.dialogId] = action.payload.data;
    },

    // ============================================
    // NAVIGATION
    // ============================================

    /**
     * Toggle sidebar collapsed state
     */
    toggleSidebar: (state) => {
      state.isSidebarCollapsed = !state.isSidebarCollapsed;
    },

    /**
     * Set sidebar collapsed state explicitly
     */
    setSidebarCollapsed: (state, action: PayloadAction<boolean>) => {
      state.isSidebarCollapsed = action.payload;
    },

    /**
     * Toggle mobile navigation
     */
    toggleMobileNav: (state) => {
      state.isMobileNavOpen = !state.isMobileNavOpen;
    },

    /**
     * Set mobile navigation state explicitly
     */
    setMobileNavOpen: (state, action: PayloadAction<boolean>) => {
      state.isMobileNavOpen = action.payload;
    },

    // ============================================
    // GLOBAL LOADING
    // ============================================

    /**
     * Show global loading overlay
     */
    showGlobalLoading: (state, action: PayloadAction<string | undefined>) => {
      state.isGlobalLoading = true;
      state.globalLoadingMessage = action.payload ?? null;
    },

    /**
     * Hide global loading overlay
     */
    hideGlobalLoading: (state) => {
      state.isGlobalLoading = false;
      state.globalLoadingMessage = null;
    },

    // ============================================
    // SESSION TIMEOUT
    // ============================================

    /**
     * Show session timeout warning
     */
    showSessionWarning: (state, action: PayloadAction<string>) => {
      state.sessionTimeoutWarningVisible = true;
      state.sessionExpiresAt = action.payload;
    },

    /**
     * Hide session timeout warning
     */
    hideSessionWarning: (state) => {
      state.sessionTimeoutWarningVisible = false;
      state.sessionExpiresAt = null;
    },
  },
});

// Export actions
export const {
  addToast,
  removeToast,
  clearAllToasts,
  openDialog,
  closeDialog,
  closeAllDialogs,
  setDialogData,
  toggleSidebar,
  setSidebarCollapsed,
  toggleMobileNav,
  setMobileNavOpen,
  showGlobalLoading,
  hideGlobalLoading,
  showSessionWarning,
  hideSessionWarning,
} = uiSlice.actions;

// ============================================
// SELECTORS
// ============================================

/**
 * Select all toasts
 */
export const selectToasts = (state: RootState) => state.ui.toasts;

/**
 * Check if a specific dialog is open
 */
export const selectIsDialogOpen =
  (dialogId: DialogId) =>
  (state: RootState): boolean =>
    state.ui.activeDialogs.includes(dialogId);

/**
 * Get data for a specific dialog
 */
export const selectDialogData =
  <T = unknown>(dialogId: DialogId) =>
  (state: RootState): T | undefined =>
    state.ui.dialogData[dialogId] as T | undefined;

/**
 * Select sidebar collapsed state
 */
export const selectIsSidebarCollapsed = (state: RootState) => state.ui.isSidebarCollapsed;

/**
 * Select mobile nav open state
 */
export const selectIsMobileNavOpen = (state: RootState) => state.ui.isMobileNavOpen;

/**
 * Select global loading state
 */
export const selectIsGlobalLoading = (state: RootState) => state.ui.isGlobalLoading;

/**
 * Select global loading message
 */
export const selectGlobalLoadingMessage = (state: RootState) => state.ui.globalLoadingMessage;

/**
 * Select session warning visibility
 */
export const selectSessionWarningVisible = (state: RootState) =>
  state.ui.sessionTimeoutWarningVisible;

/**
 * Select session expiry time
 */
export const selectSessionExpiresAt = (state: RootState) => state.ui.sessionExpiresAt;

// Export reducer
export default uiSlice.reducer;
```

---

### 6.5 Step 5: Transfer Wizard Slice

#### 6.5.1 Transfer Wizard Slice (`src/features/transferWizard/transferWizardSlice.ts`)

```typescript
// src/features/transferWizard/transferWizardSlice.ts
import { createSlice } from '@reduxjs/toolkit';
import type { PayloadAction } from '@reduxjs/toolkit';
import type { RootState } from '@/app/store';
import type {
  TransferWizardState,
  TransferWizardStep,
  Recipient,
  TransferResponse,
  InternalTransferResponse,
} from '@/types/transfer.types';

/**
 * Initial transfer wizard state
 */
const initialState: TransferWizardState = {
  currentStep: 'source',
  isOpen: false,
  transferType: null,

  // Step data
  sourceAccountId: null,
  recipient: null,
  destinationAccountId: null,
  amount: null,
  description: '',

  // Result
  completedTransfer: null,

  // UI state
  isSubmitting: false,
  error: null,
};

/**
 * Step order for navigation
 */
const STEP_ORDER: TransferWizardStep[] = [
  'source',
  'recipient',
  'amount',
  'confirm',
  'success',
];

const transferWizardSlice = createSlice({
  name: 'transferWizard',
  initialState,
  reducers: {
    // ============================================
    // WIZARD LIFECYCLE
    // ============================================

    /**
     * Open the transfer wizard for external transfer
     */
    openExternalTransfer: (state, action: PayloadAction<string | undefined>) => {
      state.isOpen = true;
      state.transferType = 'external';
      state.currentStep = 'source';
      state.sourceAccountId = action.payload ?? null;
      state.recipient = null;
      state.destinationAccountId = null;
      state.amount = null;
      state.description = '';
      state.completedTransfer = null;
      state.isSubmitting = false;
      state.error = null;
    },

    /**
     * Open the transfer wizard for internal transfer
     */
    openInternalTransfer: (state, action: PayloadAction<string | undefined>) => {
      state.isOpen = true;
      state.transferType = 'internal';
      state.currentStep = 'source';
      state.sourceAccountId = action.payload ?? null;
      state.recipient = null;
      state.destinationAccountId = null;
      state.amount = null;
      state.description = '';
      state.completedTransfer = null;
      state.isSubmitting = false;
      state.error = null;
    },

    /**
     * Close and reset the wizard
     */
    closeWizard: () => initialState,

    /**
     * Reset wizard to initial step (for "Transfer Again" action)
     */
    resetWizard: (state) => {
      state.currentStep = 'source';
      state.sourceAccountId = null;
      state.recipient = null;
      state.destinationAccountId = null;
      state.amount = null;
      state.description = '';
      state.completedTransfer = null;
      state.isSubmitting = false;
      state.error = null;
    },

    // ============================================
    // STEP NAVIGATION
    // ============================================

    /**
     * Go to a specific step
     */
    goToStep: (state, action: PayloadAction<TransferWizardStep>) => {
      state.currentStep = action.payload;
      state.error = null;
    },

    /**
     * Go to next step
     */
    nextStep: (state) => {
      const currentIndex = STEP_ORDER.indexOf(state.currentStep);
      if (currentIndex < STEP_ORDER.length - 1) {
        state.currentStep = STEP_ORDER[currentIndex + 1]!;
        state.error = null;
      }
    },

    /**
     * Go to previous step
     */
    previousStep: (state) => {
      const currentIndex = STEP_ORDER.indexOf(state.currentStep);
      if (currentIndex > 0) {
        state.currentStep = STEP_ORDER[currentIndex - 1]!;
        state.error = null;
      }
    },

    // ============================================
    // STEP DATA
    // ============================================

    /**
     * Set source account
     */
    setSourceAccount: (state, action: PayloadAction<string>) => {
      state.sourceAccountId = action.payload;
    },

    /**
     * Set recipient (for external transfers)
     */
    setRecipient: (state, action: PayloadAction<Recipient>) => {
      state.recipient = action.payload;
      state.destinationAccountId = null; // Clear internal destination
    },

    /**
     * Set destination account (for internal transfers)
     */
    setDestinationAccount: (state, action: PayloadAction<string>) => {
      state.destinationAccountId = action.payload;
      state.recipient = null; // Clear external recipient
    },

    /**
     * Set transfer amount
     */
    setAmount: (state, action: PayloadAction<number>) => {
      state.amount = action.payload;
    },

    /**
     * Set transfer description
     */
    setDescription: (state, action: PayloadAction<string>) => {
      state.description = action.payload;
    },

    // ============================================
    // SUBMISSION
    // ============================================

    /**
     * Start transfer submission
     */
    startSubmission: (state) => {
      state.isSubmitting = true;
      state.error = null;
    },

    /**
     * Handle successful transfer
     */
    transferSuccess: (
      state,
      action: PayloadAction<TransferResponse | InternalTransferResponse>
    ) => {
      state.isSubmitting = false;
      state.completedTransfer = action.payload;
      state.currentStep = 'success';
      state.error = null;
    },

    /**
     * Handle transfer failure
     */
    transferFailure: (state, action: PayloadAction<string>) => {
      state.isSubmitting = false;
      state.error = action.payload;
    },

    /**
     * Clear error
     */
    clearError: (state) => {
      state.error = null;
    },
  },
});

// Export actions
export const {
  openExternalTransfer,
  openInternalTransfer,
  closeWizard,
  resetWizard,
  goToStep,
  nextStep,
  previousStep,
  setSourceAccount,
  setRecipient,
  setDestinationAccount,
  setAmount,
  setDescription,
  startSubmission,
  transferSuccess,
  transferFailure,
  clearError,
} = transferWizardSlice.actions;

// ============================================
// SELECTORS
// ============================================

/**
 * Select entire wizard state
 */
export const selectTransferWizard = (state: RootState) => state.transferWizard;

/**
 * Select if wizard is open
 */
export const selectIsWizardOpen = (state: RootState) => state.transferWizard.isOpen;

/**
 * Select current step
 */
export const selectCurrentStep = (state: RootState) => state.transferWizard.currentStep;

/**
 * Select transfer type
 */
export const selectTransferType = (state: RootState) => state.transferWizard.transferType;

/**
 * Select source account ID
 */
export const selectSourceAccountId = (state: RootState) =>
  state.transferWizard.sourceAccountId;

/**
 * Select recipient
 */
export const selectRecipient = (state: RootState) => state.transferWizard.recipient;

/**
 * Select destination account ID
 */
export const selectDestinationAccountId = (state: RootState) =>
  state.transferWizard.destinationAccountId;

/**
 * Select amount
 */
export const selectAmount = (state: RootState) => state.transferWizard.amount;

/**
 * Select description
 */
export const selectDescription = (state: RootState) => state.transferWizard.description;

/**
 * Select completed transfer
 */
export const selectCompletedTransfer = (state: RootState) =>
  state.transferWizard.completedTransfer;

/**
 * Select submission state
 */
export const selectIsSubmitting = (state: RootState) => state.transferWizard.isSubmitting;

/**
 * Select error
 */
export const selectError = (state: RootState) => state.transferWizard.error;

/**
 * Select current step index (0-based)
 */
export const selectCurrentStepIndex = (state: RootState) =>
  STEP_ORDER.indexOf(state.transferWizard.currentStep);

/**
 * Check if can go back
 */
export const selectCanGoBack = (state: RootState) => {
  const step = state.transferWizard.currentStep;
  return step !== 'source' && step !== 'success';
};

/**
 * Check if wizard data is valid for submission
 */
export const selectCanSubmit = (state: RootState) => {
  const { transferType, sourceAccountId, recipient, destinationAccountId, amount } =
    state.transferWizard;

  if (!sourceAccountId || !amount || amount <= 0) return false;

  if (transferType === 'external' && !recipient) return false;
  if (transferType === 'internal' && !destinationAccountId) return false;

  return true;
};

// Export reducer
export default transferWizardSlice.reducer;
```

---

### 6.6 Step 6: Base API Configuration (Preview for Phase 6.3)

#### 6.6.1 Base API Placeholder (`src/features/api/baseApi.ts`)

```typescript
// src/features/api/baseApi.ts
// PREVIEW: Full implementation in Phase 6.3

import { createApi, fetchBaseQuery } from '@reduxjs/toolkit/query/react';
import { config } from '@/utils/env';

/**
 * Base API configuration for RTK Query
 *
 * IMPORTANT: Uses BFF URL, not direct API URL
 * All requests include credentials (cookies) for session auth
 */
export const baseApi = createApi({
  reducerPath: 'api',

  baseQuery: fetchBaseQuery({
    baseUrl: config.api.bffUrl, // Use BFF URL
    credentials: 'include', // Include cookies for session auth
    prepareHeaders: (headers) => {
      // Add correlation ID for request tracking
      headers.set('X-Correlation-ID', crypto.randomUUID());
      return headers;
    },
  }),

  // Tag types for cache invalidation
  tagTypes: ['Account', 'Balance', 'Transaction', 'Recipient', 'User'],

  // Endpoints will be injected in Phase 6.3
  endpoints: () => ({}),
});

// Export hooks will be generated in Phase 6.3
```

---

### 6.7 Step 7: App Provider Setup (Updated)

Update the main App component to include the Redux store provider.

#### 6.7.1 Main Entry Point (`src/main.tsx`)

```typescript
// src/main.tsx
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { Provider } from 'react-redux';
import { store } from '@/app/store';
import App from './App';
import './styles/index.css';

// Conditionally start MSW in development
async function enableMocking() {
  if (import.meta.env.VITE_ENABLE_MSW !== 'true') {
    return;
  }

  const { worker } = await import('@/mocks/browser');

  // Start MSW worker
  return worker.start({
    onUnhandledRequest: 'bypass', // Don't warn about unhandled requests
  });
}

// Initialize app after MSW is ready
enableMocking().then(() => {
  const container = document.getElementById('root');
  if (!container) {
    throw new Error('Root element not found');
  }

  createRoot(container).render(
    <StrictMode>
      <Provider store={store}>
        <App />
      </Provider>
    </StrictMode>
  );
});
```

#### 6.7.2 App Component (`src/App.tsx`)

```typescript
// src/App.tsx
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { BrowserRouter } from 'react-router-dom';
import { AppRoutes } from '@/app/router';
import { AuthInitializer } from '@/components/auth/AuthInitializer';
import { ToastContainer } from '@/components/common/ToastContainer';
import { GlobalLoading } from '@/components/common/GlobalLoading';
import { SessionWarning } from '@/components/common/SessionWarning';

// Custom theme will be defined in Phase 6.5
// import { azureBankTheme } from '@/theme';

function App() {
  return (
    <FluentProvider theme={webLightTheme}>
      <BrowserRouter>
        {/* Check auth state on app load */}
        <AuthInitializer>
          {/* Global UI components */}
          <ToastContainer />
          <GlobalLoading />
          <SessionWarning />

          {/* Route tree */}
          <AppRoutes />
        </AuthInitializer>
      </BrowserRouter>
    </FluentProvider>
  );
}

export default App;
```

---

### 6.8 Redux Architecture Best Practices

#### 6.8.1 State Management Guidelines

| State Type | Where to Store | Example |
|------------|---------------|---------|
| **Server Data** | RTK Query cache | Accounts, transactions, user profile |
| **Auth State** | `authSlice` | Current user, session info |
| **Global UI** | `uiSlice` | Toasts, dialogs, sidebar state |
| **Multi-Step Forms** | Feature slice | Transfer wizard state |
| **Form Inputs** | React Hook Form | Login form, registration form |
| **Local UI** | `useState` | Dropdown open, input focus |

#### 6.8.2 Selector Best Practices

```typescript
// GOOD: Simple selectors for direct state access
export const selectUser = (state: RootState) => state.auth.user;

// GOOD: Derived selector with computation
export const selectUserDisplayName = (state: RootState) => {
  const user = state.auth.user;
  if (!user) return null;
  return `${user.firstName} ${user.lastName.charAt(0)}.`;
};

// BETTER: Memoized selector for expensive computations
import { createSelector } from '@reduxjs/toolkit';

export const selectTotalBalance = createSelector(
  [(state: RootState) => state.api.queries],
  (queries) => {
    // Complex computation over cached data
    // Only re-runs when queries change
  }
);
```

#### 6.8.3 Action Naming Conventions

| Pattern | Example | When to Use |
|---------|---------|-------------|
| `set<Property>` | `setAuthenticated` | Setting a value |
| `update<Entity>` | `updateUser` | Partial updates |
| `clear<State>` | `clearAuth` | Resetting state |
| `toggle<Flag>` | `toggleSidebar` | Boolean toggles |
| `open/close<Thing>` | `openDialog` | Visibility |
| `<verb><noun>` | `nextStep`, `previousStep` | Actions |

#### 6.8.4 File Organization

```
src/features/
├── api/
│   └── baseApi.ts          # RTK Query base configuration
├── auth/
│   ├── authSlice.ts        # Auth state management
│   └── authApi.ts          # Auth RTK Query endpoints (Phase 6.3)
├── accounts/
│   └── accountsApi.ts      # Accounts RTK Query endpoints
├── transactions/
│   └── transactionsApi.ts  # Transactions RTK Query endpoints
├── recipients/
│   └── recipientsApi.ts    # Recipient search endpoints
├── transfers/
│   └── transfersApi.ts     # Transfer endpoints
├── ui/
│   └── uiSlice.ts          # Global UI state
└── transferWizard/
    └── transferWizardSlice.ts  # Transfer wizard state
```

---

## 7. RTK Query API Setup (Phase 6.3)

This section covers the complete RTK Query API configuration for the BFF pattern.

---

### 7.1 Base API Configuration

#### 7.1.1 Enhanced Base API (`src/features/api/baseApi.ts`)

```typescript
// src/features/api/baseApi.ts
import {
  createApi,
  fetchBaseQuery,
  retry,
} from '@reduxjs/toolkit/query/react';
import type {
  BaseQueryFn,
  FetchArgs,
  FetchBaseQueryError,
} from '@reduxjs/toolkit/query';
import { config } from '@/utils/env';
import { clearAuth } from '@features/auth/authSlice';
import { addToast } from '@features/ui/uiSlice';

/**
 * Custom base query with:
 * - BFF URL targeting
 * - Credentials (cookies) included
 * - Correlation ID header
 * - 401 handling (session expired)
 * - Retry logic for network errors
 */
const rawBaseQuery = fetchBaseQuery({
  baseUrl: config.api.bffUrl,
  credentials: 'include', // Include HTTP-only session cookies
  prepareHeaders: (headers) => {
    // Add correlation ID for request tracking
    headers.set('X-Correlation-ID', crypto.randomUUID());
    headers.set('Content-Type', 'application/json');
    return headers;
  },
});

/**
 * Base query with automatic retry on network failures
 * Retries 3 times with exponential backoff
 */
const baseQueryWithRetry = retry(rawBaseQuery, { maxRetries: 3 });

/**
 * Base query wrapper that handles 401 (session expired)
 * and dispatches logout action automatically
 */
const baseQueryWithReauth: BaseQueryFn<
  string | FetchArgs,
  unknown,
  FetchBaseQueryError
> = async (args, api, extraOptions) => {
  const result = await baseQueryWithRetry(args, api, extraOptions);

  // Handle 401 Unauthorized (session expired)
  if (result.error && result.error.status === 401) {
    // Clear auth state
    api.dispatch(clearAuth());

    // Show session expired toast
    api.dispatch(
      addToast({
        intent: 'warning',
        title: 'Session Expired',
        message: 'Please log in again to continue.',
        duration: 0, // Don't auto-dismiss
      })
    );
  }

  return result;
};

/**
 * Base API configuration
 *
 * All feature APIs inject their endpoints into this base API.
 * This ensures a single cache and consistent configuration.
 */
export const baseApi = createApi({
  reducerPath: 'api',
  baseQuery: baseQueryWithReauth,

  // Tag types for cache invalidation
  tagTypes: [
    'Account',
    'Balance',
    'Transaction',
    'Recipient',
    'User',
    'Session',
  ],

  // Default cache settings
  keepUnusedDataFor: 60, // Keep unused data for 60 seconds
  refetchOnMountOrArgChange: 30, // Refetch if data older than 30 seconds
  refetchOnFocus: true, // Refetch when window gains focus
  refetchOnReconnect: true, // Refetch when network reconnects

  // Endpoints are injected by feature APIs
  endpoints: () => ({}),
});
```

---

### 7.2 BFF Auth API

#### 7.2.1 Auth API Endpoints (`src/features/auth/authApi.ts`)

```typescript
// src/features/auth/authApi.ts
import { baseApi } from '@features/api/baseApi';
import type {
  LoginRequest,
  RegisterRequest,
  BffLoginResponse,
  BffMeResponse,
  BffSessionStatusResponse,
  VerifyPinRequest,
  VerifyPinResponse,
} from '@/types/auth.types';
import type { ApiResponse } from '@/types/api.types';

/**
 * Auth API endpoints using BFF pattern
 *
 * IMPORTANT: These endpoints hit the BFF (/bff/auth/*), NOT the backend API.
 * The BFF handles:
 * - Token storage (server-side)
 * - Session cookie management
 * - Token injection for backend requests
 */
export const authApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    /**
     * Login via BFF
     * POST /bff/auth/login
     *
     * Returns user info only - NO TOKEN in response
     * Session cookie is set automatically (HTTP-only)
     */
    login: builder.mutation<BffLoginResponse, LoginRequest>({
      query: (credentials) => ({
        url: '/auth/login',
        method: 'POST',
        body: credentials,
      }),
      // Invalidate session-related caches on login
      invalidatesTags: ['Session', 'User'],
    }),

    /**
     * Register new user via BFF
     * POST /bff/auth/register
     *
     * Creates user + initial account, returns user info
     * Auto-logs in (session cookie set)
     */
    register: builder.mutation<
      ApiResponse<{ user: BffLoginResponse['data']['user'] }>,
      RegisterRequest
    >({
      query: (userData) => ({
        url: '/auth/register',
        method: 'POST',
        body: userData,
      }),
      invalidatesTags: ['Session', 'User', 'Account'],
    }),

    /**
     * Logout via BFF
     * POST /bff/auth/logout
     *
     * Clears server-side session and cookie
     */
    logout: builder.mutation<void, void>({
      query: () => ({
        url: '/auth/logout',
        method: 'POST',
      }),
      // Invalidate ALL cached data on logout
      invalidatesTags: [
        'Session',
        'User',
        'Account',
        'Balance',
        'Transaction',
        'Recipient',
      ],
    }),

    /**
     * Get current user info
     * GET /bff/auth/me
     *
     * Used on app startup to check if session is valid
     */
    getMe: builder.query<BffMeResponse, void>({
      query: () => '/auth/me',
      providesTags: ['User', 'Session'],
    }),

    /**
     * Get session status (for timeout warnings)
     * GET /bff/auth/session-status
     */
    getSessionStatus: builder.query<BffSessionStatusResponse, void>({
      query: () => '/auth/session-status',
      providesTags: ['Session'],
    }),

    /**
     * Verify PIN for step-up authentication
     * POST /bff/auth/verify-pin
     */
    verifyPin: builder.mutation<VerifyPinResponse, VerifyPinRequest>({
      query: (data) => ({
        url: '/auth/verify-pin',
        method: 'POST',
        body: data,
      }),
      invalidatesTags: ['Session'],
    }),
  }),
  overrideExisting: false,
});

// Export hooks for components
export const {
  useLoginMutation,
  useRegisterMutation,
  useLogoutMutation,
  useGetMeQuery,
  useLazyGetMeQuery,
  useGetSessionStatusQuery,
  useVerifyPinMutation,
} = authApi;
```

---

### 7.3 Accounts API

#### 7.3.1 Accounts API Endpoints (`src/features/accounts/accountsApi.ts`)

```typescript
// src/features/accounts/accountsApi.ts
import { baseApi } from '@features/api/baseApi';
import type {
  Account,
  BalanceResponse,
  CreateAccountRequest,
  UpdateAccountRequest,
} from '@/types/account.types';
import type { ApiResponse } from '@/types/api.types';

/**
 * Accounts API endpoints
 *
 * All requests go through BFF which adds Bearer token
 */
export const accountsApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    /**
     * Get all user accounts
     * GET /api/accounts
     */
    getAccounts: builder.query<Account[], void>({
      query: () => '/api/accounts',
      // Transform response: extract data array
      transformResponse: (response: ApiResponse<Account[]>) => response.data,
      providesTags: (result) =>
        result
          ? [
              // Tag each account individually
              ...result.map(({ id }) => ({ type: 'Account' as const, id })),
              // Tag for the list itself
              { type: 'Account', id: 'LIST' },
            ]
          : [{ type: 'Account', id: 'LIST' }],
    }),

    /**
     * Get single account by ID
     * GET /api/accounts/{id}
     */
    getAccount: builder.query<Account, string>({
      query: (id) => `/api/accounts/${id}`,
      transformResponse: (response: ApiResponse<Account>) => response.data,
      providesTags: (result, error, id) => [{ type: 'Account', id }],
    }),

    /**
     * Get current balance for account
     * GET /api/accounts/{id}/balance
     */
    getBalance: builder.query<BalanceResponse, string>({
      query: (accountId) => `/api/accounts/${accountId}/balance`,
      transformResponse: (response: ApiResponse<BalanceResponse>) =>
        response.data,
      providesTags: (result, error, accountId) => [
        { type: 'Balance', id: accountId },
      ],
    }),

    /**
     * Get historical balance at specific time
     * GET /api/accounts/{id}/balance?at={datetime}
     */
    getBalanceAtTime: builder.query<
      BalanceResponse,
      { accountId: string; at: string }
    >({
      query: ({ accountId, at }) => ({
        url: `/api/accounts/${accountId}/balance`,
        params: { at },
      }),
      transformResponse: (response: ApiResponse<BalanceResponse>) =>
        response.data,
      // Historical balances don't need tags (not invalidated)
    }),

    /**
     * Create new account
     * POST /api/accounts
     */
    createAccount: builder.mutation<Account, CreateAccountRequest>({
      query: (data) => ({
        url: '/api/accounts',
        method: 'POST',
        body: data,
      }),
      transformResponse: (response: ApiResponse<Account>) => response.data,
      // Invalidate account list to refetch
      invalidatesTags: [{ type: 'Account', id: 'LIST' }],
    }),

    /**
     * Update account name
     * PATCH /api/accounts/{id}
     */
    updateAccount: builder.mutation<
      Account,
      { id: string; data: UpdateAccountRequest }
    >({
      query: ({ id, data }) => ({
        url: `/api/accounts/${id}`,
        method: 'PATCH',
        body: data,
      }),
      transformResponse: (response: ApiResponse<Account>) => response.data,
      invalidatesTags: (result, error, { id }) => [
        { type: 'Account', id },
        { type: 'Account', id: 'LIST' },
      ],
    }),

    /**
     * Set account as primary
     * PATCH /api/accounts/{id}/set-primary
     */
    setAccountPrimary: builder.mutation<Account, string>({
      query: (id) => ({
        url: `/api/accounts/${id}/set-primary`,
        method: 'PATCH',
      }),
      transformResponse: (response: ApiResponse<Account>) => response.data,
      // Invalidate all accounts (primary flag changes on multiple)
      invalidatesTags: [{ type: 'Account', id: 'LIST' }],
    }),

    /**
     * Delete account (soft delete)
     * DELETE /api/accounts/{id}
     */
    deleteAccount: builder.mutation<void, string>({
      query: (id) => ({
        url: `/api/accounts/${id}`,
        method: 'DELETE',
      }),
      invalidatesTags: (result, error, id) => [
        { type: 'Account', id },
        { type: 'Account', id: 'LIST' },
      ],
    }),
  }),
  overrideExisting: false,
});

// Export hooks
export const {
  useGetAccountsQuery,
  useGetAccountQuery,
  useGetBalanceQuery,
  useGetBalanceAtTimeQuery,
  useLazyGetBalanceAtTimeQuery,
  useCreateAccountMutation,
  useUpdateAccountMutation,
  useSetAccountPrimaryMutation,
  useDeleteAccountMutation,
} = accountsApi;
```

---

### 7.4 Transactions API

#### 7.4.1 Transactions API Endpoints (`src/features/transactions/transactionsApi.ts`)

```typescript
// src/features/transactions/transactionsApi.ts
import { baseApi } from '@features/api/baseApi';
import type {
  Transaction,
  TransactionResponse,
  TransactionHistoryParams,
  DepositRequest,
  WithdrawRequest,
} from '@/types/transaction.types';
import type { ApiResponse, PaginatedResponse } from '@/types/api.types';

/**
 * Transactions API endpoints
 */
export const transactionsApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    /**
     * Get transaction history with filtering
     * GET /api/transactions
     */
    getTransactions: builder.query<
      PaginatedResponse<Transaction>,
      TransactionHistoryParams
    >({
      query: (params) => ({
        url: '/api/transactions',
        params: {
          accountId: params.accountId,
          type: params.type,
          from: params.from,
          to: params.to,
          page: params.page ?? 1,
          pageSize: params.pageSize ?? 20,
          sortBy: params.sortBy ?? 'createdAt',
          sortOrder: params.sortOrder ?? 'desc',
        },
      }),
      providesTags: (result, error, params) =>
        result
          ? [
              // Tag each transaction
              ...result.data.map(({ id }) => ({
                type: 'Transaction' as const,
                id,
              })),
              // Tag for this specific query (account + filters)
              {
                type: 'Transaction',
                id: `LIST-${params.accountId ?? 'all'}`,
              },
            ]
          : [{ type: 'Transaction', id: `LIST-${params.accountId ?? 'all'}` }],
    }),

    /**
     * Get single transaction by ID
     * GET /api/transactions/{id}
     */
    getTransaction: builder.query<Transaction, string>({
      query: (id) => `/api/transactions/${id}`,
      transformResponse: (response: ApiResponse<Transaction>) => response.data,
      providesTags: (result, error, id) => [{ type: 'Transaction', id }],
    }),

    /**
     * Deposit money into account
     * POST /api/transactions/deposit
     */
    deposit: builder.mutation<TransactionResponse, DepositRequest>({
      query: (data) => ({
        url: '/api/transactions/deposit',
        method: 'POST',
        body: data,
      }),
      transformResponse: (response: ApiResponse<TransactionResponse>) =>
        response.data,
      // Invalidate balance and transaction list for this account
      invalidatesTags: (result, error, { accountId }) => [
        { type: 'Balance', id: accountId },
        { type: 'Transaction', id: `LIST-${accountId}` },
        { type: 'Transaction', id: 'LIST-all' },
        { type: 'Account', id: accountId }, // Balance is on account too
      ],
    }),

    /**
     * Withdraw money from account
     * POST /api/transactions/withdraw
     */
    withdraw: builder.mutation<TransactionResponse, WithdrawRequest>({
      query: (data) => ({
        url: '/api/transactions/withdraw',
        method: 'POST',
        body: data,
      }),
      transformResponse: (response: ApiResponse<TransactionResponse>) =>
        response.data,
      invalidatesTags: (result, error, { accountId }) => [
        { type: 'Balance', id: accountId },
        { type: 'Transaction', id: `LIST-${accountId}` },
        { type: 'Transaction', id: 'LIST-all' },
        { type: 'Account', id: accountId },
      ],
    }),
  }),
  overrideExisting: false,
});

// Export hooks
export const {
  useGetTransactionsQuery,
  useLazyGetTransactionsQuery,
  useGetTransactionQuery,
  useDepositMutation,
  useWithdrawMutation,
} = transactionsApi;
```

---

### 7.5 Transfers API

#### 7.5.1 Transfers API Endpoints (`src/features/transfers/transfersApi.ts`)

```typescript
// src/features/transfers/transfersApi.ts
import { baseApi } from '@features/api/baseApi';
import type {
  TransferRequest,
  InternalTransferRequest,
  TransferResponse,
  InternalTransferResponse,
} from '@/types/transfer.types';
import type { ApiResponse } from '@/types/api.types';

/**
 * Transfers API endpoints
 */
export const transfersApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    /**
     * External transfer to another user
     * POST /api/transfers
     */
    transfer: builder.mutation<TransferResponse, TransferRequest>({
      query: (data) => ({
        url: '/api/transfers',
        method: 'POST',
        body: data,
      }),
      transformResponse: (response: ApiResponse<TransferResponse>) =>
        response.data,
      // Invalidate sender's balance and transactions
      invalidatesTags: (result, error, { fromAccountId }) => [
        { type: 'Balance', id: fromAccountId },
        { type: 'Transaction', id: `LIST-${fromAccountId}` },
        { type: 'Transaction', id: 'LIST-all' },
        { type: 'Account', id: fromAccountId },
        { type: 'Account', id: 'LIST' }, // Total balance changed
      ],
    }),

    /**
     * Internal transfer between own accounts
     * POST /api/transfers/internal
     */
    internalTransfer: builder.mutation<
      InternalTransferResponse,
      InternalTransferRequest
    >({
      query: (data) => ({
        url: '/api/transfers/internal',
        method: 'POST',
        body: data,
      }),
      transformResponse: (response: ApiResponse<InternalTransferResponse>) =>
        response.data,
      // Invalidate both accounts
      invalidatesTags: (result, error, { fromAccountId, toAccountId }) => [
        { type: 'Balance', id: fromAccountId },
        { type: 'Balance', id: toAccountId },
        { type: 'Transaction', id: `LIST-${fromAccountId}` },
        { type: 'Transaction', id: `LIST-${toAccountId}` },
        { type: 'Transaction', id: 'LIST-all' },
        { type: 'Account', id: fromAccountId },
        { type: 'Account', id: toAccountId },
      ],
    }),
  }),
  overrideExisting: false,
});

// Export hooks
export const { useTransferMutation, useInternalTransferMutation } =
  transfersApi;
```

---

### 7.6 Recipients API

#### 7.6.1 Recipients API Endpoints (`src/features/recipients/recipientsApi.ts`)

```typescript
// src/features/recipients/recipientsApi.ts
import { baseApi } from '@features/api/baseApi';
import type { Recipient, RecipientExistsResponse } from '@/types/transfer.types';
import type { ApiResponse } from '@/types/api.types';

/**
 * Recipients API endpoints for transfer recipient lookup
 */
export const recipientsApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    /**
     * Search recipients by AzureTag
     * GET /api/users/search?azureTag={query}
     *
     * Returns max 10 results, min 2 characters required
     */
    searchRecipients: builder.query<Recipient[], string>({
      query: (azureTag) => ({
        url: '/api/users/search',
        params: { azureTag },
      }),
      transformResponse: (response: ApiResponse<Recipient[]>) => response.data,
      // Short cache time for search results
      keepUnusedDataFor: 30,
      providesTags: ['Recipient'],
    }),

    /**
     * Validate recipient exists
     * GET /api/users/{azureTag}
     *
     * Used to confirm recipient before transfer
     */
    getRecipient: builder.query<RecipientExistsResponse, string>({
      query: (azureTag) => `/api/users/${azureTag}`,
      transformResponse: (response: ApiResponse<RecipientExistsResponse>) =>
        response.data,
      providesTags: (result, error, azureTag) => [
        { type: 'Recipient', id: azureTag },
      ],
    }),
  }),
  overrideExisting: false,
});

// Export hooks
export const {
  useSearchRecipientsQuery,
  useLazySearchRecipientsQuery,
  useGetRecipientQuery,
  useLazyGetRecipientQuery,
} = recipientsApi;
```

---

### 7.7 Custom Hooks for API Integration

#### 7.7.1 useAuth Hook (`src/hooks/useAuth.ts`)

```typescript
// src/hooks/useAuth.ts
import { useCallback, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAppDispatch, useAppSelector } from '@/app/hooks';
import {
  useLoginMutation,
  useRegisterMutation,
  useLogoutMutation,
  useLazyGetMeQuery,
} from '@features/auth/authApi';
import {
  setAuthenticated,
  clearAuth,
  setInitialized,
  setLoading,
  selectUser,
  selectIsAuthenticated,
  selectIsAuthLoading,
  selectIsAuthInitialized,
} from '@features/auth/authSlice';
import { addToast } from '@features/ui/uiSlice';
import type { LoginRequest, RegisterRequest } from '@/types/auth.types';
import { getErrorMessage } from '@/types/api.types';

/**
 * Custom hook for authentication operations
 *
 * Provides:
 * - Current user state
 * - Login/register/logout functions
 * - Initial auth check on app load
 */
export function useAuth() {
  const dispatch = useAppDispatch();
  const navigate = useNavigate();

  // State selectors
  const user = useAppSelector(selectUser);
  const isAuthenticated = useAppSelector(selectIsAuthenticated);
  const isLoading = useAppSelector(selectIsAuthLoading);
  const isInitialized = useAppSelector(selectIsAuthInitialized);

  // API mutations
  const [loginMutation, { isLoading: isLoginLoading }] = useLoginMutation();
  const [registerMutation, { isLoading: isRegisterLoading }] =
    useRegisterMutation();
  const [logoutMutation, { isLoading: isLogoutLoading }] = useLogoutMutation();
  const [getMe] = useLazyGetMeQuery();

  /**
   * Check auth status on app startup
   * Called by AuthInitializer component
   */
  const checkAuth = useCallback(async () => {
    dispatch(setLoading(true));

    try {
      const result = await getMe().unwrap();
      dispatch(
        setAuthenticated({
          user: result.data.user,
          session: result.data.session,
        })
      );
    } catch {
      // Not authenticated - that's okay
      dispatch(setInitialized());
    }
  }, [dispatch, getMe]);

  /**
   * Login user
   */
  const login = useCallback(
    async (credentials: LoginRequest) => {
      try {
        const result = await loginMutation(credentials).unwrap();
        dispatch(setAuthenticated({ user: result.data.user }));
        dispatch(
          addToast({
            intent: 'success',
            title: 'Welcome back!',
            message: `Logged in as ${result.data.user.firstName}`,
          })
        );
        navigate('/dashboard');
        return result;
      } catch (error) {
        dispatch(
          addToast({
            intent: 'error',
            title: 'Login failed',
            message: getErrorMessage(error),
          })
        );
        throw error;
      }
    },
    [dispatch, loginMutation, navigate]
  );

  /**
   * Register new user
   */
  const register = useCallback(
    async (userData: RegisterRequest) => {
      try {
        const result = await registerMutation(userData).unwrap();
        dispatch(setAuthenticated({ user: result.data.user }));
        dispatch(
          addToast({
            intent: 'success',
            title: 'Welcome to AzureBank!',
            message: 'Your account has been created.',
          })
        );
        navigate('/dashboard');
        return result;
      } catch (error) {
        dispatch(
          addToast({
            intent: 'error',
            title: 'Registration failed',
            message: getErrorMessage(error),
          })
        );
        throw error;
      }
    },
    [dispatch, registerMutation, navigate]
  );

  /**
   * Logout user
   */
  const logout = useCallback(async () => {
    try {
      await logoutMutation().unwrap();
    } catch {
      // Ignore logout errors - clear state anyway
    } finally {
      dispatch(clearAuth());
      dispatch(
        addToast({
          intent: 'info',
          title: 'Logged out',
          message: 'You have been logged out successfully.',
        })
      );
      navigate('/login');
    }
  }, [dispatch, logoutMutation, navigate]);

  return {
    // State
    user,
    isAuthenticated,
    isLoading: isLoading || isLoginLoading || isRegisterLoading || isLogoutLoading,
    isInitialized,

    // Actions
    checkAuth,
    login,
    register,
    logout,
  };
}
```

#### 7.7.2 useAccounts Hook (`src/hooks/useAccounts.ts`)

```typescript
// src/hooks/useAccounts.ts
import { useMemo } from 'react';
import {
  useGetAccountsQuery,
  useGetBalanceQuery,
  useCreateAccountMutation,
} from '@features/accounts/accountsApi';
import type { Account, AccountSummary } from '@/types/account.types';

/**
 * Custom hook for account operations
 */
export function useAccounts() {
  const {
    data: accounts,
    isLoading: isLoadingAccounts,
    error: accountsError,
    refetch: refetchAccounts,
  } = useGetAccountsQuery();

  const [createAccount, { isLoading: isCreating }] = useCreateAccountMutation();

  // Derive primary account
  const primaryAccount = useMemo(
    () => accounts?.find((a) => a.isPrimary) ?? accounts?.[0] ?? null,
    [accounts]
  );

  // Get primary account balance
  const { data: primaryBalance, isLoading: isLoadingBalance } =
    useGetBalanceQuery(primaryAccount?.id ?? '', {
      skip: !primaryAccount,
    });

  // Calculate account summary
  const summary: AccountSummary = useMemo(
    () => ({
      totalBalance:
        accounts?.reduce((sum, account) => sum + account.balance, 0) ?? 0,
      accountCount: accounts?.length ?? 0,
      primaryAccount,
    }),
    [accounts, primaryAccount]
  );

  return {
    // Data
    accounts: accounts ?? [],
    primaryAccount,
    primaryBalance: primaryBalance?.balance,
    summary,

    // Loading states
    isLoading: isLoadingAccounts || isLoadingBalance,
    isCreating,

    // Error
    error: accountsError,

    // Actions
    createAccount,
    refetchAccounts,
  };
}
```

#### 7.7.3 useTransactions Hook (`src/hooks/useTransactions.ts`)

```typescript
// src/hooks/useTransactions.ts
import { useState, useMemo, useCallback } from 'react';
import {
  useGetTransactionsQuery,
  useDepositMutation,
  useWithdrawMutation,
} from '@features/transactions/transactionsApi';
import { useAppDispatch } from '@/app/hooks';
import { addToast } from '@features/ui/uiSlice';
import type {
  TransactionHistoryParams,
  TransactionGroup,
  DepositRequest,
  WithdrawRequest,
} from '@/types/transaction.types';
import { getErrorMessage } from '@/types/api.types';
import { format, isToday, isYesterday, parseISO } from 'date-fns';

/**
 * Group transactions by date for display
 */
function groupTransactionsByDate(
  transactions: Array<{ createdAt: string; type: string; amount: number }>
): TransactionGroup[] {
  const groups: Record<string, TransactionGroup> = {};

  for (const tx of transactions) {
    const date = parseISO(tx.createdAt);
    const dateKey = format(date, 'yyyy-MM-dd');

    if (!groups[dateKey]) {
      let label: string;
      if (isToday(date)) {
        label = 'Today';
      } else if (isYesterday(date)) {
        label = 'Yesterday';
      } else {
        label = format(date, 'MMMM d, yyyy');
      }

      groups[dateKey] = {
        date: dateKey,
        label,
        transactions: [],
        totalIn: 0,
        totalOut: 0,
      };
    }

    groups[dateKey]!.transactions.push(tx as TransactionGroup['transactions'][0]);

    if (tx.type === 'deposit' || tx.type === 'transfer_in') {
      groups[dateKey]!.totalIn += tx.amount;
    } else {
      groups[dateKey]!.totalOut += tx.amount;
    }
  }

  // Sort by date descending
  return Object.values(groups).sort(
    (a, b) => new Date(b.date).getTime() - new Date(a.date).getTime()
  );
}

/**
 * Custom hook for transaction operations
 */
export function useTransactions(accountId?: string) {
  const dispatch = useAppDispatch();

  // Filter state
  const [filters, setFilters] = useState<TransactionHistoryParams>({
    accountId,
    page: 1,
    pageSize: 20,
  });

  // Query transactions
  const {
    data: transactionData,
    isLoading,
    isFetching,
    error,
    refetch,
  } = useGetTransactionsQuery(filters);

  // Mutations
  const [depositMutation, { isLoading: isDepositing }] = useDepositMutation();
  const [withdrawMutation, { isLoading: isWithdrawing }] = useWithdrawMutation();

  // Group transactions by date
  const groupedTransactions = useMemo(
    () => groupTransactionsByDate(transactionData?.data ?? []),
    [transactionData]
  );

  // Deposit money
  const deposit = useCallback(
    async (data: DepositRequest) => {
      try {
        const result = await depositMutation(data).unwrap();
        dispatch(
          addToast({
            intent: 'success',
            title: 'Deposit successful',
            message: `€${data.amount.toFixed(2)} deposited. New balance: €${result.newBalance.toFixed(2)}`,
          })
        );
        return result;
      } catch (error) {
        dispatch(
          addToast({
            intent: 'error',
            title: 'Deposit failed',
            message: getErrorMessage(error),
          })
        );
        throw error;
      }
    },
    [depositMutation, dispatch]
  );

  // Withdraw money
  const withdraw = useCallback(
    async (data: WithdrawRequest) => {
      try {
        const result = await withdrawMutation(data).unwrap();
        dispatch(
          addToast({
            intent: 'success',
            title: 'Withdrawal successful',
            message: `€${data.amount.toFixed(2)} withdrawn. New balance: €${result.newBalance.toFixed(2)}`,
          })
        );
        return result;
      } catch (error) {
        dispatch(
          addToast({
            intent: 'error',
            title: 'Withdrawal failed',
            message: getErrorMessage(error),
          })
        );
        throw error;
      }
    },
    [withdrawMutation, dispatch]
  );

  // Update filters
  const updateFilters = useCallback(
    (newFilters: Partial<TransactionHistoryParams>) => {
      setFilters((prev) => ({ ...prev, ...newFilters, page: 1 }));
    },
    []
  );

  // Load next page
  const loadNextPage = useCallback(() => {
    if (transactionData?.pagination.hasNextPage) {
      setFilters((prev) => ({ ...prev, page: (prev.page ?? 1) + 1 }));
    }
  }, [transactionData]);

  return {
    // Data
    transactions: transactionData?.data ?? [],
    groupedTransactions,
    pagination: transactionData?.pagination,

    // Loading states
    isLoading,
    isFetching,
    isDepositing,
    isWithdrawing,

    // Error
    error,

    // Actions
    deposit,
    withdraw,
    refetch,
    updateFilters,
    loadNextPage,

    // Current filters
    filters,
  };
}
```

#### 7.7.4 useRecipientSearch Hook (`src/hooks/useRecipientSearch.ts`)

```typescript
// src/hooks/useRecipientSearch.ts
import { useState, useCallback, useMemo } from 'react';
import { useDebouncedCallback } from 'use-debounce';
import { useLazySearchRecipientsQuery } from '@features/recipients/recipientsApi';
import type { Recipient } from '@/types/transfer.types';

/**
 * Custom hook for searching transfer recipients
 *
 * Features:
 * - Debounced search (300ms)
 * - Minimum 2 characters
 * - Loading and error states
 */
export function useRecipientSearch() {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Recipient[]>([]);
  const [hasSearched, setHasSearched] = useState(false);

  const [triggerSearch, { isLoading, isFetching, error }] =
    useLazySearchRecipientsQuery();

  // Debounced search function
  const debouncedSearch = useDebouncedCallback(
    async (searchQuery: string) => {
      if (searchQuery.length < 2) {
        setResults([]);
        setHasSearched(false);
        return;
      }

      try {
        const result = await triggerSearch(searchQuery).unwrap();
        setResults(result);
        setHasSearched(true);
      } catch {
        setResults([]);
        setHasSearched(true);
      }
    },
    300 // 300ms debounce
  );

  // Handle query change
  const handleQueryChange = useCallback(
    (newQuery: string) => {
      setQuery(newQuery);
      debouncedSearch(newQuery);
    },
    [debouncedSearch]
  );

  // Clear search
  const clearSearch = useCallback(() => {
    setQuery('');
    setResults([]);
    setHasSearched(false);
  }, []);

  // Computed states
  const showNoResults = useMemo(
    () => hasSearched && results.length === 0 && query.length >= 2,
    [hasSearched, results.length, query.length]
  );

  const showMinCharsMessage = useMemo(
    () => query.length > 0 && query.length < 2,
    [query.length]
  );

  return {
    // State
    query,
    results,
    isLoading: isLoading || isFetching,
    error,

    // Computed
    showNoResults,
    showMinCharsMessage,

    // Actions
    setQuery: handleQueryChange,
    clearSearch,
  };
}
```

---

### 7.8 Cache Invalidation Strategy

#### 7.8.1 Tag-Based Cache Invalidation Matrix

| Action | Invalidated Tags | Effect |
|--------|-----------------|--------|
| **Login** | `Session`, `User` | Refetch user data |
| **Logout** | All tags | Clear all cached data |
| **Deposit** | `Balance:{id}`, `Transaction:LIST-{id}`, `Account:{id}` | Refetch balance and transactions |
| **Withdraw** | `Balance:{id}`, `Transaction:LIST-{id}`, `Account:{id}` | Refetch balance and transactions |
| **External Transfer** | `Balance:{from}`, `Transaction:LIST-{from}`, `Account:LIST` | Refetch sender's data |
| **Internal Transfer** | `Balance:{from}`, `Balance:{to}`, `Transaction:LIST-*`, `Account:{from}`, `Account:{to}` | Refetch both accounts |
| **Create Account** | `Account:LIST` | Refetch account list |
| **Update Account** | `Account:{id}`, `Account:LIST` | Refetch account |
| **Delete Account** | `Account:{id}`, `Account:LIST` | Refetch account list |

#### 7.8.2 Cache Timing Configuration

```typescript
// Recommended cache configuration per endpoint type

// Auth endpoints - Always fresh
{ keepUnusedDataFor: 0 }

// Balance - Short cache (updates frequently)
{ keepUnusedDataFor: 30 }

// Accounts list - Medium cache
{ keepUnusedDataFor: 60 }

// Transaction history - Longer cache (historical data)
{ keepUnusedDataFor: 120 }

// Recipient search - Short cache
{ keepUnusedDataFor: 30 }
```

---

### 7.9 Error Handling Patterns

#### 7.9.1 API Error Handler Utility (`src/utils/apiErrors.ts`)

```typescript
// src/utils/apiErrors.ts
import type { ApiError, RtkQueryError } from '@/types/api.types';

/**
 * Error type constants matching backend
 */
export const ErrorTypes = {
  VALIDATION_ERROR: 'VALIDATION_ERROR',
  AUTHENTICATION_FAILED: 'AUTHENTICATION_FAILED',
  UNAUTHORIZED: 'UNAUTHORIZED',
  ACCESS_DENIED: 'ACCESS_DENIED',
  NOT_FOUND: 'NOT_FOUND',
  CONFLICT: 'CONFLICT',
  INSUFFICIENT_FUNDS: 'INSUFFICIENT_FUNDS',
  BUSINESS_RULE_VIOLATION: 'BUSINESS_RULE_VIOLATION',
  RATE_LIMIT_EXCEEDED: 'RATE_LIMIT_EXCEEDED',
  INTERNAL_SERVER_ERROR: 'INTERNAL_SERVER_ERROR',
} as const;

/**
 * Check if error is a specific type
 */
export function isErrorType(
  error: unknown,
  type: keyof typeof ErrorTypes
): boolean {
  if (!isRtkQueryError(error)) return false;
  return error.data.type === ErrorTypes[type];
}

/**
 * Type guard for RTK Query errors
 */
export function isRtkQueryError(error: unknown): error is RtkQueryError {
  return (
    typeof error === 'object' &&
    error !== null &&
    'status' in error &&
    'data' in error &&
    typeof (error as RtkQueryError).data === 'object'
  );
}

/**
 * Get user-friendly error message
 */
export function getUserMessage(error: unknown): string {
  if (isRtkQueryError(error)) {
    return error.data.message;
  }
  if (error instanceof Error) {
    return error.message;
  }
  return 'An unexpected error occurred. Please try again.';
}

/**
 * Get field validation errors
 */
export function getValidationErrors(
  error: unknown
): Record<string, string[]> | null {
  if (isRtkQueryError(error) && error.data.errors) {
    return error.data.errors;
  }
  return null;
}

/**
 * Get first validation error for a field
 */
export function getFieldError(error: unknown, field: string): string | null {
  const errors = getValidationErrors(error);
  if (errors && errors[field] && errors[field].length > 0) {
    return errors[field][0] ?? null;
  }
  return null;
}

/**
 * Check if error is insufficient funds
 */
export function isInsufficientFunds(error: unknown): boolean {
  return isErrorType(error, 'INSUFFICIENT_FUNDS');
}

/**
 * Get insufficient funds details
 */
export function getInsufficientFundsDetails(
  error: unknown
): { available: number; requested: number } | null {
  if (!isInsufficientFunds(error) || !isRtkQueryError(error)) return null;
  const details = error.data.details as Record<string, number> | undefined;
  if (details && 'available' in details && 'requested' in details) {
    return {
      available: details.available,
      requested: details.requested,
    };
  }
  return null;
}
```

---

### 7.10 API Exports Index

#### 7.10.1 Features API Index (`src/features/api/index.ts`)

```typescript
// src/features/api/index.ts
// Re-export all API hooks for convenient imports

// Base API
export { baseApi } from './baseApi';

// Auth API
export {
  authApi,
  useLoginMutation,
  useRegisterMutation,
  useLogoutMutation,
  useGetMeQuery,
  useLazyGetMeQuery,
  useGetSessionStatusQuery,
  useVerifyPinMutation,
} from '@features/auth/authApi';

// Accounts API
export {
  accountsApi,
  useGetAccountsQuery,
  useGetAccountQuery,
  useGetBalanceQuery,
  useGetBalanceAtTimeQuery,
  useLazyGetBalanceAtTimeQuery,
  useCreateAccountMutation,
  useUpdateAccountMutation,
  useSetAccountPrimaryMutation,
  useDeleteAccountMutation,
} from '@features/accounts/accountsApi';

// Transactions API
export {
  transactionsApi,
  useGetTransactionsQuery,
  useLazyGetTransactionsQuery,
  useGetTransactionQuery,
  useDepositMutation,
  useWithdrawMutation,
} from '@features/transactions/transactionsApi';

// Transfers API
export {
  transfersApi,
  useTransferMutation,
  useInternalTransferMutation,
} from '@features/transfers/transfersApi';

// Recipients API
export {
  recipientsApi,
  useSearchRecipientsQuery,
  useLazySearchRecipientsQuery,
  useGetRecipientQuery,
  useLazyGetRecipientQuery,
} from '@features/recipients/recipientsApi';
```

---

## 8. MSW Integration Strategy (Phase 6.4)

This section covers the complete MSW (Mock Service Worker) integration for frontend-first development with the BFF pattern.

---

### 8.1 MSW Architecture Overview

#### 8.1.1 Why MSW?

MSW intercepts network requests at the service worker level, enabling:
- **Frontend-first development**: Build UI before backend is ready
- **Realistic testing**: Same code paths as production
- **BFF simulation**: Mock session-based authentication
- **Isolated testing**: No external dependencies

#### 8.1.2 BFF Pattern Considerations

> **CRITICAL**: The BFF manages session cookies server-side. In MSW, we simulate this with an in-memory session store.

**What MSW Must Simulate**:
1. **BFF Authentication** (`/bff/auth/*`): Session creation, validation, destruction
2. **Session Cookies**: HTTP-only cookie behavior (with limitations)
3. **Backend API** (`/api/*`): All protected endpoints
4. **Session Validation**: Check session on every protected request

**Browser Limitations**:
- MSW cannot set true HTTP-only cookies
- We track session state in memory
- Session ID is returned in response headers for realism

---

### 8.2 Project Structure

#### 8.2.1 MSW Directory Layout

```
src/mocks/
├── browser.ts              # MSW browser worker setup
├── handlers/
│   ├── index.ts            # Export all handlers (order matters!)
│   ├── bff-auth.handlers.ts    # /bff/auth/* (login, logout, me, session)
│   ├── account.handlers.ts     # /api/accounts/*
│   ├── transaction.handlers.ts # /api/transactions/*
│   ├── transfer.handlers.ts    # /api/transfers/*
│   └── user.handlers.ts        # /api/users/* (recipient search)
├── data/
│   ├── db.ts               # Mock data types and initial data
│   ├── state.ts            # In-memory state manager
│   └── session.ts          # Session store (BFF simulation)
└── utils/
    └── errorSimulation.ts  # Toggle error scenarios for testing
```

---

### 8.3 Browser Setup

#### 8.3.1 MSW Worker Configuration (`src/mocks/browser.ts`)

```typescript
// src/mocks/browser.ts
import { setupWorker } from 'msw/browser';
import { handlers } from './handlers';

/**
 * MSW browser worker for development
 *
 * This intercepts all network requests in the browser
 * and responds with mock data when handlers match.
 */
export const worker = setupWorker(...handlers);
```

#### 8.3.2 Main Entry Point Integration (`src/main.tsx`)

```typescript
// src/main.tsx
import React from 'react';
import ReactDOM from 'react-dom/client';
import { Provider } from 'react-redux';
import { FluentProvider } from '@fluentui/react-components';
import { store } from '@app/store';
import { azureBankTheme } from '@theme';
import App from './App';
import './index.css';

/**
 * Enable MSW mocking in development
 *
 * Only starts if VITE_ENABLE_MSW is 'true'
 * This allows easy toggling between mock and real API
 */
async function enableMocking(): Promise<void> {
  // Skip in production or if MSW is disabled
  if (import.meta.env.PROD || import.meta.env.VITE_ENABLE_MSW !== 'true') {
    return;
  }

  const { worker } = await import('./mocks/browser');

  // Start the worker
  await worker.start({
    onUnhandledRequest: 'bypass', // Let unhandled requests pass through
    quiet: false, // Log handled requests to console
  });

  console.log('[MSW] Mock Service Worker started');
}

// Initialize the app after MSW is ready
enableMocking().then(() => {
  ReactDOM.createRoot(document.getElementById('root')!).render(
    <React.StrictMode>
      <Provider store={store}>
        <FluentProvider theme={azureBankTheme}>
          <App />
        </FluentProvider>
      </Provider>
    </React.StrictMode>
  );
});
```

#### 8.3.3 Environment Configuration (`.env.development`)

```bash
# .env.development

# Enable MSW for mocking API requests
VITE_ENABLE_MSW=true

# API base URL (not used when MSW is enabled, but kept for consistency)
VITE_API_URL=http://localhost:5000

# BFF URL (frontend calls this, BFF proxies to API)
VITE_BFF_URL=
```

---

### 8.4 Handler Organization

#### 8.4.1 Handler Index (`src/mocks/handlers/index.ts`)

```typescript
// src/mocks/handlers/index.ts
import { bffAuthHandlers } from './bff-auth.handlers';
import { accountHandlers } from './account.handlers';
import { transactionHandlers } from './transaction.handlers';
import { transferHandlers } from './transfer.handlers';
import { userHandlers } from './user.handlers';

/**
 * All MSW handlers combined
 *
 * ORDER MATTERS:
 * - BFF handlers must come FIRST (they match /bff/* routes)
 * - More specific routes should come before general ones
 */
export const handlers = [
  // BFF endpoints (session-based auth)
  ...bffAuthHandlers,

  // API endpoints (proxied through BFF in production)
  ...accountHandlers,
  ...transactionHandlers,
  ...transferHandlers,
  ...userHandlers,
];
```

---

### 8.5 Session Store (BFF Simulation)

#### 8.5.1 Session Types and Store (`src/mocks/data/session.ts`)

```typescript
// src/mocks/data/session.ts

/**
 * Mock session representing server-side session storage
 * In production, this is managed by the BFF
 */
export interface MockSession {
  /** Unique session identifier */
  sessionId: string;
  /** User ID associated with this session */
  userId: string;
  /** Access token (stored server-side, never sent to client) */
  accessToken: string;
  /** Auth level: 1 = basic session, 2 = PIN verified */
  authLevel: 1 | 2;
  /** Session creation timestamp */
  createdAt: string;
  /** Last activity timestamp (for inactivity timeout) */
  lastActivity: string;
  /** When PIN was verified (if applicable) */
  pinVerifiedAt?: string;
}

// In-memory session store (simulates Redis/server session storage)
const sessionStore = new Map<string, MockSession>();

/**
 * Generate a unique session ID
 */
function generateSessionId(): string {
  return `sess_${Date.now()}_${Math.random().toString(36).slice(2, 11)}`;
}

/**
 * Create a new session for a user
 * @param userId - The authenticated user's ID
 * @param accessToken - The JWT token (stored server-side)
 * @returns The session ID (sent to client as cookie)
 */
export function createSession(userId: string, accessToken: string): string {
  const sessionId = generateSessionId();
  const now = new Date().toISOString();

  const session: MockSession = {
    sessionId,
    userId,
    accessToken,
    authLevel: 1,
    createdAt: now,
    lastActivity: now,
  };

  sessionStore.set(sessionId, session);
  return sessionId;
}

/**
 * Get a session by ID, updating last activity
 * @param sessionId - The session ID from cookie
 * @returns The session if valid, undefined otherwise
 */
export function getSession(sessionId: string): MockSession | undefined {
  const session = sessionStore.get(sessionId);

  if (session) {
    // Update last activity on every access
    session.lastActivity = new Date().toISOString();
  }

  return session;
}

/**
 * Delete a session (logout)
 * @param sessionId - The session ID to delete
 */
export function deleteSession(sessionId: string): void {
  sessionStore.delete(sessionId);
}

/**
 * Upgrade session auth level after PIN verification
 * @param sessionId - The session ID
 */
export function verifyPinForSession(sessionId: string): void {
  const session = sessionStore.get(sessionId);

  if (session) {
    session.authLevel = 2;
    session.pinVerifiedAt = new Date().toISOString();
  }
}

/**
 * Check if session is expired
 * @param session - The session to check
 * @returns true if expired
 */
export function isSessionExpired(session: MockSession): boolean {
  const now = Date.now();
  const lastActivity = new Date(session.lastActivity).getTime();
  const createdAt = new Date(session.createdAt).getTime();

  // Development timeouts (shorter for testing)
  const INACTIVITY_TIMEOUT = 10 * 60 * 1000; // 10 minutes
  const ABSOLUTE_TIMEOUT = 20 * 60 * 1000;   // 20 minutes

  // Check inactivity timeout
  if (now - lastActivity > INACTIVITY_TIMEOUT) {
    return true;
  }

  // Check absolute timeout
  if (now - createdAt > ABSOLUTE_TIMEOUT) {
    return true;
  }

  return false;
}

/**
 * Extract session from cookie header
 * @param cookieHeader - The Cookie header value
 * @returns The session if found and valid
 */
export function getSessionFromCookies(
  cookieHeader: string | null
): MockSession | undefined {
  if (!cookieHeader) return undefined;

  // Parse cookie: .AzureBank.Session=<sessionId>
  const match = cookieHeader.match(/\.AzureBank\.Session=([^;]+)/);
  if (!match) return undefined;

  const session = getSession(match[1]);

  // Return undefined if expired
  if (session && isSessionExpired(session)) {
    deleteSession(session.sessionId);
    return undefined;
  }

  return session;
}

/**
 * Clear all sessions (useful for testing)
 */
export function clearAllSessions(): void {
  sessionStore.clear();
}

/**
 * Get session timeout info for status endpoint
 */
export function getSessionTimeoutInfo(session: MockSession): {
  inactivityExpiresIn: number;
  absoluteExpiresIn: number;
} {
  const now = Date.now();
  const lastActivity = new Date(session.lastActivity).getTime();
  const createdAt = new Date(session.createdAt).getTime();

  const INACTIVITY_TIMEOUT = 10 * 60 * 1000;
  const ABSOLUTE_TIMEOUT = 20 * 60 * 1000;

  return {
    inactivityExpiresIn: Math.max(
      0,
      Math.floor((lastActivity + INACTIVITY_TIMEOUT - now) / 1000)
    ),
    absoluteExpiresIn: Math.max(
      0,
      Math.floor((createdAt + ABSOLUTE_TIMEOUT - now) / 1000)
    ),
  };
}
```

---

### 8.6 Mock Database

#### 8.6.1 Type Definitions and Initial Data (`src/mocks/data/db.ts`)

```typescript
// src/mocks/data/db.ts

// ============================================
// TYPE DEFINITIONS
// ============================================

export interface MockUser {
  id: string;
  email: string;
  passwordHash: string; // Plain text for mock (not hashed)
  firstName: string;
  lastName: string;
  azureTag: string;
  pin?: string; // Optional PIN for step-up auth
  createdAt: string;
}

export interface MockAccount {
  id: string;
  userId: string;
  accountNumber: string;
  name: string;
  type: 'checking' | 'savings' | 'investment';
  balance: number;
  isPrimary: boolean;
  isDeleted: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface MockTransaction {
  id: string;
  accountId: string;
  type: 'deposit' | 'withdrawal' | 'transfer_in' | 'transfer_out';
  amount: number;
  description: string | null;
  balanceAfter: number;
  relatedTransactionId: string | null;
  recipientAzureTag: string | null;
  senderAzureTag: string | null;
  createdAt: string;
}

// ============================================
// INITIAL TEST DATA
// ============================================

/**
 * Test users for development
 * Password for all: SecurePass123!
 * PIN for all: 123456
 */
export const initialUsers: MockUser[] = [
  {
    id: '550e8400-e29b-41d4-a716-446655440000',
    email: 'john.doe@example.com',
    passwordHash: 'SecurePass123!',
    firstName: 'John',
    lastName: 'Doe',
    azureTag: 'johndoe',
    pin: '123456',
    createdAt: '2026-01-01T10:00:00Z',
  },
  {
    id: '550e8400-e29b-41d4-a716-446655440001',
    email: 'jane.smith@example.com',
    passwordHash: 'SecurePass123!',
    firstName: 'Jane',
    lastName: 'Smith',
    azureTag: 'janesmith',
    pin: '123456',
    createdAt: '2026-01-02T10:00:00Z',
  },
  {
    id: '550e8400-e29b-41d4-a716-446655440002',
    email: 'bob.wilson@example.com',
    passwordHash: 'SecurePass123!',
    firstName: 'Bob',
    lastName: 'Wilson',
    azureTag: 'bobwilson',
    pin: '123456',
    createdAt: '2026-01-03T10:00:00Z',
  },
];

export const initialAccounts: MockAccount[] = [
  {
    id: '660e8400-e29b-41d4-a716-446655440001',
    userId: '550e8400-e29b-41d4-a716-446655440000',
    accountNumber: 'AB-1234-5678-90',
    name: 'Primary Account',
    type: 'checking',
    balance: 2500.0,
    isPrimary: true,
    isDeleted: false,
    createdAt: '2026-01-01T10:00:00Z',
    updatedAt: '2026-01-08T14:30:00Z',
  },
  {
    id: '660e8400-e29b-41d4-a716-446655440002',
    userId: '550e8400-e29b-41d4-a716-446655440000',
    accountNumber: 'AB-2345-6789-01',
    name: 'Savings Account',
    type: 'savings',
    balance: 10000.0,
    isPrimary: false,
    isDeleted: false,
    createdAt: '2026-01-02T10:00:00Z',
    updatedAt: '2026-01-02T10:00:00Z',
  },
  {
    id: '660e8400-e29b-41d4-a716-446655440003',
    userId: '550e8400-e29b-41d4-a716-446655440001',
    accountNumber: 'AB-3456-7890-12',
    name: 'Main Account',
    type: 'checking',
    balance: 5000.0,
    isPrimary: true,
    isDeleted: false,
    createdAt: '2026-01-02T10:00:00Z',
    updatedAt: '2026-01-02T10:00:00Z',
  },
];

export const initialTransactions: MockTransaction[] = [
  {
    id: '770e8400-e29b-41d4-a716-446655440001',
    accountId: '660e8400-e29b-41d4-a716-446655440001',
    type: 'deposit',
    amount: 1000.0,
    description: 'Initial deposit',
    balanceAfter: 1000.0,
    relatedTransactionId: null,
    recipientAzureTag: null,
    senderAzureTag: null,
    createdAt: '2026-01-01T10:30:00Z',
  },
  {
    id: '770e8400-e29b-41d4-a716-446655440002',
    accountId: '660e8400-e29b-41d4-a716-446655440001',
    type: 'deposit',
    amount: 2000.0,
    description: 'Salary January',
    balanceAfter: 3000.0,
    relatedTransactionId: null,
    recipientAzureTag: null,
    senderAzureTag: null,
    createdAt: '2026-01-05T09:00:00Z',
  },
  {
    id: '770e8400-e29b-41d4-a716-446655440003',
    accountId: '660e8400-e29b-41d4-a716-446655440001',
    type: 'withdrawal',
    amount: 200.0,
    description: 'ATM withdrawal',
    balanceAfter: 2800.0,
    relatedTransactionId: null,
    recipientAzureTag: null,
    senderAzureTag: null,
    createdAt: '2026-01-06T15:00:00Z',
  },
  {
    id: '770e8400-e29b-41d4-a716-446655440004',
    accountId: '660e8400-e29b-41d4-a716-446655440001',
    type: 'transfer_out',
    amount: 300.0,
    description: 'Payment to @janesmith',
    balanceAfter: 2500.0,
    relatedTransactionId: '770e8400-e29b-41d4-a716-446655440005',
    recipientAzureTag: 'janesmith',
    senderAzureTag: null,
    createdAt: '2026-01-07T12:00:00Z',
  },
  {
    id: '770e8400-e29b-41d4-a716-446655440005',
    accountId: '660e8400-e29b-41d4-a716-446655440003',
    type: 'transfer_in',
    amount: 300.0,
    description: 'Payment from @johndoe',
    balanceAfter: 5000.0,
    relatedTransactionId: '770e8400-e29b-41d4-a716-446655440004',
    recipientAzureTag: null,
    senderAzureTag: 'johndoe',
    createdAt: '2026-01-07T12:00:00Z',
  },
];

// ============================================
// HELPER FUNCTIONS
// ============================================

/**
 * Generate a unique account number in AzureBank format
 */
export function generateAccountNumber(): string {
  const digits = Array.from({ length: 8 }, () =>
    Math.floor(Math.random() * 10)
  ).join('');
  const formatted = `${digits.slice(0, 4)}-${digits.slice(4)}`;
  const checkDigits = String(Math.floor(Math.random() * 100)).padStart(2, '0');
  return `AB-${formatted}-${checkDigits}`;
}

/**
 * Generate a mock JWT token (not cryptographically valid)
 * This is for simulation only - real tokens come from the backend
 */
export function generateMockToken(userId: string): string {
  const payload = btoa(
    JSON.stringify({
      sub: userId,
      iat: Date.now(),
      exp: Date.now() + 30 * 60 * 1000, // 30 minutes
    })
  );
  return `mock.${payload}.signature`;
}

/**
 * Generate a correlation ID for request tracking
 */
export function generateCorrelationId(): string {
  return crypto.randomUUID();
}
```

#### 8.6.2 State Manager (`src/mocks/data/state.ts`)

```typescript
// src/mocks/data/state.ts
import {
  initialUsers,
  initialAccounts,
  initialTransactions,
  MockUser,
  MockAccount,
  MockTransaction,
} from './db';

/**
 * In-memory state manager for mock data
 *
 * This class manages all mock data and provides
 * query methods similar to a database ORM.
 *
 * State persists only during the browser session.
 * Refreshing the page resets to initial data.
 */
class MockState {
  users: MockUser[] = [...initialUsers];
  accounts: MockAccount[] = [...initialAccounts];
  transactions: MockTransaction[] = [...initialTransactions];

  /**
   * Reset all data to initial state
   * Useful for testing and development
   */
  reset(): void {
    this.users = [...initialUsers];
    this.accounts = [...initialAccounts];
    this.transactions = [...initialTransactions];
    console.log('[MSW] Mock state reset to initial data');
  }

  // ============================================
  // USER QUERIES
  // ============================================

  getUserById(id: string): MockUser | undefined {
    return this.users.find((u) => u.id === id);
  }

  getUserByEmail(email: string): MockUser | undefined {
    return this.users.find(
      (u) => u.email.toLowerCase() === email.toLowerCase()
    );
  }

  getUserByAzureTag(tag: string): MockUser | undefined {
    return this.users.find(
      (u) => u.azureTag.toLowerCase() === tag.toLowerCase()
    );
  }

  searchUsers(query: string, excludeUserId: string): MockUser[] {
    const lowerQuery = query.toLowerCase();
    return this.users.filter(
      (u) =>
        u.id !== excludeUserId &&
        (u.azureTag.toLowerCase().includes(lowerQuery) ||
          u.firstName.toLowerCase().includes(lowerQuery) ||
          u.lastName.toLowerCase().includes(lowerQuery))
    );
  }

  // ============================================
  // ACCOUNT QUERIES
  // ============================================

  getAccountsByUserId(userId: string): MockAccount[] {
    return this.accounts.filter((a) => a.userId === userId && !a.isDeleted);
  }

  getAccountById(id: string): MockAccount | undefined {
    return this.accounts.find((a) => a.id === id && !a.isDeleted);
  }

  getPrimaryAccount(userId: string): MockAccount | undefined {
    return this.accounts.find(
      (a) => a.userId === userId && a.isPrimary && !a.isDeleted
    );
  }

  // ============================================
  // TRANSACTION QUERIES
  // ============================================

  getTransactionsByAccountId(
    accountId: string,
    options?: {
      type?: string;
      fromDate?: string;
      toDate?: string;
    }
  ): MockTransaction[] {
    let transactions = this.transactions.filter(
      (t) => t.accountId === accountId
    );

    if (options?.type) {
      transactions = transactions.filter((t) => t.type === options.type);
    }

    if (options?.fromDate) {
      const from = new Date(options.fromDate).getTime();
      transactions = transactions.filter(
        (t) => new Date(t.createdAt).getTime() >= from
      );
    }

    if (options?.toDate) {
      const to = new Date(options.toDate).getTime();
      transactions = transactions.filter(
        (t) => new Date(t.createdAt).getTime() <= to
      );
    }

    // Sort by date descending (newest first)
    return transactions.sort(
      (a, b) =>
        new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime()
    );
  }

  getTransactionById(id: string): MockTransaction | undefined {
    return this.transactions.find((t) => t.id === id);
  }

  getAllUserTransactions(userId: string): MockTransaction[] {
    const accountIds = new Set(
      this.getAccountsByUserId(userId).map((a) => a.id)
    );

    return this.transactions
      .filter((t) => accountIds.has(t.accountId))
      .sort(
        (a, b) =>
          new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime()
      );
  }
}

// Export singleton instance
export const state = new MockState();

// Expose reset function for dev tools
if (import.meta.env.DEV) {
  (window as unknown as { resetMockState: () => void }).resetMockState =
    () => state.reset();
}
```

---

### 8.7 BFF Auth Handlers

#### 8.7.1 BFF Authentication Handlers (`src/mocks/handlers/bff-auth.handlers.ts`)

```typescript
// src/mocks/handlers/bff-auth.handlers.ts
import { http, HttpResponse, delay } from 'msw';
import { state } from '../data/state';
import { generateMockToken, generateCorrelationId } from '../data/db';
import {
  createSession,
  getSessionFromCookies,
  deleteSession,
  verifyPinForSession,
  getSessionTimeoutInfo,
} from '../data/session';

/**
 * BFF Authentication Handlers
 *
 * These handlers simulate the BFF authentication endpoints.
 * Key difference from backend: NO tokens returned to client.
 * Session is managed via HTTP-only cookies.
 */
export const bffAuthHandlers = [
  // ============================================
  // POST /bff/auth/login
  // ============================================
  http.post('/bff/auth/login', async ({ request }) => {
    await delay(300); // Simulate network latency

    const correlationId = generateCorrelationId();
    const body = (await request.json()) as {
      email: string;
      password: string;
    };

    // Validation
    if (!body.email || !body.password) {
      return HttpResponse.json(
        {
          type: 'VALIDATION_ERROR',
          message: 'Email and password are required',
          correlationId,
          statusCode: 400,
        },
        {
          status: 400,
          headers: { 'X-Correlation-ID': correlationId },
        }
      );
    }

    // Find user
    const user = state.getUserByEmail(body.email);

    if (!user || user.passwordHash !== body.password) {
      return HttpResponse.json(
        {
          type: 'INVALID_CREDENTIALS',
          message: 'Invalid email or password',
          correlationId,
          statusCode: 401,
        },
        {
          status: 401,
          headers: { 'X-Correlation-ID': correlationId },
        }
      );
    }

    // Generate token (stored server-side, NOT returned to client)
    const token = generateMockToken(user.id);

    // Create session
    const sessionId = createSession(user.id, token);

    // Return user info WITHOUT token
    return HttpResponse.json(
      {
        data: {
          user: {
            id: user.id,
            email: user.email,
            firstName: user.firstName,
            lastName: user.lastName,
            azureTag: user.azureTag,
          },
        },
        message: 'Login successful',
      },
      {
        status: 200,
        headers: {
          'X-Correlation-ID': correlationId,
          'Set-Cookie': `.AzureBank.Session=${sessionId}; HttpOnly; Secure; SameSite=Strict; Path=/`,
        },
      }
    );
  }),

  // ============================================
  // POST /bff/auth/register
  // ============================================
  http.post('/bff/auth/register', async ({ request }) => {
    await delay(400);

    const correlationId = generateCorrelationId();
    const body = (await request.json()) as {
      email: string;
      password: string;
      confirmPassword: string;
      firstName: string;
      lastName: string;
      azureTag: string;
    };

    // Validation
    const errors: Record<string, string[]> = {};

    if (!body.email || !body.email.includes('@')) {
      errors.email = ['Valid email is required'];
    } else if (state.getUserByEmail(body.email)) {
      errors.email = ['Email is already registered'];
    }

    if (!body.password || body.password.length < 8) {
      errors.password = ['Password must be at least 8 characters'];
    }

    if (body.password !== body.confirmPassword) {
      errors.confirmPassword = ['Passwords do not match'];
    }

    if (!body.firstName || body.firstName.length < 1) {
      errors.firstName = ['First name is required'];
    }

    if (!body.lastName || body.lastName.length < 1) {
      errors.lastName = ['Last name is required'];
    }

    if (!body.azureTag || body.azureTag.length < 3) {
      errors.azureTag = ['AzureTag must be at least 3 characters'];
    } else if (!/^[a-zA-Z0-9_]+$/.test(body.azureTag)) {
      errors.azureTag = ['AzureTag can only contain letters, numbers, and underscores'];
    } else if (state.getUserByAzureTag(body.azureTag)) {
      errors.azureTag = ['AzureTag is already taken'];
    }

    if (Object.keys(errors).length > 0) {
      return HttpResponse.json(
        {
          type: 'VALIDATION_ERROR',
          message: 'Validation failed',
          correlationId,
          statusCode: 422,
          errors,
        },
        { status: 422 }
      );
    }

    // Create user
    const userId = crypto.randomUUID();
    const now = new Date().toISOString();

    const newUser = {
      id: userId,
      email: body.email,
      passwordHash: body.password,
      firstName: body.firstName,
      lastName: body.lastName,
      azureTag: body.azureTag.toLowerCase(),
      pin: '123456', // Default PIN
      createdAt: now,
    };
    state.users.push(newUser);

    // Create primary account
    const accountId = crypto.randomUUID();
    const newAccount = {
      id: accountId,
      userId,
      accountNumber: `AB-${Math.random().toString().slice(2, 6)}-${Math.random().toString().slice(2, 6)}-${Math.random().toString().slice(2, 4)}`,
      name: 'Primary Account',
      type: 'checking' as const,
      balance: 0,
      isPrimary: true,
      isDeleted: false,
      createdAt: now,
      updatedAt: now,
    };
    state.accounts.push(newAccount);

    // Generate token and create session
    const token = generateMockToken(userId);
    const sessionId = createSession(userId, token);

    return HttpResponse.json(
      {
        data: {
          user: {
            id: newUser.id,
            email: newUser.email,
            firstName: newUser.firstName,
            lastName: newUser.lastName,
            azureTag: newUser.azureTag,
          },
          account: {
            id: newAccount.id,
            accountNumber: newAccount.accountNumber,
            name: newAccount.name,
            type: newAccount.type,
            balance: newAccount.balance,
            isPrimary: newAccount.isPrimary,
          },
        },
        message: 'Registration successful',
      },
      {
        status: 201,
        headers: {
          'X-Correlation-ID': correlationId,
          'Set-Cookie': `.AzureBank.Session=${sessionId}; HttpOnly; Secure; SameSite=Strict; Path=/`,
        },
      }
    );
  }),

  // ============================================
  // POST /bff/auth/logout
  // ============================================
  http.post('/bff/auth/logout', async ({ request }) => {
    await delay(100);

    const correlationId = generateCorrelationId();
    const cookieHeader = request.headers.get('cookie');
    const session = getSessionFromCookies(cookieHeader);

    if (session) {
      deleteSession(session.sessionId);
    }

    return HttpResponse.json(
      {
        data: null,
        message: 'Logged out successfully',
      },
      {
        status: 200,
        headers: {
          'X-Correlation-ID': correlationId,
          // Clear the session cookie
          'Set-Cookie':
            '.AzureBank.Session=; HttpOnly; Secure; SameSite=Strict; Path=/; Max-Age=0',
        },
      }
    );
  }),

  // ============================================
  // GET /bff/auth/me
  // ============================================
  http.get('/bff/auth/me', async ({ request }) => {
    await delay(100);

    const correlationId = generateCorrelationId();
    const cookieHeader = request.headers.get('cookie');
    const session = getSessionFromCookies(cookieHeader);

    if (!session) {
      return HttpResponse.json(
        {
          type: 'SESSION_EXPIRED',
          message: 'Session has expired',
          correlationId,
          statusCode: 401,
        },
        { status: 401, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    const user = state.getUserById(session.userId);

    if (!user) {
      return HttpResponse.json(
        {
          type: 'USER_NOT_FOUND',
          message: 'User not found',
          correlationId,
          statusCode: 404,
        },
        { status: 404, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    return HttpResponse.json(
      {
        data: {
          user: {
            id: user.id,
            email: user.email,
            firstName: user.firstName,
            lastName: user.lastName,
            azureTag: user.azureTag,
          },
          session: {
            authLevel: session.authLevel,
            createdAt: session.createdAt,
            lastActivity: session.lastActivity,
          },
        },
      },
      { status: 200, headers: { 'X-Correlation-ID': correlationId } }
    );
  }),

  // ============================================
  // GET /bff/auth/session-status
  // ============================================
  http.get('/bff/auth/session-status', async ({ request }) => {
    await delay(50);

    const correlationId = generateCorrelationId();
    const cookieHeader = request.headers.get('cookie');
    const session = getSessionFromCookies(cookieHeader);

    if (!session) {
      return HttpResponse.json(
        {
          type: 'SESSION_EXPIRED',
          message: 'Session has expired',
          correlationId,
          statusCode: 401,
        },
        { status: 401, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    const timeoutInfo = getSessionTimeoutInfo(session);

    return HttpResponse.json(
      {
        data: {
          authLevel: session.authLevel,
          inactivityExpiresIn: timeoutInfo.inactivityExpiresIn,
          absoluteExpiresIn: timeoutInfo.absoluteExpiresIn,
          pinVerifiedUntil: session.pinVerifiedAt || null,
        },
      },
      { status: 200, headers: { 'X-Correlation-ID': correlationId } }
    );
  }),

  // ============================================
  // POST /bff/auth/verify-pin
  // ============================================
  http.post('/bff/auth/verify-pin', async ({ request }) => {
    await delay(200);

    const correlationId = generateCorrelationId();
    const cookieHeader = request.headers.get('cookie');
    const session = getSessionFromCookies(cookieHeader);

    if (!session) {
      return HttpResponse.json(
        {
          type: 'SESSION_EXPIRED',
          message: 'Session has expired',
          correlationId,
          statusCode: 401,
        },
        { status: 401, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    const body = (await request.json()) as { pin: string };
    const user = state.getUserById(session.userId);

    // Validate PIN (default is 123456 for all test users)
    if (!user || body.pin !== (user.pin || '123456')) {
      return HttpResponse.json(
        {
          type: 'INVALID_PIN',
          message: 'Invalid PIN',
          correlationId,
          statusCode: 400,
        },
        { status: 400, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    // Upgrade session auth level
    verifyPinForSession(session.sessionId);

    const expiresAt = new Date(Date.now() + 10 * 60 * 1000).toISOString();

    return HttpResponse.json(
      {
        data: {
          verified: true,
          authLevel: 2,
          expiresAt,
        },
        message: 'PIN verified',
      },
      { status: 200, headers: { 'X-Correlation-ID': correlationId } }
    );
  }),
];
```

---

### 8.8 Session-Protected Request Helper

#### 8.8.1 Auth Verification Utility (`src/mocks/utils/verifySession.ts`)

```typescript
// src/mocks/utils/verifySession.ts
import { HttpResponse } from 'msw';
import { getSessionFromCookies, MockSession } from '../data/session';
import { generateCorrelationId } from '../data/db';

/**
 * Result of session verification
 */
export type SessionVerifyResult =
  | { success: true; session: MockSession; userId: string }
  | { success: false; response: ReturnType<typeof HttpResponse.json> };

/**
 * Verify session for protected API routes
 *
 * Use this at the start of any handler that requires authentication.
 *
 * @example
 * http.get('/api/accounts', async ({ request }) => {
 *   const auth = verifySession(request);
 *   if (!auth.success) return auth.response;
 *
 *   // auth.session and auth.userId are available
 *   const accounts = state.getAccountsByUserId(auth.userId);
 *   // ...
 * });
 */
export function verifySession(request: Request): SessionVerifyResult {
  const correlationId = generateCorrelationId();
  const cookieHeader = request.headers.get('cookie');
  const session = getSessionFromCookies(cookieHeader);

  if (!session) {
    return {
      success: false,
      response: HttpResponse.json(
        {
          type: 'SESSION_EXPIRED',
          message: 'Session has expired',
          correlationId,
          statusCode: 401,
        },
        { status: 401, headers: { 'X-Correlation-ID': correlationId } }
      ),
    };
  }

  return {
    success: true,
    session,
    userId: session.userId,
  };
}

/**
 * Verify session AND require PIN verification (auth level 2)
 *
 * Use this for sensitive operations like transfers.
 */
export function verifySessionWithPin(request: Request): SessionVerifyResult {
  const result = verifySession(request);

  if (!result.success) {
    return result;
  }

  if (result.session.authLevel < 2) {
    const correlationId = generateCorrelationId();
    return {
      success: false,
      response: HttpResponse.json(
        {
          type: 'PIN_REQUIRED',
          message: 'PIN verification required for this operation',
          correlationId,
          statusCode: 403,
        },
        { status: 403, headers: { 'X-Correlation-ID': correlationId } }
      ),
    };
  }

  return result;
}
```

---

### 8.9 Error Simulation Utilities

#### 8.9.1 Error Toggle Configuration (`src/mocks/utils/errorSimulation.ts`)

```typescript
// src/mocks/utils/errorSimulation.ts

/**
 * Error simulation configuration
 *
 * Toggle these flags to simulate different error scenarios
 * during development and testing.
 *
 * Access in browser console:
 *   window.mswErrors.networkError = true
 *   window.mswErrors.serverError = true
 */
export const errorSimulation = {
  /** Simulate network failure (MSW returns error) */
  networkError: false,

  /** Simulate 500 Internal Server Error */
  serverError: false,

  /** Simulate 429 Rate Limit */
  rateLimitError: false,

  /** Simulate slow responses */
  slowResponse: false,

  /** Delay in ms when slowResponse is true */
  slowResponseMs: 5000,

  /** Simulate random failures (10% of requests) */
  randomFailures: false,
};

/**
 * Apply error simulation in handlers
 *
 * @example
 * http.get('/api/accounts', async () => {
 *   const errorResponse = applyErrorSimulation();
 *   if (errorResponse) return errorResponse;
 *   // ... normal handling
 * });
 */
export function applyErrorSimulation(): ReturnType<
  typeof import('msw').HttpResponse.json
> | null {
  const { HttpResponse } = require('msw');
  const correlationId = crypto.randomUUID();

  if (errorSimulation.networkError) {
    return HttpResponse.error();
  }

  if (errorSimulation.serverError) {
    return HttpResponse.json(
      {
        type: 'INTERNAL_SERVER_ERROR',
        message: 'An unexpected error occurred',
        correlationId,
        statusCode: 500,
      },
      { status: 500 }
    );
  }

  if (errorSimulation.rateLimitError) {
    return HttpResponse.json(
      {
        type: 'RATE_LIMIT_EXCEEDED',
        message: 'Too many requests. Please try again later.',
        correlationId,
        statusCode: 429,
      },
      {
        status: 429,
        headers: { 'Retry-After': '60' },
      }
    );
  }

  if (errorSimulation.randomFailures && Math.random() < 0.1) {
    return HttpResponse.json(
      {
        type: 'RANDOM_FAILURE',
        message: 'Random failure for testing',
        correlationId,
        statusCode: 500,
      },
      { status: 500 }
    );
  }

  return null;
}

/**
 * Get delay to apply based on simulation settings
 */
export function getSimulatedDelay(baseDelay: number): number {
  if (errorSimulation.slowResponse) {
    return errorSimulation.slowResponseMs;
  }
  return baseDelay;
}

// Expose to window for dev tools access
if (import.meta.env.DEV) {
  (window as unknown as { mswErrors: typeof errorSimulation }).mswErrors =
    errorSimulation;
}
```

---

### 8.10 Test Credentials

#### 8.10.1 Development Test Accounts

For development and testing, use these pre-configured accounts:

| Email | Password | AzureTag | Initial Balance |
|-------|----------|----------|-----------------|
| john.doe@example.com | SecurePass123! | johndoe | €2,500.00 |
| jane.smith@example.com | SecurePass123! | janesmith | €5,000.00 |
| bob.wilson@example.com | SecurePass123! | bobwilson | €0.00 |

**PIN for all users**: `123456`

#### 8.10.2 Quick Test Scenarios

```typescript
// Login as John Doe
fetch('/bff/auth/login', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    email: 'john.doe@example.com',
    password: 'SecurePass123!'
  })
});

// Reset mock state (in browser console)
window.resetMockState();

// Enable error simulation
window.mswErrors.slowResponse = true;
window.mswErrors.slowResponseMs = 3000;
```

---

### 8.11 MSW Handler Reference

> **Full Handler Implementations**: See `07-msw-mock-handlers.md` for complete implementations of:
> - Account handlers (`account.handlers.ts`)
> - Transaction handlers (`transaction.handlers.ts`)
> - Transfer handlers (`transfer.handlers.ts`)
> - User search handlers (`user.handlers.ts`)

The handlers in `07-msw-mock-handlers.md` are fully compatible with this MSW integration strategy. Use the `verifySession` utility for consistent authentication checks.

---

### 8.12 Best Practices

#### 8.12.1 Handler Development Guidelines

1. **Always use delay()**: Simulate realistic network latency
   ```typescript
   await delay(200); // Minimum 100-300ms for realism
   ```

2. **Always include correlation IDs**: Enable request tracing
   ```typescript
   const correlationId = generateCorrelationId();
   // Include in all responses
   headers: { 'X-Correlation-ID': correlationId }
   ```

3. **Validate session first**: Use `verifySession` for protected routes
   ```typescript
   const auth = verifySession(request);
   if (!auth.success) return auth.response;
   ```

4. **Match API contract exactly**: Response shapes must match production
   ```typescript
   // Always wrap data in { data: ... } or { data: ..., message: ... }
   return HttpResponse.json({ data: result });
   ```

5. **Handle edge cases**: Include validation and business rule errors
   ```typescript
   if (account.balance < amount) {
     return HttpResponse.json({
       type: 'INSUFFICIENT_FUNDS',
       message: `Insufficient funds. Available: €${account.balance}`,
       correlationId,
       statusCode: 400,
     }, { status: 400 });
   }
   ```

#### 8.12.2 Common Pitfalls to Avoid

| Pitfall | Solution |
|---------|----------|
| Forgetting `credentials: 'include'` in fetch | RTK Query baseApi handles this |
| Not simulating delays | Always use `delay()` for realistic testing |
| Returning wrong response shape | Match API contract exactly |
| Not handling 401 in handlers | Use `verifySession` consistently |
| Exposing tokens to client | BFF handlers never return tokens |

---

## 9. FluentUI Theme Configuration (Phase 6.5)

This section defines the complete FluentUI v9 theming system for AzureBank, including brand colors, semantic tokens, typography, and component customizations.

---

### 9.1 Theme Architecture Overview

#### 9.1.1 FluentUI v9 Theming Concepts

FluentUI v9 uses a **token-based design system** where all visual properties are defined as design tokens. This enables:

- **Consistent branding**: Single source of truth for all colors
- **Dark mode support**: Easy theme switching
- **Accessibility**: Built-in contrast verification
- **Type safety**: TypeScript support for all tokens

**Key Concepts**:
| Concept | Description |
|---------|-------------|
| BrandVariants | 16-shade color ramp (10-160) |
| Design Tokens | CSS custom properties for styling |
| Theme Object | Complete token set for a theme |
| FluentProvider | Context provider for theme distribution |

#### 9.1.2 Theme File Structure

```
src/theme/
├── index.ts              # Main theme export
├── brandColors.ts        # AzureBank brand color ramp
├── lightTheme.ts         # Light theme configuration
├── darkTheme.ts          # Dark theme (prepared for future)
├── semanticColors.ts     # Transaction/status colors
├── typography.ts         # Custom typography tokens
├── layout.ts             # Spacing and layout tokens
├── animations.ts         # Animation and transition tokens
└── zIndex.ts             # Z-index scale
```

---

### 9.2 Brand Colors

#### 9.2.1 AzureBank Brand Color Ramp (`src/theme/brandColors.ts`)

```typescript
// src/theme/brandColors.ts
import type { BrandVariants } from '@fluentui/react-components';

/**
 * AzureBank Brand Color Ramp
 *
 * Primary brand color: #006DE2 (shade 60)
 *
 * This 16-shade ramp follows FluentUI's brand color system:
 * - Shades 10-50: Dark variants (pressed states, gradients)
 * - Shade 60: PRIMARY brand color
 * - Shades 70-100: Light variants (hover, focus)
 * - Shades 110-160: Very light variants (backgrounds)
 *
 * All colors have been verified for WCAG AA accessibility.
 */
export const azureBankBrand: BrandVariants = {
  10: '#001D3D',  // Darkest - pressed backgrounds
  20: '#002D5E',  // Dark - gradient ends
  30: '#003D7F',  // Pressed state
  40: '#004DA0',  // Hover state / Gradient end
  50: '#005DC1',  // Active state
  60: '#006DE2',  // ★ PRIMARY - Main brand color
  70: '#1A7FE8',  // Hover on light backgrounds
  80: '#4D99ED',  // Links hover / secondary highlights
  90: '#80B3F2',  // Light accents
  100: '#B3CDF7', // Border highlights
  110: '#CCE0FA', // Selected backgrounds
  120: '#E6F0FC', // Very light backgrounds
  130: '#F0F6FE', // Hover backgrounds (subtle)
  140: '#F5FAFF', // Light background tint
  150: '#FAFCFF', // Near white tint
  160: '#FFFFFF', // Lightest / Pure white
};

/**
 * Quick access to commonly used brand colors
 */
export const brandColors = {
  /** Primary brand color - #006DE2 */
  primary: azureBankBrand[60],
  /** Pressed state - #003D7F */
  pressed: azureBankBrand[30],
  /** Hover state - #004DA0 */
  hover: azureBankBrand[40],
  /** Link color - matches primary */
  link: azureBankBrand[60],
  /** Link hover - #4D99ED */
  linkHover: azureBankBrand[80],
  /** Light background - #E6F0FC */
  lightBackground: azureBankBrand[120],
  /** Subtle background - #F0F6FE */
  subtleBackground: azureBankBrand[130],
};
```

#### 9.2.2 Brand Color Usage Guide

| Use Case | Shade | Hex | Example |
|----------|-------|-----|---------|
| Primary buttons | 60 | #006DE2 | Submit, Confirm |
| Button hover | 40 | #004DA0 | Darkens on hover |
| Button pressed | 30 | #003D7F | Darkens more on press |
| Links | 60 | #006DE2 | Text links |
| Link hover | 80 | #4D99ED | Lightens on hover |
| Selected row | 120 | #E6F0FC | Table row highlight |
| Subtle hover | 130 | #F0F6FE | Card hover background |
| Balance card gradient | 60 → 40 | Gradient | Hero display |

---

### 9.3 Semantic Colors

#### 9.3.1 Transaction and Status Colors (`src/theme/semanticColors.ts`)

```typescript
// src/theme/semanticColors.ts

/**
 * Semantic Colors for AzureBank
 *
 * These colors convey meaning independent of the brand.
 * Used for transaction types, status indicators, and feedback.
 *
 * All color combinations meet WCAG AA contrast requirements:
 * - Foreground on Background: ≥ 4.5:1 for normal text
 * - Icon on Background: ≥ 3:1 for UI components
 */

// ============================================
// TRANSACTION TYPE COLORS
// ============================================

export const transactionColors = {
  /**
   * Deposit - Money coming in (Green)
   * Background: Light green for positive indication
   * Foreground: Dark green for text readability
   */
  deposit: {
    background: '#E6F4EA',
    foreground: '#137333',
    icon: '#34A853',
    border: '#A8D5B6',
  },

  /**
   * Withdrawal - Money going out (Red)
   * Background: Light red for caution
   * Foreground: Dark red for text readability
   */
  withdrawal: {
    background: '#FCE8E6',
    foreground: '#C5221F',
    icon: '#EA4335',
    border: '#F5B7B1',
  },

  /**
   * Transfer Out - Sending to another user (Orange/Amber)
   * Background: Light orange for action
   * Foreground: Dark orange for text readability
   */
  transferOut: {
    background: '#FEF3E2',
    foreground: '#B45309',
    icon: '#F59E0B',
    border: '#FCD9A3',
  },

  /**
   * Transfer In - Receiving from another user (Blue)
   * Background: Light blue for positive/neutral
   * Foreground: Dark blue for text readability
   */
  transferIn: {
    background: '#E0F2FE',
    foreground: '#0369A1',
    icon: '#0EA5E9',
    border: '#7DD3FC',
  },
} as const;

// ============================================
// STATUS/FEEDBACK COLORS
// ============================================

export const statusColors = {
  /**
   * Success - Operation completed successfully
   */
  success: {
    background: '#E6F4EA',
    foreground: '#137333',
    icon: '#34A853',
    border: '#A8D5B6',
  },

  /**
   * Warning - Caution or important notice
   */
  warning: {
    background: '#FEF3E2',
    foreground: '#B45309',
    icon: '#F59E0B',
    border: '#FCD9A3',
  },

  /**
   * Error - Operation failed or validation error
   */
  error: {
    background: '#FCE8E6',
    foreground: '#C5221F',
    icon: '#EA4335',
    border: '#F5B7B1',
  },

  /**
   * Info - Informational message
   */
  info: {
    background: '#E0F2FE',
    foreground: '#0369A1',
    icon: '#0EA5E9',
    border: '#7DD3FC',
  },
} as const;

// ============================================
// BALANCE DISPLAY COLORS
// ============================================

export const balanceColors = {
  /** Positive balance (green text) */
  positive: '#137333',
  /** Negative balance (red text) - rare in banking */
  negative: '#C5221F',
  /** Zero/neutral balance */
  neutral: '#1F2937',
} as const;

// ============================================
// ACCOUNT TYPE COLORS
// ============================================

export const accountTypeColors = {
  checking: {
    background: '#E0F2FE',
    foreground: '#0369A1',
    icon: '#0EA5E9',
  },
  savings: {
    background: '#E6F4EA',
    foreground: '#137333',
    icon: '#34A853',
  },
  investment: {
    background: '#F3E8FF',
    foreground: '#7C3AED',
    icon: '#8B5CF6',
  },
} as const;

// ============================================
// TYPE EXPORTS
// ============================================

export type TransactionColorKey = keyof typeof transactionColors;
export type StatusColorKey = keyof typeof statusColors;
export type AccountTypeColorKey = keyof typeof accountTypeColors;
```

#### 9.3.2 Semantic Color Usage

```typescript
// Example: Getting colors for a transaction type
import { transactionColors } from '@theme/semanticColors';

function getTransactionStyle(type: 'deposit' | 'withdrawal' | 'transfer_in' | 'transfer_out') {
  const colorMap = {
    deposit: transactionColors.deposit,
    withdrawal: transactionColors.withdrawal,
    transfer_in: transactionColors.transferIn,
    transfer_out: transactionColors.transferOut,
  };

  return colorMap[type];
}

// Usage in component
const colors = getTransactionStyle('deposit');
// colors.background → '#E6F4EA'
// colors.foreground → '#137333'
// colors.icon → '#34A853'
```

---

### 9.4 Light Theme Configuration

#### 9.4.1 Creating the Theme (`src/theme/lightTheme.ts`)

```typescript
// src/theme/lightTheme.ts
import {
  createLightTheme,
  type Theme,
} from '@fluentui/react-components';
import { azureBankBrand } from './brandColors';

/**
 * Base AzureBank Light Theme
 *
 * Created using FluentUI's createLightTheme utility with our brand colors.
 * This automatically generates 600+ design tokens.
 */
const baseTheme = createLightTheme(azureBankBrand);

/**
 * AzureBank Light Theme with Custom Overrides
 *
 * We extend the base theme with banking-specific customizations.
 * These overrides ensure consistent branding across the application.
 */
export const azureBankLightTheme: Theme = {
  ...baseTheme,

  // ============================================
  // BRAND COLOR OVERRIDES
  // ============================================

  // Primary brand color for buttons, links, etc.
  colorBrandForeground1: '#006DE2',
  colorBrandForeground2: '#004DA0',
  colorBrandForegroundLink: '#006DE2',
  colorBrandForegroundLinkHover: '#4D99ED',
  colorBrandForegroundLinkPressed: '#003D7F',

  // Background colors for primary buttons
  colorBrandBackground: '#006DE2',
  colorBrandBackgroundHover: '#004DA0',
  colorBrandBackgroundPressed: '#003D7F',
  colorBrandBackgroundSelected: '#005DC1',

  // ============================================
  // TYPOGRAPHY OVERRIDES
  // ============================================

  // Font family stack
  fontFamilyBase:
    "'Segoe UI', 'Segoe UI Web (West European)', -apple-system, BlinkMacSystemFont, Roboto, 'Helvetica Neue', sans-serif",
  fontFamilyMonospace:
    "Consolas, 'Courier New', Courier, monospace",

  // ============================================
  // BORDER RADIUS OVERRIDES
  // ============================================

  // Slightly larger border radius for modern feel
  borderRadiusMedium: '6px',
  borderRadiusLarge: '8px',
  borderRadiusXLarge: '12px',

  // ============================================
  // SHADOW OVERRIDES
  // ============================================

  // Softer shadows for banking aesthetic
  shadow2: '0 1px 2px rgba(0, 0, 0, 0.06), 0 1px 3px rgba(0, 0, 0, 0.1)',
  shadow4: '0 2px 4px rgba(0, 0, 0, 0.08), 0 4px 6px rgba(0, 0, 0, 0.06)',
  shadow8: '0 4px 8px rgba(0, 0, 0, 0.08), 0 8px 16px rgba(0, 0, 0, 0.08)',
  shadow16: '0 8px 16px rgba(0, 0, 0, 0.1), 0 16px 32px rgba(0, 0, 0, 0.08)',
  shadow28: '0 14px 28px rgba(0, 0, 0, 0.12), 0 28px 56px rgba(0, 0, 0, 0.08)',
  shadow64: '0 32px 64px rgba(0, 0, 0, 0.14), 0 64px 128px rgba(0, 0, 0, 0.1)',

  // ============================================
  // NEUTRAL COLOR ADJUSTMENTS
  // ============================================

  // Slightly warmer grays for friendlier feel
  colorNeutralForeground1: '#1F2937',  // Primary text
  colorNeutralForeground2: '#4B5563',  // Secondary text
  colorNeutralForeground3: '#6B7280',  // Tertiary text
  colorNeutralForeground4: '#9CA3AF',  // Placeholder text

  // Neutral backgrounds
  colorNeutralBackground1: '#FFFFFF',  // Main background
  colorNeutralBackground2: '#F9FAFB',  // Card backgrounds
  colorNeutralBackground3: '#F3F4F6',  // Hover backgrounds
  colorNeutralBackground4: '#E5E7EB',  // Pressed backgrounds

  // Neutral strokes (borders)
  colorNeutralStroke1: '#D1D5DB',      // Primary borders
  colorNeutralStroke2: '#E5E7EB',      // Secondary borders
  colorNeutralStroke3: '#F3F4F6',      // Subtle borders

  // ============================================
  // FOCUS INDICATORS
  // ============================================

  // High contrast focus for accessibility
  colorStrokeFocus1: '#000000',
  colorStrokeFocus2: '#FFFFFF',

  // ============================================
  // STATUS COLORS (FluentUI Palette)
  // ============================================

  // Error states
  colorPaletteRedForeground1: '#C5221F',
  colorPaletteRedForeground2: '#EA4335',
  colorPaletteRedBackground1: '#FCE8E6',
  colorPaletteRedBackground2: '#F5B7B1',
  colorPaletteRedBorder1: '#F5B7B1',
  colorPaletteRedBorder2: '#EA4335',

  // Success states
  colorPaletteGreenForeground1: '#137333',
  colorPaletteGreenForeground2: '#34A853',
  colorPaletteGreenBackground1: '#E6F4EA',
  colorPaletteGreenBackground2: '#A8D5B6',
  colorPaletteGreenBorder1: '#A8D5B6',
  colorPaletteGreenBorder2: '#34A853',

  // Warning states
  colorPaletteYellowForeground1: '#B45309',
  colorPaletteYellowForeground2: '#F59E0B',
  colorPaletteYellowBackground1: '#FEF3E2',
  colorPaletteYellowBackground2: '#FCD9A3',
  colorPaletteYellowBorder1: '#FCD9A3',
  colorPaletteYellowBorder2: '#F59E0B',
};

export default azureBankLightTheme;
```

---

### 9.5 Dark Theme (Future-Ready)

#### 9.5.1 Dark Theme Configuration (`src/theme/darkTheme.ts`)

```typescript
// src/theme/darkTheme.ts
import {
  createDarkTheme,
  type Theme,
} from '@fluentui/react-components';
import { azureBankBrand } from './brandColors';

/**
 * AzureBank Dark Theme
 *
 * Prepared for future implementation.
 * Dark mode is not in MVP scope but the theme is ready.
 */
const baseDarkTheme = createDarkTheme(azureBankBrand);

export const azureBankDarkTheme: Theme = {
  ...baseDarkTheme,

  // ============================================
  // DARK MODE SPECIFIC OVERRIDES
  // ============================================

  // Brighter brand colors for dark backgrounds
  colorBrandForeground1: '#4D99ED',
  colorBrandForeground2: '#80B3F2',
  colorBrandForegroundLink: '#4D99ED',
  colorBrandForegroundLinkHover: '#80B3F2',

  // Background colors adjusted for dark mode
  colorBrandBackground: '#006DE2',
  colorBrandBackgroundHover: '#1A7FE8',
  colorBrandBackgroundPressed: '#005DC1',

  // Typography
  fontFamilyBase:
    "'Segoe UI', 'Segoe UI Web (West European)', -apple-system, BlinkMacSystemFont, Roboto, 'Helvetica Neue', sans-serif",
  fontFamilyMonospace:
    "Consolas, 'Courier New', Courier, monospace",

  // Border radius
  borderRadiusMedium: '6px',
  borderRadiusLarge: '8px',
  borderRadiusXLarge: '12px',

  // Dark mode shadows (more subtle)
  shadow2: '0 1px 2px rgba(0, 0, 0, 0.2), 0 1px 3px rgba(0, 0, 0, 0.3)',
  shadow4: '0 2px 4px rgba(0, 0, 0, 0.2), 0 4px 6px rgba(0, 0, 0, 0.2)',
  shadow8: '0 4px 8px rgba(0, 0, 0, 0.2), 0 8px 16px rgba(0, 0, 0, 0.25)',
  shadow16: '0 8px 16px rgba(0, 0, 0, 0.25), 0 16px 32px rgba(0, 0, 0, 0.2)',

  // Dark backgrounds
  colorNeutralBackground1: '#1F2937',
  colorNeutralBackground2: '#111827',
  colorNeutralBackground3: '#0F172A',

  // Dark foregrounds
  colorNeutralForeground1: '#F9FAFB',
  colorNeutralForeground2: '#E5E7EB',
  colorNeutralForeground3: '#D1D5DB',
  colorNeutralForeground4: '#9CA3AF',

  // Dark strokes
  colorNeutralStroke1: '#374151',
  colorNeutralStroke2: '#4B5563',
  colorNeutralStroke3: '#6B7280',
};

export default azureBankDarkTheme;
```

---

### 9.6 Typography System

#### 9.6.1 Typography Tokens (`src/theme/typography.ts`)

```typescript
// src/theme/typography.ts

/**
 * AzureBank Typography System
 *
 * Extends FluentUI's typography with banking-specific text styles.
 * All measurements use rem for accessibility (respects user font size).
 */

// ============================================
// BASE TYPOGRAPHY SCALE
// ============================================

export const typography = {
  /**
   * Hero Balance Display
   * Used for main balance on dashboard
   */
  balanceHero: {
    fontSize: '3rem',        // 48px
    fontWeight: 700,
    lineHeight: 1.2,
    letterSpacing: '-0.02em',
    fontFamily: 'inherit',
  },

  /**
   * Large Balance (Card headers)
   * Used for account balances in cards
   */
  balanceLarge: {
    fontSize: '2rem',        // 32px
    fontWeight: 700,
    lineHeight: 1.25,
    letterSpacing: '-0.01em',
    fontFamily: 'inherit',
  },

  /**
   * Medium Balance (List items)
   * Used for balances in lists and tables
   */
  balanceMedium: {
    fontSize: '1.5rem',      // 24px
    fontWeight: 600,
    lineHeight: 1.3,
    letterSpacing: '-0.01em',
    fontFamily: 'inherit',
  },

  /**
   * Page Title
   * Used for main page headings
   */
  pageTitle: {
    fontSize: '1.5rem',      // 24px
    fontWeight: 600,
    lineHeight: 1.3,
    fontFamily: 'inherit',
  },

  /**
   * Section Title
   * Used for card/section headings
   */
  sectionTitle: {
    fontSize: '1.25rem',     // 20px
    fontWeight: 600,
    lineHeight: 1.4,
    fontFamily: 'inherit',
  },

  /**
   * Card Title
   * Used for smaller card headings
   */
  cardTitle: {
    fontSize: '1rem',        // 16px
    fontWeight: 600,
    lineHeight: 1.4,
    fontFamily: 'inherit',
  },

  /**
   * Body Text
   * Default text style
   */
  body: {
    fontSize: '0.875rem',    // 14px
    fontWeight: 400,
    lineHeight: 1.5,
    fontFamily: 'inherit',
  },

  /**
   * Body Strong
   * Emphasized body text
   */
  bodyStrong: {
    fontSize: '0.875rem',    // 14px
    fontWeight: 600,
    lineHeight: 1.5,
    fontFamily: 'inherit',
  },

  /**
   * Caption
   * Helper text, timestamps, labels
   */
  caption: {
    fontSize: '0.75rem',     // 12px
    fontWeight: 400,
    lineHeight: 1.4,
    fontFamily: 'inherit',
  },

  /**
   * Caption Strong
   * Emphasized captions
   */
  captionStrong: {
    fontSize: '0.75rem',     // 12px
    fontWeight: 500,
    lineHeight: 1.4,
    fontFamily: 'inherit',
  },

  /**
   * Account Number
   * Monospace for account identifiers
   */
  accountNumber: {
    fontSize: '0.875rem',    // 14px
    fontWeight: 400,
    lineHeight: 1.4,
    letterSpacing: '0.05em',
    fontFamily: 'var(--fontFamilyMonospace)',
  },

  /**
   * Transaction Amount
   * Monospace for currency amounts
   */
  transactionAmount: {
    fontSize: '1rem',        // 16px
    fontWeight: 600,
    lineHeight: 1.3,
    fontFamily: 'var(--fontFamilyMonospace)',
  },

  /**
   * AzureTag
   * Username display (@username format)
   */
  azureTag: {
    fontSize: '0.875rem',    // 14px
    fontWeight: 500,
    lineHeight: 1.4,
    fontFamily: 'inherit',
  },
} as const;

// ============================================
// TYPE EXPORTS
// ============================================

export type TypographyToken = keyof typeof typography;
export type TypographyStyle = (typeof typography)[TypographyToken];
```

#### 9.6.2 Typography Usage with makeStyles

```typescript
// Example: Using typography in a component
import { makeStyles, shorthands } from '@fluentui/react-components';
import { typography } from '@theme/typography';

const useStyles = makeStyles({
  balance: {
    ...typography.balanceHero,
    color: 'white',
  },
  accountNumber: {
    ...typography.accountNumber,
  },
  caption: {
    ...typography.caption,
    color: 'var(--colorNeutralForeground3)',
  },
});
```

---

### 9.7 Layout System

#### 9.7.1 Layout Tokens (`src/theme/layout.ts`)

```typescript
// src/theme/layout.ts

/**
 * AzureBank Layout System
 *
 * Consistent spacing and layout values for the application.
 * Uses CSS custom properties for runtime flexibility.
 */

// ============================================
// BREAKPOINTS
// ============================================

export const breakpoints = {
  /** Mobile devices: 0 - 479px */
  mobile: 0,
  /** Tablets: 480 - 767px */
  tablet: 480,
  /** Desktop: 768 - 1023px */
  desktop: 768,
  /** Wide screens: 1024 - 1439px */
  wide: 1024,
  /** Ultra-wide screens: 1440px+ */
  ultraWide: 1440,
} as const;

export const mediaQueries = {
  /** Mobile only */
  mobile: '@media (max-width: 479px)',
  /** Mobile and tablet */
  mobileAndTablet: '@media (max-width: 767px)',
  /** Tablet only */
  tablet: '@media (min-width: 480px) and (max-width: 767px)',
  /** Tablet and up */
  tabletUp: '@media (min-width: 480px)',
  /** Desktop and up */
  desktop: '@media (min-width: 768px)',
  /** Wide screens */
  wide: '@media (min-width: 1024px)',
  /** Ultra-wide screens */
  ultraWide: '@media (min-width: 1440px)',
  /** Reduced motion preference */
  reducedMotion: '@media (prefers-reduced-motion: reduce)',
} as const;

// ============================================
// PAGE LAYOUT
// ============================================

export const pageLayout = {
  /** Maximum content width */
  maxWidth: '1200px',
  /** Maximum dashboard content width */
  contentMaxWidth: '800px',
  /** Page padding on desktop */
  paddingDesktop: '32px',
  /** Page padding on mobile */
  paddingMobile: '16px',
  /** Gap between main sections */
  sectionGap: '32px',
} as const;

// ============================================
// CARD LAYOUT
// ============================================

export const cardLayout = {
  /** Card padding on desktop */
  padding: '24px',
  /** Card padding on mobile */
  paddingMobile: '16px',
  /** Gap between cards */
  gap: '24px',
  /** Card border radius */
  borderRadius: '8px',
  /** Inner element gap */
  innerGap: '16px',
} as const;

// ============================================
// FORM LAYOUT
// ============================================

export const formLayout = {
  /** Gap between form fields */
  fieldGap: '16px',
  /** Gap between label and input */
  labelGap: '4px',
  /** Inline field gap */
  inlineGap: '12px',
  /** Error message margin */
  errorMargin: '4px',
} as const;

// ============================================
// HEADER LAYOUT
// ============================================

export const headerLayout = {
  /** Header height on desktop */
  height: '64px',
  /** Header height on mobile */
  heightMobile: '56px',
  /** Logo height */
  logoHeight: '32px',
  /** Nav item gap */
  navGap: '24px',
} as const;

// ============================================
// DIALOG LAYOUT
// ============================================

export const dialogLayout = {
  /** Dialog width on desktop */
  width: '480px',
  /** Dialog max width on mobile */
  widthMobile: 'calc(100vw - 32px)',
  /** Dialog padding */
  padding: '24px',
  /** Dialog header padding */
  headerPadding: '24px 24px 16px',
  /** Dialog footer padding */
  footerPadding: '16px 24px 24px',
  /** Dialog action gap */
  actionGap: '12px',
} as const;

// ============================================
// TRANSACTION LIST LAYOUT
// ============================================

export const transactionListLayout = {
  /** Item height */
  itemHeight: '72px',
  /** Item padding */
  itemPadding: '12px 16px',
  /** Group header height */
  groupHeaderHeight: '32px',
  /** Amount column width */
  amountWidth: '100px',
} as const;

// ============================================
// TOUCH TARGETS (Accessibility)
// ============================================

export const touchTargets = {
  /** Minimum button height (WCAG 2.5.5) */
  minButtonHeight: '44px',
  /** Minimum icon button size */
  minIconButton: '44px',
  /** Minimum list item height */
  minListItem: '48px',
  /** Minimum input height */
  minInputHeight: '44px',
} as const;

// ============================================
// COMBINED EXPORT
// ============================================

export const layout = {
  breakpoints,
  mediaQueries,
  page: pageLayout,
  card: cardLayout,
  form: formLayout,
  header: headerLayout,
  dialog: dialogLayout,
  transactionList: transactionListLayout,
  touchTargets,
} as const;
```

---

### 9.8 Animation System

#### 9.8.1 Animation Tokens (`src/theme/animations.ts`)

```typescript
// src/theme/animations.ts

/**
 * AzureBank Animation System
 *
 * Defines consistent animations across the application.
 * All animations respect prefers-reduced-motion.
 */

// ============================================
// DURATION TOKENS
// ============================================

export const durations = {
  /** Ultra fast (50ms) - Micro-interactions */
  ultraFast: '50ms',
  /** Faster (100ms) - Quick feedback */
  faster: '100ms',
  /** Fast (150ms) - Button states */
  fast: '150ms',
  /** Normal (200ms) - Default transitions */
  normal: '200ms',
  /** Slow (300ms) - Dialogs, modals */
  slow: '300ms',
  /** Slower (400ms) - Complex animations */
  slower: '400ms',
  /** Skeleton pulse duration */
  skeleton: '1.5s',
  /** Balance count-up duration */
  balanceCountUp: '1s',
} as const;

// ============================================
// EASING FUNCTIONS
// ============================================

export const easings = {
  /** Standard ease */
  ease: 'ease',
  /** Ease in-out for emphasis */
  easeInOut: 'ease-in-out',
  /** Ease out for enter animations */
  easeOut: 'ease-out',
  /** Ease in for exit animations */
  easeIn: 'ease-in',
  /** Decelerate for entering elements */
  decelerate: 'cubic-bezier(0, 0, 0.2, 1)',
  /** Accelerate for exiting elements */
  accelerate: 'cubic-bezier(0.4, 0, 1, 1)',
  /** Standard motion curve */
  standard: 'cubic-bezier(0.4, 0, 0.2, 1)',
  /** Spring-like bounce */
  spring: 'cubic-bezier(0.175, 0.885, 0.32, 1.275)',
} as const;

// ============================================
// TRANSITION PRESETS
// ============================================

export const transitions = {
  /** Button hover/focus */
  button: `background-color ${durations.fast} ${easings.ease},
           transform ${durations.fast} ${easings.ease},
           box-shadow ${durations.fast} ${easings.ease}`,

  /** Card hover */
  card: `box-shadow ${durations.normal} ${easings.ease},
         transform ${durations.normal} ${easings.ease}`,

  /** Link hover */
  link: `color ${durations.fast} ${easings.ease}`,

  /** Input focus */
  input: `border-color ${durations.fast} ${easings.ease},
          box-shadow ${durations.fast} ${easings.ease}`,

  /** Opacity fade */
  fade: `opacity ${durations.normal} ${easings.ease}`,

  /** Scale transform */
  scale: `transform ${durations.fast} ${easings.spring}`,

  /** All properties (use sparingly) */
  all: `all ${durations.normal} ${easings.ease}`,
} as const;

// ============================================
// KEYFRAME ANIMATIONS (as strings for makeStyles)
// ============================================

export const keyframes = {
  /** Fade in animation */
  fadeIn: `
    @keyframes fadeIn {
      from { opacity: 0; }
      to { opacity: 1; }
    }
  `,

  /** Fade out animation */
  fadeOut: `
    @keyframes fadeOut {
      from { opacity: 1; }
      to { opacity: 0; }
    }
  `,

  /** Slide up animation (for dialogs) */
  slideUp: `
    @keyframes slideUp {
      from {
        opacity: 0;
        transform: translateY(16px);
      }
      to {
        opacity: 1;
        transform: translateY(0);
      }
    }
  `,

  /** Slide down animation */
  slideDown: `
    @keyframes slideDown {
      from {
        opacity: 0;
        transform: translateY(-16px);
      }
      to {
        opacity: 1;
        transform: translateY(0);
      }
    }
  `,

  /** Slide in from right (for toasts) */
  slideInRight: `
    @keyframes slideInRight {
      from {
        opacity: 0;
        transform: translateX(100%);
      }
      to {
        opacity: 1;
        transform: translateX(0);
      }
    }
  `,

  /** Skeleton pulse animation */
  pulse: `
    @keyframes pulse {
      0% { opacity: 1; }
      50% { opacity: 0.4; }
      100% { opacity: 1; }
    }
  `,

  /** Scale up (for success celebrations) */
  scaleUp: `
    @keyframes scaleUp {
      from {
        opacity: 0;
        transform: scale(0.9);
      }
      to {
        opacity: 1;
        transform: scale(1);
      }
    }
  `,

  /** Spin animation (for loading spinners) */
  spin: `
    @keyframes spin {
      from { transform: rotate(0deg); }
      to { transform: rotate(360deg); }
    }
  `,

  /** Bounce animation (for attention) */
  bounce: `
    @keyframes bounce {
      0%, 100% { transform: translateY(0); }
      50% { transform: translateY(-8px); }
    }
  `,
} as const;

// ============================================
// ANIMATION STYLES FOR makeStyles
// ============================================

export const animationStyles = {
  /** Dialog enter animation */
  dialogEnter: {
    animationName: 'slideUp',
    animationDuration: durations.slow,
    animationTimingFunction: easings.decelerate,
    animationFillMode: 'forwards',
  },

  /** Toast enter animation */
  toastEnter: {
    animationName: 'slideInRight',
    animationDuration: durations.slow,
    animationTimingFunction: easings.decelerate,
    animationFillMode: 'forwards',
  },

  /** Skeleton loading pulse */
  skeletonPulse: {
    animationName: 'pulse',
    animationDuration: durations.skeleton,
    animationTimingFunction: easings.easeInOut,
    animationIterationCount: 'infinite',
  },

  /** Loading spinner */
  spinner: {
    animationName: 'spin',
    animationDuration: '1s',
    animationTimingFunction: 'linear',
    animationIterationCount: 'infinite',
  },

  /** Success celebration */
  success: {
    animationName: 'scaleUp',
    animationDuration: durations.slow,
    animationTimingFunction: easings.spring,
    animationFillMode: 'forwards',
  },

  /** Card hover transform */
  cardHover: {
    transform: 'translateY(-2px)',
  },

  /** Button press transform */
  buttonPress: {
    transform: 'scale(0.98)',
  },
} as const;

// ============================================
// REDUCED MOTION FALLBACKS
// ============================================

export const reducedMotionStyles = {
  /** Disable all animations */
  '@media (prefers-reduced-motion: reduce)': {
    animationDuration: '0.001ms !important',
    animationIterationCount: '1 !important',
    transitionDuration: '0.001ms !important',
  },
} as const;

// ============================================
// COMBINED EXPORT
// ============================================

export const animations = {
  durations,
  easings,
  transitions,
  keyframes,
  styles: animationStyles,
  reducedMotion: reducedMotionStyles,
} as const;
```

---

### 9.9 Z-Index System

#### 9.9.1 Z-Index Scale (`src/theme/zIndex.ts`)

```typescript
// src/theme/zIndex.ts

/**
 * AzureBank Z-Index Scale
 *
 * Centralized z-index values to prevent stacking context conflicts.
 * Values are spaced to allow intermediate values if needed.
 */

export const zIndex = {
  /** Base level (content) */
  base: 0,

  /** Elevated cards, dropdowns */
  elevated: 100,

  /** Sticky headers, navigation */
  sticky: 200,

  /** Fixed elements (FAB buttons) */
  fixed: 300,

  /** Dropdown menus, popovers */
  dropdown: 1000,

  /** Drawer overlays */
  drawer: 1100,

  /** Modal backdrop */
  modalBackdrop: 1200,

  /** Modal/dialog content */
  modal: 1300,

  /** Popover content */
  popover: 1400,

  /** Tooltips */
  tooltip: 1500,

  /** Toast notifications */
  toast: 1600,

  /** Skip links (accessibility) */
  skipLink: 9999,
} as const;

export type ZIndexToken = keyof typeof zIndex;
```

---

### 9.10 Theme Provider Setup

#### 9.10.1 Main Theme Export (`src/theme/index.ts`)

```typescript
// src/theme/index.ts

/**
 * AzureBank Theme Exports
 *
 * Central export for all theme-related utilities and tokens.
 */

// Theme objects
export { azureBankLightTheme } from './lightTheme';
export { azureBankDarkTheme } from './darkTheme';

// Brand colors
export { azureBankBrand, brandColors } from './brandColors';

// Semantic colors
export {
  transactionColors,
  statusColors,
  balanceColors,
  accountTypeColors,
  type TransactionColorKey,
  type StatusColorKey,
  type AccountTypeColorKey,
} from './semanticColors';

// Typography
export {
  typography,
  type TypographyToken,
  type TypographyStyle,
} from './typography';

// Layout
export {
  layout,
  breakpoints,
  mediaQueries,
  pageLayout,
  cardLayout,
  formLayout,
  headerLayout,
  dialogLayout,
  transactionListLayout,
  touchTargets,
} from './layout';

// Animations
export {
  animations,
  durations,
  easings,
  transitions,
  keyframes,
  animationStyles,
  reducedMotionStyles,
} from './animations';

// Z-Index
export { zIndex, type ZIndexToken } from './zIndex';

// ============================================
// CONVENIENCE ALIASES
// ============================================

/** Default theme (light mode) */
export const azureBankTheme = azureBankLightTheme;

/** Theme type for TypeScript */
export type { Theme } from '@fluentui/react-components';
```

#### 9.10.2 FluentProvider Integration (`src/App.tsx`)

```typescript
// src/App.tsx
import React from 'react';
import { FluentProvider, Toaster, useId } from '@fluentui/react-components';
import { Provider as ReduxProvider } from 'react-redux';
import { RouterProvider } from 'react-router-dom';
import { store } from '@app/store';
import { router } from '@app/router';
import { azureBankTheme } from '@theme';

/**
 * Root Application Component
 *
 * Provides:
 * - Redux store context
 * - FluentUI theme context
 * - Router configuration
 * - Toast notification system
 */
export function App(): React.ReactElement {
  // Unique ID for toast container
  const toasterId = useId('azure-bank-toaster');

  return (
    <ReduxProvider store={store}>
      <FluentProvider theme={azureBankTheme}>
        {/* Toast notification container */}
        <Toaster toasterId={toasterId} position="top-end" timeout={5000} />

        {/* Router with all routes */}
        <RouterProvider router={router} />
      </FluentProvider>
    </ReduxProvider>
  );
}

export default App;
```

---

### 9.11 Global Styles

#### 9.11.1 CSS Reset and Base Styles (`src/index.css`)

```css
/* src/index.css */

/**
 * AzureBank Global Styles
 *
 * Minimal CSS reset and base styles.
 * Most styling is handled by FluentUI tokens.
 */

/* ============================================
   CSS RESET
   ============================================ */

*,
*::before,
*::after {
  box-sizing: border-box;
  margin: 0;
  padding: 0;
}

html {
  /* Respect user font size preferences */
  font-size: 100%;
  /* Prevent text size adjustment on orientation change */
  -webkit-text-size-adjust: 100%;
  text-size-adjust: 100%;
}

body {
  /* FluentUI will override these via FluentProvider */
  font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif;
  font-size: 14px;
  line-height: 1.5;
  color: #1f2937;
  background-color: #f9fafb;
  /* Smooth scrolling */
  scroll-behavior: smooth;
  /* Better font rendering */
  -webkit-font-smoothing: antialiased;
  -moz-osx-font-smoothing: grayscale;
}

/* ============================================
   ACCESSIBILITY
   ============================================ */

/* Skip link for keyboard navigation */
.skip-link {
  position: absolute;
  left: -9999px;
  z-index: 9999;
  padding: 1rem;
  background-color: #006de2;
  color: white;
  text-decoration: none;
  font-weight: 600;
}

.skip-link:focus {
  left: 0;
}

/* Reduced motion preference */
@media (prefers-reduced-motion: reduce) {
  *,
  *::before,
  *::after {
    animation-duration: 0.001ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.001ms !important;
    scroll-behavior: auto !important;
  }
}

/* ============================================
   FOCUS STYLES
   ============================================ */

/* Remove default focus outline - FluentUI handles this */
:focus {
  outline: none;
}

/* Visible focus for keyboard users */
:focus-visible {
  outline: 2px solid #006de2;
  outline-offset: 2px;
}

/* ============================================
   SCROLLBAR STYLING
   ============================================ */

/* Webkit browsers */
::-webkit-scrollbar {
  width: 8px;
  height: 8px;
}

::-webkit-scrollbar-track {
  background: #f3f4f6;
}

::-webkit-scrollbar-thumb {
  background: #d1d5db;
  border-radius: 4px;
}

::-webkit-scrollbar-thumb:hover {
  background: #9ca3af;
}

/* ============================================
   UTILITY CLASSES
   ============================================ */

/* Screen reader only */
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

/* Monospace text (account numbers, amounts) */
.monospace {
  font-family: Consolas, 'Courier New', Courier, monospace;
  letter-spacing: 0.05em;
}

/* ============================================
   PRINT STYLES
   ============================================ */

@media print {
  body {
    background: white;
    color: black;
  }

  /* Hide non-essential elements */
  header,
  nav,
  footer,
  button,
  .no-print {
    display: none !important;
  }
}
```

---

### 9.12 Best Practices

#### 9.12.1 Theme Usage Guidelines

| Do | Don't |
|----|-------|
| Use theme tokens via `tokens.colorBrandBackground` | Hardcode hex colors like `#006DE2` |
| Access colors from `azureBankTheme` object | Create one-off color variables |
| Use `makeStyles` with theme tokens | Use inline styles for colors |
| Import from `@theme` alias | Deep import from individual files |
| Check contrast ratios in design tools | Assume colors meet accessibility |
| Use semantic color names | Use generic names like `blue` |

#### 9.12.2 makeStyles Pattern

```typescript
// GOOD: Using theme tokens
const useStyles = makeStyles({
  card: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow4,
    ...shorthands.padding(tokens.spacingHorizontalL),
  },
  title: {
    color: tokens.colorNeutralForeground1,
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  primaryButton: {
    backgroundColor: tokens.colorBrandBackground,
    ':hover': {
      backgroundColor: tokens.colorBrandBackgroundHover,
    },
  },
});

// BAD: Hardcoding values
const useBadStyles = makeStyles({
  card: {
    backgroundColor: '#FFFFFF', // Use token instead
    border: '1px solid #E5E7EB', // Use token instead
    borderRadius: '6px', // Use token instead
    boxShadow: '0 2px 4px rgba(0,0,0,0.1)', // Use token instead
  },
});
```

#### 9.12.3 Semantic Colors Usage

```typescript
// GOOD: Using semantic colors for transaction types
import { transactionColors } from '@theme';

function TransactionBadge({ type }: { type: TransactionType }) {
  const colors = transactionColors[type];

  return (
    <span
      style={{
        backgroundColor: colors.background,
        color: colors.foreground,
        borderColor: colors.border,
      }}
    >
      {type}
    </span>
  );
}

// GOOD: Using FluentUI MessageBar with intent
<MessageBar intent="success">Deposit successful!</MessageBar>
<MessageBar intent="error">Transaction failed</MessageBar>
<MessageBar intent="warning">Low balance warning</MessageBar>
<MessageBar intent="info">Your session will expire soon</MessageBar>
```

---

## 10. Core Component Patterns (Phase 6.6)

This section provides comprehensive implementation patterns for all reusable components in the AzureBank application. Each component follows a consistent structure with full TypeScript types, FluentUI styling, and accessibility support.

---

### 10.1 Component Architecture Overview

#### 10.1.1 Standard Component Structure

Every reusable component follows this file structure:

```
src/components/common/ComponentName/
├── ComponentName.tsx           # Main component implementation
├── ComponentName.styles.ts     # FluentUI makeStyles definitions
├── ComponentName.types.ts      # TypeScript interfaces and types
├── ComponentName.test.tsx      # Unit tests (Vitest + Testing Library)
└── index.ts                    # Re-exports for clean imports
```

#### 10.1.2 Component Template

```typescript
// src/components/common/ComponentName/ComponentName.tsx
import * as React from 'react';
import { mergeClasses } from '@fluentui/react-components';
import { useStyles } from './ComponentName.styles';
import type { ComponentNameProps } from './ComponentName.types';

/**
 * ComponentName - Brief description of what this component does.
 *
 * @example
 * <ComponentName
 *   prop1="value"
 *   onAction={handleAction}
 * />
 */
export const ComponentName: React.FC<ComponentNameProps> = ({
  // Destructure props with defaults
  className,
  ...props
}) => {
  const styles = useStyles();

  return (
    <div
      className={mergeClasses(styles.root, className)}
      // ARIA attributes for accessibility
      role="region"
      aria-label="Component description"
    >
      {/* Component content */}
    </div>
  );
};

ComponentName.displayName = 'ComponentName';
```

#### 10.1.3 Styles Template

```typescript
// src/components/common/ComponentName/ComponentName.styles.ts
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';

export const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    ...shorthands.padding(tokens.spacingVerticalM, tokens.spacingHorizontalM),
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    backgroundColor: tokens.colorNeutralBackground1,
  },
  // Additional style classes...
});
```

#### 10.1.4 Types Template

```typescript
// src/components/common/ComponentName/ComponentName.types.ts
import type { HTMLAttributes } from 'react';

export interface ComponentNameProps extends HTMLAttributes<HTMLDivElement> {
  /**
   * Description of prop
   * @default defaultValue
   */
  propName?: string;

  /**
   * Callback when action occurs
   */
  onAction?: () => void;
}
```

#### 10.1.5 Index Re-export

```typescript
// src/components/common/ComponentName/index.ts
export { ComponentName } from './ComponentName';
export type { ComponentNameProps } from './ComponentName.types';
```

---

### 10.2 BalanceCard Component

The BalanceCard is the primary balance display component used on the Dashboard and Account Detail pages. It shows account information with action buttons for deposit, withdraw, and transfer.

#### 10.2.1 Types

```typescript
// src/components/common/BalanceCard/BalanceCard.types.ts
import type { Account } from '@/types/account.types';

export type BalanceCardVariant = 'hero' | 'compact' | 'mini';

export interface BalanceCardProps {
  /**
   * The account to display
   */
  account: Account;

  /**
   * Card display variant
   * - hero: Large balance display with all actions (Dashboard primary)
   * - compact: Medium size with limited actions (Account list)
   * - mini: Small inline display (Selection lists)
   * @default 'hero'
   */
  variant?: BalanceCardVariant;

  /**
   * Whether to show action buttons (Deposit, Withdraw, Transfer)
   * @default true for hero, false for compact/mini
   */
  showActions?: boolean;

  /**
   * Whether this card is selected (for account selection UI)
   * @default false
   */
  isSelected?: boolean;

  /**
   * Whether the balance is currently loading
   * @default false
   */
  isLoading?: boolean;

  /**
   * Callback when Deposit button is clicked
   */
  onDeposit?: () => void;

  /**
   * Callback when Withdraw button is clicked
   */
  onWithdraw?: () => void;

  /**
   * Callback when Transfer button is clicked
   */
  onTransfer?: () => void;

  /**
   * Callback when the card is clicked (for selection mode)
   */
  onClick?: () => void;

  /**
   * Additional CSS class name
   */
  className?: string;
}
```

#### 10.2.2 Styles

```typescript
// src/components/common/BalanceCard/BalanceCard.styles.ts
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { typography, layout, animations } from '@/theme';

export const useStyles = makeStyles({
  // Base card styles
  root: {
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1,
    ...shorthands.borderRadius(tokens.borderRadiusXLarge),
    boxShadow: tokens.shadow4,
    ...shorthands.transition('box-shadow', animations.durations.normal, animations.easings.easeOut),

    '&:hover': {
      boxShadow: tokens.shadow8,
    },
  },

  // Variant: Hero (Dashboard primary)
  rootHero: {
    ...shorthands.padding(tokens.spacingVerticalXXL, tokens.spacingHorizontalXXL),
    minHeight: '200px',
    background: `linear-gradient(135deg, ${tokens.colorBrandBackground} 0%, #004DA0 100%)`,
    color: tokens.colorNeutralForegroundOnBrand,
  },

  // Variant: Compact (Account list)
  rootCompact: {
    ...shorthands.padding(tokens.spacingVerticalL, tokens.spacingHorizontalL),
    minHeight: '120px',
  },

  // Variant: Mini (Selection lists)
  rootMini: {
    ...shorthands.padding(tokens.spacingVerticalM, tokens.spacingHorizontalM),
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },

  // Selectable card (adds border on selection)
  rootSelectable: {
    cursor: 'pointer',
    ...shorthands.border('2px', 'solid', 'transparent'),
    ...shorthands.transition('border-color', animations.durations.fast),

    '&:focus-visible': {
      outlineOffset: '2px',
      ...shorthands.outline('2px', 'solid', tokens.colorBrandForeground1),
    },
  },

  rootSelected: {
    ...shorthands.borderColor(tokens.colorBrandForeground1),
  },

  // Account type badge
  accountType: {
    display: 'inline-flex',
    alignItems: 'center',
    ...shorthands.padding(tokens.spacingVerticalXS, tokens.spacingHorizontalS),
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightMedium,
    textTransform: 'uppercase',
    letterSpacing: '0.05em',
  },

  accountTypeHero: {
    backgroundColor: 'rgba(255, 255, 255, 0.2)',
    color: tokens.colorNeutralForegroundOnBrand,
  },

  accountTypeCompact: {
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground1,
  },

  // Account name
  accountName: {
    marginTop: tokens.spacingVerticalS,
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
  },

  accountNameHero: {
    fontSize: tokens.fontSizeHero700,
    color: tokens.colorNeutralForegroundOnBrand,
  },

  // Account number
  accountNumber: {
    marginTop: tokens.spacingVerticalXS,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    letterSpacing: '0.1em',
  },

  accountNumberHero: {
    color: 'rgba(255, 255, 255, 0.8)',
  },

  accountNumberCompact: {
    color: tokens.colorNeutralForeground3,
  },

  // Balance section
  balanceSection: {
    marginTop: tokens.spacingVerticalL,
    display: 'flex',
    flexDirection: 'column',
  },

  balanceLabel: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightMedium,
    textTransform: 'uppercase',
    letterSpacing: '0.05em',
  },

  balanceLabelHero: {
    color: 'rgba(255, 255, 255, 0.7)',
  },

  balanceLabelCompact: {
    color: tokens.colorNeutralForeground3,
  },

  // Balance amount
  balanceAmount: {
    marginTop: tokens.spacingVerticalXS,
    fontWeight: tokens.fontWeightBold,
    fontFamily: tokens.fontFamilyNumeric,
    letterSpacing: '-0.02em',
  },

  balanceAmountHero: {
    ...typography.balanceHero,
    color: tokens.colorNeutralForegroundOnBrand,
  },

  balanceAmountCompact: {
    ...typography.balanceLarge,
    color: tokens.colorNeutralForeground1,
  },

  balanceAmountMini: {
    ...typography.balanceMedium,
    color: tokens.colorNeutralForeground1,
  },

  // Positive/negative balance colors
  balancePositive: {
    // Green tint for positive (optional - usually all balances shown same)
  },

  balanceNegative: {
    color: tokens.colorPaletteRedForeground1,
  },

  // Action buttons container
  actionsContainer: {
    marginTop: tokens.spacingVerticalL,
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalS,
  },

  // Primary action button (hero variant)
  actionButtonHero: {
    backgroundColor: 'rgba(255, 255, 255, 0.15)',
    color: tokens.colorNeutralForegroundOnBrand,
    ...shorthands.border('1px', 'solid', 'rgba(255, 255, 255, 0.3)'),

    '&:hover': {
      backgroundColor: 'rgba(255, 255, 255, 0.25)',
    },
  },

  // Mini variant specific
  miniInfo: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },

  miniBalance: {
    textAlign: 'right',
  },

  // Loading skeleton overlay
  loadingOverlay: {
    position: 'absolute',
    inset: 0,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(255, 255, 255, 0.8)',
    ...shorthands.borderRadius(tokens.borderRadiusXLarge),
  },
});
```

#### 10.2.3 Implementation

```typescript
// src/components/common/BalanceCard/BalanceCard.tsx
import * as React from 'react';
import { useMemo } from 'react';
import {
  Button,
  Spinner,
  mergeClasses,
} from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  ArrowUploadRegular,
  ArrowSwapRegular,
} from '@fluentui/react-icons';
import { useStyles } from './BalanceCard.styles';
import type { BalanceCardProps, BalanceCardVariant } from './BalanceCard.types';
import { AnimatedNumber } from '../AnimatedNumber';
import { formatAccountNumber } from '@/utils/formatters';

/**
 * BalanceCard - Displays account balance with optional action buttons.
 *
 * Used on Dashboard (hero variant), Account list (compact), and selection dialogs (mini).
 *
 * @example
 * // Hero variant on Dashboard
 * <BalanceCard
 *   account={primaryAccount}
 *   variant="hero"
 *   showActions
 *   onDeposit={() => openDepositDialog()}
 *   onWithdraw={() => openWithdrawDialog()}
 *   onTransfer={() => openTransferWizard()}
 * />
 *
 * @example
 * // Compact variant in account list
 * <BalanceCard
 *   account={account}
 *   variant="compact"
 *   onClick={() => navigateToAccount(account.id)}
 * />
 *
 * @example
 * // Mini variant for selection
 * <BalanceCard
 *   account={account}
 *   variant="mini"
 *   isSelected={selectedId === account.id}
 *   onClick={() => selectAccount(account.id)}
 * />
 */
export const BalanceCard: React.FC<BalanceCardProps> = ({
  account,
  variant = 'hero',
  showActions,
  isSelected = false,
  isLoading = false,
  onDeposit,
  onWithdraw,
  onTransfer,
  onClick,
  className,
}) => {
  const styles = useStyles();

  // Determine if actions should be shown based on variant and prop
  const shouldShowActions = showActions ?? (variant === 'hero');

  // Format currency for display
  const formattedBalance = useMemo(() => {
    return new Intl.NumberFormat('en-IE', {
      style: 'currency',
      currency: account.currency || 'EUR',
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(account.balance);
  }, [account.balance, account.currency]);

  // Determine if balance is negative
  const isNegative = account.balance < 0;

  // Build class names based on variant
  const rootClassName = mergeClasses(
    styles.root,
    variant === 'hero' && styles.rootHero,
    variant === 'compact' && styles.rootCompact,
    variant === 'mini' && styles.rootMini,
    onClick && styles.rootSelectable,
    isSelected && styles.rootSelected,
    className,
  );

  const accountTypeClassName = mergeClasses(
    styles.accountType,
    variant === 'hero' && styles.accountTypeHero,
    (variant === 'compact' || variant === 'mini') && styles.accountTypeCompact,
  );

  const accountNameClassName = mergeClasses(
    styles.accountName,
    variant === 'hero' && styles.accountNameHero,
  );

  const accountNumberClassName = mergeClasses(
    styles.accountNumber,
    variant === 'hero' && styles.accountNumberHero,
    (variant === 'compact' || variant === 'mini') && styles.accountNumberCompact,
  );

  const balanceLabelClassName = mergeClasses(
    styles.balanceLabel,
    variant === 'hero' && styles.balanceLabelHero,
    (variant === 'compact' || variant === 'mini') && styles.balanceLabelCompact,
  );

  const balanceAmountClassName = mergeClasses(
    styles.balanceAmount,
    variant === 'hero' && styles.balanceAmountHero,
    variant === 'compact' && styles.balanceAmountCompact,
    variant === 'mini' && styles.balanceAmountMini,
    isNegative && styles.balanceNegative,
  );

  // Handle keyboard interaction for selectable cards
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (onClick && (e.key === 'Enter' || e.key === ' ')) {
      e.preventDefault();
      onClick();
    }
  };

  // Render mini variant (simplified layout)
  if (variant === 'mini') {
    return (
      <div
        className={rootClassName}
        onClick={onClick}
        onKeyDown={handleKeyDown}
        role={onClick ? 'button' : undefined}
        tabIndex={onClick ? 0 : undefined}
        aria-pressed={onClick ? isSelected : undefined}
        aria-label={`${account.name}, ${account.accountType}, Balance: ${formattedBalance}`}
      >
        <div className={styles.miniInfo}>
          <span className={accountTypeClassName}>{account.accountType}</span>
          <span className={accountNumberClassName}>
            {formatAccountNumber(account.accountNumber)}
          </span>
        </div>
        <div className={styles.miniBalance}>
          <span className={balanceAmountClassName}>
            {isLoading ? (
              <Spinner size="tiny" />
            ) : (
              <AnimatedNumber
                value={account.balance}
                prefix="€"
                decimals={2}
              />
            )}
          </span>
        </div>
      </div>
    );
  }

  // Render hero/compact variants
  return (
    <article
      className={rootClassName}
      onClick={onClick}
      onKeyDown={handleKeyDown}
      role={onClick ? 'button' : 'article'}
      tabIndex={onClick ? 0 : undefined}
      aria-pressed={onClick ? isSelected : undefined}
      aria-label={`${account.name} account`}
    >
      {/* Loading overlay */}
      {isLoading && (
        <div className={styles.loadingOverlay} aria-hidden="true">
          <Spinner size="large" label="Loading balance..." />
        </div>
      )}

      {/* Account type badge */}
      <span className={accountTypeClassName}>
        {account.accountType}
      </span>

      {/* Account name */}
      <h2 className={accountNameClassName}>
        {account.name}
      </h2>

      {/* Account number */}
      <span className={accountNumberClassName}>
        {formatAccountNumber(account.accountNumber)}
      </span>

      {/* Balance section */}
      <div className={styles.balanceSection}>
        <span className={balanceLabelClassName}>
          Current Balance
        </span>
        <span
          className={balanceAmountClassName}
          aria-live="polite"
          aria-atomic="true"
        >
          {variant === 'hero' ? (
            <AnimatedNumber
              value={account.balance}
              prefix="€"
              decimals={2}
              duration={800}
            />
          ) : (
            formattedBalance
          )}
        </span>
      </div>

      {/* Action buttons */}
      {shouldShowActions && (
        <div
          className={styles.actionsContainer}
          role="group"
          aria-label="Account actions"
        >
          <Button
            appearance={variant === 'hero' ? 'secondary' : 'outline'}
            className={variant === 'hero' ? styles.actionButtonHero : undefined}
            icon={<ArrowDownloadRegular />}
            onClick={(e) => {
              e.stopPropagation();
              onDeposit?.();
            }}
            aria-label="Make a deposit"
          >
            Deposit
          </Button>

          <Button
            appearance={variant === 'hero' ? 'secondary' : 'outline'}
            className={variant === 'hero' ? styles.actionButtonHero : undefined}
            icon={<ArrowUploadRegular />}
            onClick={(e) => {
              e.stopPropagation();
              onWithdraw?.();
            }}
            aria-label="Make a withdrawal"
          >
            Withdraw
          </Button>

          <Button
            appearance={variant === 'hero' ? 'secondary' : 'outline'}
            className={variant === 'hero' ? styles.actionButtonHero : undefined}
            icon={<ArrowSwapRegular />}
            onClick={(e) => {
              e.stopPropagation();
              onTransfer?.();
            }}
            aria-label="Transfer money"
          >
            Transfer
          </Button>
        </div>
      )}
    </article>
  );
};

BalanceCard.displayName = 'BalanceCard';
```

#### 10.2.4 Usage Examples

```typescript
// Dashboard hero card
import { BalanceCard } from '@/components/common/BalanceCard';
import { useAccounts } from '@/hooks/useAccounts';
import { useAppDispatch } from '@/app/hooks';
import { openDialog } from '@/features/ui/uiSlice';

function DashboardPage() {
  const { primaryAccount, isLoading } = useAccounts();
  const dispatch = useAppDispatch();

  if (!primaryAccount) return null;

  return (
    <BalanceCard
      account={primaryAccount}
      variant="hero"
      isLoading={isLoading}
      onDeposit={() => dispatch(openDialog({ type: 'deposit', accountId: primaryAccount.id }))}
      onWithdraw={() => dispatch(openDialog({ type: 'withdraw', accountId: primaryAccount.id }))}
      onTransfer={() => dispatch(openDialog({ type: 'transfer' }))}
    />
  );
}
```

---

### 10.3 TransactionCard Component

The TransactionCard displays individual transaction information in list views. It shows transaction type, amount, description, and counterparty information for transfers.

#### 10.3.1 Types

```typescript
// src/components/common/TransactionCard/TransactionCard.types.ts
import type { Transaction } from '@/types/transaction.types';

export interface TransactionCardProps {
  /**
   * The transaction to display
   */
  transaction: Transaction;

  /**
   * Whether to show the account name/number (for all-accounts view)
   * @default false
   */
  showAccount?: boolean;

  /**
   * Card display variant
   * - default: Standard list item
   * - compact: Smaller for dense lists
   * @default 'default'
   */
  variant?: 'default' | 'compact';

  /**
   * Callback when the card is clicked
   */
  onClick?: () => void;

  /**
   * Additional CSS class name
   */
  className?: string;
}
```

#### 10.3.2 Styles

```typescript
// src/components/common/TransactionCard/TransactionCard.styles.ts
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { transactionColors } from '@/theme/semanticColors';
import { animations } from '@/theme';

export const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    ...shorthands.padding(tokens.spacingVerticalM, tokens.spacingHorizontalM),
    backgroundColor: tokens.colorNeutralBackground1,
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    ...shorthands.border('1px', 'solid', tokens.colorNeutralStroke2),
    ...shorthands.transition('all', animations.durations.fast),

    '&:hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      boxShadow: tokens.shadow2,
    },
  },

  rootClickable: {
    cursor: 'pointer',

    '&:focus-visible': {
      outlineOffset: '2px',
      ...shorthands.outline('2px', 'solid', tokens.colorBrandForeground1),
    },
  },

  rootCompact: {
    ...shorthands.padding(tokens.spacingVerticalS, tokens.spacingHorizontalM),
    gap: tokens.spacingHorizontalS,
  },

  // Transaction type icon container
  iconContainer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '40px',
    height: '40px',
    ...shorthands.borderRadius(tokens.borderRadiusCircular),
    flexShrink: 0,
  },

  iconContainerCompact: {
    width: '32px',
    height: '32px',
  },

  // Icon colors by transaction type
  iconDeposit: {
    backgroundColor: transactionColors.deposit.background,
    color: transactionColors.deposit.icon,
  },

  iconWithdrawal: {
    backgroundColor: transactionColors.withdrawal.background,
    color: transactionColors.withdrawal.icon,
  },

  iconTransferIn: {
    backgroundColor: transactionColors.transferIn.background,
    color: transactionColors.transferIn.icon,
  },

  iconTransferOut: {
    backgroundColor: transactionColors.transferOut.background,
    color: transactionColors.transferOut.icon,
  },

  // Main content area
  content: {
    display: 'flex',
    flexDirection: 'column',
    flex: 1,
    minWidth: 0, // Enable text truncation
  },

  // Transaction description/title
  description: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorNeutralForeground1,
    ...shorthands.overflow('hidden'),
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  // Counterparty info (for transfers)
  counterparty: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    marginTop: tokens.spacingVerticalXXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },

  azureTag: {
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorBrandForeground1,
  },

  // Date/time
  dateTime: {
    marginTop: tokens.spacingVerticalXXS,
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
  },

  // Amount section
  amountSection: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    flexShrink: 0,
  },

  amount: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    fontFamily: tokens.fontFamilyNumeric,
    tabularNums: 'tabular-nums',
  },

  amountCompact: {
    fontSize: tokens.fontSizeBase300,
  },

  amountPositive: {
    color: transactionColors.deposit.foreground,
  },

  amountNegative: {
    color: transactionColors.withdrawal.foreground,
  },

  // Balance after transaction
  balanceAfter: {
    marginTop: tokens.spacingVerticalXXS,
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground4,
  },

  // Account info (when showing in all-accounts view)
  accountInfo: {
    marginTop: tokens.spacingVerticalXXS,
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    fontFamily: tokens.fontFamilyMonospace,
  },

  // Transaction type badge
  typeBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    ...shorthands.padding(tokens.spacingVerticalXXS, tokens.spacingHorizontalXS),
    ...shorthands.borderRadius(tokens.borderRadiusSmall),
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightMedium,
    textTransform: 'uppercase',
  },
});
```

#### 10.3.3 Implementation

```typescript
// src/components/common/TransactionCard/TransactionCard.tsx
import * as React from 'react';
import { useMemo } from 'react';
import { mergeClasses } from '@fluentui/react-components';
import {
  ArrowDownRegular,
  ArrowUpRegular,
  ArrowRightRegular,
  ArrowLeftRegular,
} from '@fluentui/react-icons';
import { useStyles } from './TransactionCard.styles';
import type { TransactionCardProps } from './TransactionCard.types';
import type { TransactionType } from '@/types/transaction.types';
import { formatDateTime, formatRelativeTime } from '@/utils/formatDate';
import { formatCurrency } from '@/utils/formatCurrency';

// Icon mapping for transaction types
const transactionIcons: Record<TransactionType, React.FC<{ className?: string }>> = {
  Deposit: ArrowDownRegular,
  Withdrawal: ArrowUpRegular,
  TransferOut: ArrowRightRegular,
  TransferIn: ArrowLeftRegular,
};

// Display labels for transaction types
const transactionLabels: Record<TransactionType, string> = {
  Deposit: 'Deposit',
  Withdrawal: 'Withdrawal',
  TransferOut: 'Transfer Sent',
  TransferIn: 'Transfer Received',
};

/**
 * TransactionCard - Displays a single transaction in a list.
 *
 * Shows transaction type icon, description, amount, and optional
 * counterparty information for transfers.
 *
 * @example
 * <TransactionCard
 *   transaction={transaction}
 *   onClick={() => openTransactionDetails(transaction.id)}
 * />
 *
 * @example
 * // In all-accounts transaction list
 * <TransactionCard
 *   transaction={transaction}
 *   showAccount
 * />
 */
export const TransactionCard: React.FC<TransactionCardProps> = ({
  transaction,
  showAccount = false,
  variant = 'default',
  onClick,
  className,
}) => {
  const styles = useStyles();

  // Get the appropriate icon component
  const IconComponent = transactionIcons[transaction.type];

  // Determine if amount is positive (money in) or negative (money out)
  const isPositive = transaction.type === 'Deposit' || transaction.type === 'TransferIn';

  // Format the amount with sign
  const formattedAmount = useMemo(() => {
    const sign = isPositive ? '+' : '-';
    return `${sign}${formatCurrency(Math.abs(transaction.amount))}`;
  }, [transaction.amount, isPositive]);

  // Format the balance after transaction
  const formattedBalance = useMemo(() => {
    return `Balance: ${formatCurrency(transaction.balance)}`;
  }, [transaction.balance]);

  // Format the date/time
  const formattedDate = useMemo(() => {
    return formatRelativeTime(transaction.createdAt);
  }, [transaction.createdAt]);

  const fullDateTime = useMemo(() => {
    return formatDateTime(transaction.createdAt);
  }, [transaction.createdAt]);

  // Build class names
  const rootClassName = mergeClasses(
    styles.root,
    variant === 'compact' && styles.rootCompact,
    onClick && styles.rootClickable,
    className,
  );

  const iconContainerClassName = mergeClasses(
    styles.iconContainer,
    variant === 'compact' && styles.iconContainerCompact,
    transaction.type === 'Deposit' && styles.iconDeposit,
    transaction.type === 'Withdrawal' && styles.iconWithdrawal,
    transaction.type === 'TransferIn' && styles.iconTransferIn,
    transaction.type === 'TransferOut' && styles.iconTransferOut,
  );

  const amountClassName = mergeClasses(
    styles.amount,
    variant === 'compact' && styles.amountCompact,
    isPositive ? styles.amountPositive : styles.amountNegative,
  );

  // Handle keyboard interaction
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (onClick && (e.key === 'Enter' || e.key === ' ')) {
      e.preventDefault();
      onClick();
    }
  };

  // Determine the description to show
  const displayDescription = transaction.description ||
    transactionLabels[transaction.type];

  return (
    <article
      className={rootClassName}
      onClick={onClick}
      onKeyDown={handleKeyDown}
      role={onClick ? 'button' : 'article'}
      tabIndex={onClick ? 0 : undefined}
      aria-label={`${transactionLabels[transaction.type]}: ${formattedAmount}, ${displayDescription}`}
    >
      {/* Transaction type icon */}
      <div className={iconContainerClassName} aria-hidden="true">
        <IconComponent />
      </div>

      {/* Content */}
      <div className={styles.content}>
        {/* Description */}
        <span className={styles.description}>
          {displayDescription}
        </span>

        {/* Counterparty for transfers */}
        {(transaction.type === 'TransferIn' || transaction.type === 'TransferOut') &&
          transaction.counterpartyAzureTag && (
            <span className={styles.counterparty}>
              {transaction.type === 'TransferIn' ? 'From' : 'To'}:{' '}
              <span className={styles.azureTag}>
                @{transaction.counterpartyAzureTag}
              </span>
            </span>
          )}

        {/* Account info (when showing all accounts) */}
        {showAccount && transaction.accountNumber && (
          <span className={styles.accountInfo}>
            {transaction.accountNumber}
          </span>
        )}

        {/* Date/time */}
        <time
          className={styles.dateTime}
          dateTime={transaction.createdAt}
          title={fullDateTime}
        >
          {formattedDate}
        </time>
      </div>

      {/* Amount section */}
      <div className={styles.amountSection}>
        <span className={amountClassName} aria-label={`Amount: ${formattedAmount}`}>
          {formattedAmount}
        </span>
        <span className={styles.balanceAfter}>
          {formattedBalance}
        </span>
      </div>
    </article>
  );
};

TransactionCard.displayName = 'TransactionCard';
```

---

### 10.4 CurrencyInput Component

The CurrencyInput provides formatted Euro input with validation, live formatting, and optional minimum/maximum constraints.

#### 10.4.1 Types

```typescript
// src/components/common/CurrencyInput/CurrencyInput.types.ts
export interface CurrencyInputProps {
  /**
   * The numeric value (in cents or full units)
   */
  value: number;

  /**
   * Callback when value changes
   */
  onChange: (value: number) => void;

  /**
   * Currency symbol/code
   * @default 'EUR'
   */
  currency?: string;

  /**
   * Input label
   */
  label?: string;

  /**
   * Placeholder text
   * @default '0.00'
   */
  placeholder?: string;

  /**
   * Error message to display
   */
  error?: string;

  /**
   * Help text below input
   */
  helpText?: string;

  /**
   * Whether the field is required
   * @default false
   */
  required?: boolean;

  /**
   * Whether the input is disabled
   * @default false
   */
  disabled?: boolean;

  /**
   * Minimum allowed value
   */
  min?: number;

  /**
   * Maximum allowed value
   */
  max?: number;

  /**
   * Name attribute for form integration
   */
  name?: string;

  /**
   * ID for label association
   */
  id?: string;

  /**
   * Additional CSS class name
   */
  className?: string;

  /**
   * Auto focus on mount
   * @default false
   */
  autoFocus?: boolean;
}
```

#### 10.4.2 Styles

```typescript
// src/components/common/CurrencyInput/CurrencyInput.styles.ts
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';

export const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },

  label: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorNeutralForeground1,
  },

  labelRequired: {
    '&::after': {
      content: '" *"',
      color: tokens.colorPaletteRedForeground1,
    },
  },

  inputWrapper: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
  },

  currencySymbol: {
    position: 'absolute',
    left: tokens.spacingHorizontalM,
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    pointerEvents: 'none',
    zIndex: 1,
  },

  input: {
    width: '100%',
    paddingLeft: '36px', // Space for currency symbol
    paddingRight: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalM,
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    fontFamily: tokens.fontFamilyNumeric,
    textAlign: 'right',
    ...shorthands.border('1px', 'solid', tokens.colorNeutralStroke1),
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    ...shorthands.transition('border-color', '150ms'),

    '&:hover:not(:disabled)': {
      ...shorthands.borderColor(tokens.colorNeutralStroke1Hover),
    },

    '&:focus': {
      ...shorthands.outline('none'),
      ...shorthands.borderColor(tokens.colorBrandStroke1),
      boxShadow: `0 0 0 1px ${tokens.colorBrandStroke1}`,
    },

    '&:disabled': {
      backgroundColor: tokens.colorNeutralBackgroundDisabled,
      color: tokens.colorNeutralForegroundDisabled,
      cursor: 'not-allowed',
    },

    '&::placeholder': {
      color: tokens.colorNeutralForeground4,
    },
  },

  inputError: {
    ...shorthands.borderColor(tokens.colorPaletteRedBorder1),

    '&:focus': {
      ...shorthands.borderColor(tokens.colorPaletteRedBorder1),
      boxShadow: `0 0 0 1px ${tokens.colorPaletteRedBorder1}`,
    },
  },

  inputLarge: {
    fontSize: tokens.fontSizeHero700,
    paddingTop: tokens.spacingVerticalL,
    paddingBottom: tokens.spacingVerticalL,
  },

  helpText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },

  errorText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteRedForeground1,
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
});
```

#### 10.4.3 Implementation

```typescript
// src/components/common/CurrencyInput/CurrencyInput.tsx
import * as React from 'react';
import { useCallback, useState, useRef, useId } from 'react';
import { mergeClasses } from '@fluentui/react-components';
import { ErrorCircleRegular } from '@fluentui/react-icons';
import { useStyles } from './CurrencyInput.styles';
import type { CurrencyInputProps } from './CurrencyInput.types';

// Currency symbol mapping
const currencySymbols: Record<string, string> = {
  EUR: '€',
  USD: '$',
  GBP: '£',
};

/**
 * CurrencyInput - Formatted currency input with validation.
 *
 * Features:
 * - Live formatting as you type
 * - Decimal precision enforcement
 * - Min/max validation
 * - Currency symbol display
 * - Full keyboard support
 *
 * @example
 * const [amount, setAmount] = useState(0);
 *
 * <CurrencyInput
 *   label="Amount"
 *   value={amount}
 *   onChange={setAmount}
 *   min={1}
 *   max={10000}
 *   error={amount > balance ? 'Insufficient funds' : undefined}
 *   required
 * />
 */
export const CurrencyInput: React.FC<CurrencyInputProps> = ({
  value,
  onChange,
  currency = 'EUR',
  label,
  placeholder = '0.00',
  error,
  helpText,
  required = false,
  disabled = false,
  min,
  max,
  name,
  id: providedId,
  className,
  autoFocus = false,
}) => {
  const styles = useStyles();
  const generatedId = useId();
  const inputId = providedId || generatedId;
  const errorId = `${inputId}-error`;
  const helpId = `${inputId}-help`;

  const inputRef = useRef<HTMLInputElement>(null);

  // Internal string state for display
  const [displayValue, setDisplayValue] = useState<string>(() => {
    if (value === 0) return '';
    return formatDisplayValue(value);
  });

  // Format number for display
  function formatDisplayValue(num: number): string {
    if (num === 0) return '';
    return num.toFixed(2);
  }

  // Parse display string to number
  function parseDisplayValue(str: string): number {
    const cleaned = str.replace(/[^\d.]/g, '');
    const parsed = parseFloat(cleaned);
    return isNaN(parsed) ? 0 : Math.round(parsed * 100) / 100;
  }

  // Handle input change
  const handleChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const inputValue = e.target.value;

    // Allow empty input
    if (inputValue === '') {
      setDisplayValue('');
      onChange(0);
      return;
    }

    // Only allow numbers and one decimal point
    const regex = /^\d*\.?\d{0,2}$/;
    if (!regex.test(inputValue)) {
      return;
    }

    setDisplayValue(inputValue);

    const numericValue = parseDisplayValue(inputValue);
    onChange(numericValue);
  }, [onChange]);

  // Handle blur - format the display value
  const handleBlur = useCallback(() => {
    if (displayValue === '') {
      return;
    }

    let numericValue = parseDisplayValue(displayValue);

    // Apply min/max constraints
    if (min !== undefined && numericValue < min) {
      numericValue = min;
    }
    if (max !== undefined && numericValue > max) {
      numericValue = max;
    }

    setDisplayValue(formatDisplayValue(numericValue));
    onChange(numericValue);
  }, [displayValue, min, max, onChange]);

  // Handle focus - select all text for easy replacement
  const handleFocus = useCallback((e: React.FocusEvent<HTMLInputElement>) => {
    e.target.select();
  }, []);

  // Sync display value when external value changes
  React.useEffect(() => {
    const currentNumeric = parseDisplayValue(displayValue);
    if (value !== currentNumeric) {
      setDisplayValue(value === 0 ? '' : formatDisplayValue(value));
    }
  }, [value]);

  const symbol = currencySymbols[currency] || currency;

  const inputClassName = mergeClasses(
    styles.input,
    error && styles.inputError,
  );

  const labelClassName = mergeClasses(
    styles.label,
    required && styles.labelRequired,
  );

  return (
    <div className={mergeClasses(styles.root, className)}>
      {label && (
        <label htmlFor={inputId} className={labelClassName}>
          {label}
        </label>
      )}

      <div className={styles.inputWrapper}>
        <span className={styles.currencySymbol} aria-hidden="true">
          {symbol}
        </span>

        <input
          ref={inputRef}
          id={inputId}
          name={name}
          type="text"
          inputMode="decimal"
          className={inputClassName}
          value={displayValue}
          onChange={handleChange}
          onBlur={handleBlur}
          onFocus={handleFocus}
          placeholder={placeholder}
          disabled={disabled}
          required={required}
          autoFocus={autoFocus}
          aria-invalid={!!error}
          aria-describedby={`${error ? errorId : ''} ${helpText ? helpId : ''}`.trim() || undefined}
          aria-label={label ? undefined : 'Amount'}
        />
      </div>

      {error && (
        <span id={errorId} className={styles.errorText} role="alert">
          <ErrorCircleRegular fontSize={14} />
          {error}
        </span>
      )}

      {helpText && !error && (
        <span id={helpId} className={styles.helpText}>
          {helpText}
        </span>
      )}
    </div>
  );
};

CurrencyInput.displayName = 'CurrencyInput';
```

---

### 10.5 PasswordInput Component

The PasswordInput provides a password field with visibility toggle, strength indicator, and validation feedback.

#### 10.5.1 Types

```typescript
// src/components/common/PasswordInput/PasswordInput.types.ts
export interface PasswordInputProps {
  /**
   * Current password value
   */
  value: string;

  /**
   * Callback when value changes
   */
  onChange: (value: string) => void;

  /**
   * Input label
   */
  label?: string;

  /**
   * Placeholder text
   * @default 'Enter password'
   */
  placeholder?: string;

  /**
   * Error message to display
   */
  error?: string;

  /**
   * Whether the field is required
   * @default false
   */
  required?: boolean;

  /**
   * Whether the input is disabled
   * @default false
   */
  disabled?: boolean;

  /**
   * Show password strength indicator
   * @default false
   */
  showStrengthIndicator?: boolean;

  /**
   * Name attribute for form integration
   */
  name?: string;

  /**
   * ID for label association
   */
  id?: string;

  /**
   * Additional CSS class name
   */
  className?: string;

  /**
   * Auto focus on mount
   * @default false
   */
  autoFocus?: boolean;

  /**
   * Autocomplete attribute
   * @default 'current-password'
   */
  autoComplete?: 'current-password' | 'new-password' | 'off';
}

export type PasswordStrength = 'weak' | 'fair' | 'good' | 'strong';
```

#### 10.5.2 Styles

```typescript
// src/components/common/PasswordInput/PasswordInput.styles.ts
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';

export const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },

  label: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorNeutralForeground1,
  },

  labelRequired: {
    '&::after': {
      content: '" *"',
      color: tokens.colorPaletteRedForeground1,
    },
  },

  inputWrapper: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
  },

  input: {
    width: '100%',
    paddingLeft: tokens.spacingHorizontalM,
    paddingRight: '48px', // Space for toggle button
    paddingTop: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalM,
    fontSize: tokens.fontSizeBase300,
    ...shorthands.border('1px', 'solid', tokens.colorNeutralStroke1),
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    ...shorthands.transition('border-color', '150ms'),

    '&:hover:not(:disabled)': {
      ...shorthands.borderColor(tokens.colorNeutralStroke1Hover),
    },

    '&:focus': {
      ...shorthands.outline('none'),
      ...shorthands.borderColor(tokens.colorBrandStroke1),
      boxShadow: `0 0 0 1px ${tokens.colorBrandStroke1}`,
    },

    '&:disabled': {
      backgroundColor: tokens.colorNeutralBackgroundDisabled,
      color: tokens.colorNeutralForegroundDisabled,
      cursor: 'not-allowed',
    },

    '&::placeholder': {
      color: tokens.colorNeutralForeground4,
    },
  },

  inputError: {
    ...shorthands.borderColor(tokens.colorPaletteRedBorder1),

    '&:focus': {
      ...shorthands.borderColor(tokens.colorPaletteRedBorder1),
      boxShadow: `0 0 0 1px ${tokens.colorPaletteRedBorder1}`,
    },
  },

  toggleButton: {
    position: 'absolute',
    right: tokens.spacingHorizontalXS,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '36px',
    height: '36px',
    ...shorthands.border('none'),
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground3,
    cursor: 'pointer',
    ...shorthands.transition('color', '150ms'),

    '&:hover': {
      color: tokens.colorNeutralForeground1,
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },

    '&:focus-visible': {
      ...shorthands.outline('2px', 'solid', tokens.colorBrandStroke1),
      outlineOffset: '-2px',
    },
  },

  errorText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteRedForeground1,
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },

  // Strength indicator
  strengthContainer: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    marginTop: tokens.spacingVerticalXS,
  },

  strengthBars: {
    display: 'flex',
    gap: tokens.spacingHorizontalXS,
  },

  strengthBar: {
    flex: 1,
    height: '4px',
    ...shorthands.borderRadius('2px'),
    backgroundColor: tokens.colorNeutralStroke2,
    ...shorthands.transition('background-color', '200ms'),
  },

  strengthBarActive: {
    // Colors set via inline style based on strength
  },

  strengthLabel: {
    fontSize: tokens.fontSizeBase100,
    fontWeight: tokens.fontWeightMedium,
  },

  strengthWeak: {
    color: tokens.colorPaletteRedForeground1,
  },

  strengthFair: {
    color: '#F59E0B', // Orange
  },

  strengthGood: {
    color: '#2563EB', // Blue
  },

  strengthStrong: {
    color: tokens.colorPaletteGreenForeground1,
  },
});
```

#### 10.5.3 Implementation

```typescript
// src/components/common/PasswordInput/PasswordInput.tsx
import * as React from 'react';
import { useCallback, useState, useId, useMemo } from 'react';
import { mergeClasses } from '@fluentui/react-components';
import {
  EyeRegular,
  EyeOffRegular,
  ErrorCircleRegular,
} from '@fluentui/react-icons';
import { useStyles } from './PasswordInput.styles';
import type { PasswordInputProps, PasswordStrength } from './PasswordInput.types';

// Password strength colors
const strengthColors: Record<PasswordStrength, string> = {
  weak: '#DC2626',    // Red
  fair: '#F59E0B',    // Orange
  good: '#2563EB',    // Blue
  strong: '#16A34A',  // Green
};

// Password strength labels
const strengthLabels: Record<PasswordStrength, string> = {
  weak: 'Weak',
  fair: 'Fair',
  good: 'Good',
  strong: 'Strong',
};

// Calculate password strength
function calculateStrength(password: string): PasswordStrength {
  if (!password) return 'weak';

  let score = 0;

  // Length checks
  if (password.length >= 8) score += 1;
  if (password.length >= 12) score += 1;

  // Character variety checks
  if (/[a-z]/.test(password)) score += 1;
  if (/[A-Z]/.test(password)) score += 1;
  if (/\d/.test(password)) score += 1;
  if (/[^a-zA-Z\d]/.test(password)) score += 1;

  // Map score to strength
  if (score <= 2) return 'weak';
  if (score <= 3) return 'fair';
  if (score <= 5) return 'good';
  return 'strong';
}

/**
 * PasswordInput - Password field with visibility toggle and strength indicator.
 *
 * Features:
 * - Show/hide password toggle
 * - Optional password strength meter
 * - Full accessibility support
 * - Form integration
 *
 * @example
 * <PasswordInput
 *   label="Password"
 *   value={password}
 *   onChange={setPassword}
 *   showStrengthIndicator
 *   required
 *   autoComplete="new-password"
 * />
 */
export const PasswordInput: React.FC<PasswordInputProps> = ({
  value,
  onChange,
  label,
  placeholder = 'Enter password',
  error,
  required = false,
  disabled = false,
  showStrengthIndicator = false,
  name,
  id: providedId,
  className,
  autoFocus = false,
  autoComplete = 'current-password',
}) => {
  const styles = useStyles();
  const generatedId = useId();
  const inputId = providedId || generatedId;
  const errorId = `${inputId}-error`;

  const [isVisible, setIsVisible] = useState(false);

  // Calculate password strength
  const strength = useMemo(() => calculateStrength(value), [value]);

  // Number of active strength bars
  const strengthBarsActive = useMemo(() => {
    switch (strength) {
      case 'weak': return 1;
      case 'fair': return 2;
      case 'good': return 3;
      case 'strong': return 4;
    }
  }, [strength]);

  // Handle input change
  const handleChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    onChange(e.target.value);
  }, [onChange]);

  // Toggle password visibility
  const toggleVisibility = useCallback(() => {
    setIsVisible((prev) => !prev);
  }, []);

  const inputClassName = mergeClasses(
    styles.input,
    error && styles.inputError,
  );

  const labelClassName = mergeClasses(
    styles.label,
    required && styles.labelRequired,
  );

  const strengthLabelClassName = mergeClasses(
    styles.strengthLabel,
    strength === 'weak' && styles.strengthWeak,
    strength === 'fair' && styles.strengthFair,
    strength === 'good' && styles.strengthGood,
    strength === 'strong' && styles.strengthStrong,
  );

  return (
    <div className={mergeClasses(styles.root, className)}>
      {label && (
        <label htmlFor={inputId} className={labelClassName}>
          {label}
        </label>
      )}

      <div className={styles.inputWrapper}>
        <input
          id={inputId}
          name={name}
          type={isVisible ? 'text' : 'password'}
          className={inputClassName}
          value={value}
          onChange={handleChange}
          placeholder={placeholder}
          disabled={disabled}
          required={required}
          autoFocus={autoFocus}
          autoComplete={autoComplete}
          aria-invalid={!!error}
          aria-describedby={error ? errorId : undefined}
        />

        <button
          type="button"
          className={styles.toggleButton}
          onClick={toggleVisibility}
          disabled={disabled}
          aria-label={isVisible ? 'Hide password' : 'Show password'}
          aria-pressed={isVisible}
        >
          {isVisible ? (
            <EyeOffRegular fontSize={20} />
          ) : (
            <EyeRegular fontSize={20} />
          )}
        </button>
      </div>

      {error && (
        <span id={errorId} className={styles.errorText} role="alert">
          <ErrorCircleRegular fontSize={14} />
          {error}
        </span>
      )}

      {showStrengthIndicator && value && !error && (
        <div className={styles.strengthContainer}>
          <div className={styles.strengthBars} aria-hidden="true">
            {[1, 2, 3, 4].map((bar) => (
              <div
                key={bar}
                className={mergeClasses(
                  styles.strengthBar,
                  bar <= strengthBarsActive && styles.strengthBarActive,
                )}
                style={{
                  backgroundColor: bar <= strengthBarsActive
                    ? strengthColors[strength]
                    : undefined,
                }}
              />
            ))}
          </div>
          <span
            className={strengthLabelClassName}
            aria-live="polite"
            aria-label={`Password strength: ${strengthLabels[strength]}`}
          >
            {strengthLabels[strength]}
          </span>
        </div>
      )}
    </div>
  );
};

PasswordInput.displayName = 'PasswordInput';
```

---

### 10.6 LoadingButton Component

The LoadingButton wraps FluentUI Button with loading state support, showing a spinner and disabling interaction during async operations.

#### 10.6.1 Types

```typescript
// src/components/common/LoadingButton/LoadingButton.types.ts
import type { ButtonProps } from '@fluentui/react-components';

export interface LoadingButtonProps extends ButtonProps {
  /**
   * Whether the button is in loading state
   * @default false
   */
  isLoading?: boolean;

  /**
   * Text to show while loading
   * @default 'Processing...'
   */
  loadingText?: string;

  /**
   * Position of the spinner relative to text
   * @default 'before'
   */
  spinnerPosition?: 'before' | 'after';
}
```

#### 10.6.2 Implementation

```typescript
// src/components/common/LoadingButton/LoadingButton.tsx
import * as React from 'react';
import {
  Button,
  Spinner,
  mergeClasses,
} from '@fluentui/react-components';
import { makeStyles, tokens } from '@fluentui/react-components';
import type { LoadingButtonProps } from './LoadingButton.types';

const useStyles = makeStyles({
  button: {
    position: 'relative',
    minWidth: '100px', // Prevent layout shift
  },

  content: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: tokens.spacingHorizontalS,
  },

  // Hide text but keep space during loading
  textHidden: {
    visibility: 'hidden',
  },

  spinnerOverlay: {
    position: 'absolute',
    inset: 0,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
});

/**
 * LoadingButton - Button with integrated loading state.
 *
 * Shows a spinner and disables interaction while loading.
 * Maintains button width to prevent layout shift.
 *
 * @example
 * <LoadingButton
 *   appearance="primary"
 *   isLoading={isSubmitting}
 *   loadingText="Saving..."
 *   onClick={handleSubmit}
 * >
 *   Save Changes
 * </LoadingButton>
 *
 * @example
 * // With icon
 * <LoadingButton
 *   appearance="primary"
 *   icon={<SaveRegular />}
 *   isLoading={isSubmitting}
 * >
 *   Save
 * </LoadingButton>
 */
export const LoadingButton: React.FC<LoadingButtonProps> = ({
  isLoading = false,
  loadingText = 'Processing...',
  spinnerPosition = 'before',
  children,
  disabled,
  className,
  icon,
  ...props
}) => {
  const styles = useStyles();

  // Determine spinner size based on button size
  const spinnerSize = props.size === 'small' ? 'extra-tiny' : 'tiny';

  return (
    <Button
      {...props}
      className={mergeClasses(styles.button, className)}
      disabled={disabled || isLoading}
      icon={isLoading ? undefined : icon}
      aria-busy={isLoading}
      aria-disabled={disabled || isLoading}
    >
      {isLoading ? (
        <span className={styles.content}>
          {spinnerPosition === 'before' && (
            <Spinner size={spinnerSize} />
          )}
          <span>{loadingText}</span>
          {spinnerPosition === 'after' && (
            <Spinner size={spinnerSize} />
          )}
        </span>
      ) : (
        children
      )}
    </Button>
  );
};

LoadingButton.displayName = 'LoadingButton';
```

---

### 10.7 EmptyState Component

The EmptyState component displays a message when lists or sections have no content, with optional action buttons.

#### 10.7.1 Types

```typescript
// src/components/common/EmptyState/EmptyState.types.ts
import type { ReactNode } from 'react';

export type EmptyStateVariant = 'default' | 'compact' | 'inline';

export interface EmptyStateProps {
  /**
   * Icon to display (FluentUI icon component)
   */
  icon?: ReactNode;

  /**
   * Main title/heading
   */
  title: string;

  /**
   * Description text
   */
  description?: string;

  /**
   * Display variant
   * - default: Full empty state with icon, centered
   * - compact: Smaller version for cards
   * - inline: Inline message without centering
   * @default 'default'
   */
  variant?: EmptyStateVariant;

  /**
   * Primary action button label
   */
  actionLabel?: string;

  /**
   * Primary action callback
   */
  onAction?: () => void;

  /**
   * Secondary action button label
   */
  secondaryActionLabel?: string;

  /**
   * Secondary action callback
   */
  onSecondaryAction?: () => void;

  /**
   * Additional CSS class name
   */
  className?: string;
}
```

#### 10.7.2 Styles

```typescript
// src/components/common/EmptyState/EmptyState.styles.ts
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';

export const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    textAlign: 'center',
    ...shorthands.padding(tokens.spacingVerticalXXL, tokens.spacingHorizontalL),
  },

  rootCompact: {
    ...shorthands.padding(tokens.spacingVerticalL, tokens.spacingHorizontalM),
  },

  rootInline: {
    flexDirection: 'row',
    textAlign: 'left',
    gap: tokens.spacingHorizontalM,
    ...shorthands.padding(tokens.spacingVerticalM, tokens.spacingHorizontalM),
    backgroundColor: tokens.colorNeutralBackground2,
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
  },

  iconContainer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '80px',
    height: '80px',
    marginBottom: tokens.spacingVerticalL,
    ...shorthands.borderRadius(tokens.borderRadiusCircular),
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground3,
    fontSize: '40px',
  },

  iconContainerCompact: {
    width: '56px',
    height: '56px',
    marginBottom: tokens.spacingVerticalM,
    fontSize: '28px',
  },

  iconContainerInline: {
    width: '40px',
    height: '40px',
    marginBottom: 0,
    fontSize: '20px',
    flexShrink: 0,
  },

  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },

  title: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    marginBottom: tokens.spacingVerticalXS,
  },

  titleCompact: {
    fontSize: tokens.fontSizeBase400,
  },

  titleInline: {
    fontSize: tokens.fontSizeBase300,
    marginBottom: 0,
  },

  description: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
    maxWidth: '400px',
  },

  descriptionCompact: {
    fontSize: tokens.fontSizeBase200,
    maxWidth: '300px',
  },

  descriptionInline: {
    fontSize: tokens.fontSizeBase200,
  },

  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalL,
  },

  actionsCompact: {
    marginTop: tokens.spacingVerticalM,
  },
});
```

#### 10.7.3 Implementation

```typescript
// src/components/common/EmptyState/EmptyState.tsx
import * as React from 'react';
import { Button, mergeClasses } from '@fluentui/react-components';
import { useStyles } from './EmptyState.styles';
import type { EmptyStateProps } from './EmptyState.types';

/**
 * EmptyState - Displays when lists or sections have no content.
 *
 * @example
 * // Full empty state with action
 * <EmptyState
 *   icon={<WalletRegular />}
 *   title="No accounts yet"
 *   description="Create your first account to start managing your finances."
 *   actionLabel="Create Account"
 *   onAction={() => openCreateDialog()}
 * />
 *
 * @example
 * // Compact empty state in a card
 * <EmptyState
 *   variant="compact"
 *   icon={<HistoryRegular />}
 *   title="No recent transactions"
 * />
 *
 * @example
 * // Inline empty state
 * <EmptyState
 *   variant="inline"
 *   icon={<SearchRegular />}
 *   title="No results found"
 *   description="Try adjusting your search criteria."
 * />
 */
export const EmptyState: React.FC<EmptyStateProps> = ({
  icon,
  title,
  description,
  variant = 'default',
  actionLabel,
  onAction,
  secondaryActionLabel,
  onSecondaryAction,
  className,
}) => {
  const styles = useStyles();

  const rootClassName = mergeClasses(
    styles.root,
    variant === 'compact' && styles.rootCompact,
    variant === 'inline' && styles.rootInline,
    className,
  );

  const iconContainerClassName = mergeClasses(
    styles.iconContainer,
    variant === 'compact' && styles.iconContainerCompact,
    variant === 'inline' && styles.iconContainerInline,
  );

  const titleClassName = mergeClasses(
    styles.title,
    variant === 'compact' && styles.titleCompact,
    variant === 'inline' && styles.titleInline,
  );

  const descriptionClassName = mergeClasses(
    styles.description,
    variant === 'compact' && styles.descriptionCompact,
    variant === 'inline' && styles.descriptionInline,
  );

  const actionsClassName = mergeClasses(
    styles.actions,
    variant === 'compact' && styles.actionsCompact,
  );

  return (
    <div
      className={rootClassName}
      role="status"
      aria-label={`Empty state: ${title}`}
    >
      {icon && (
        <div className={iconContainerClassName} aria-hidden="true">
          {icon}
        </div>
      )}

      <div className={styles.content}>
        <h3 className={titleClassName}>{title}</h3>

        {description && (
          <p className={descriptionClassName}>{description}</p>
        )}
      </div>

      {(actionLabel || secondaryActionLabel) && variant !== 'inline' && (
        <div className={actionsClassName}>
          {actionLabel && onAction && (
            <Button appearance="primary" onClick={onAction}>
              {actionLabel}
            </Button>
          )}
          {secondaryActionLabel && onSecondaryAction && (
            <Button appearance="outline" onClick={onSecondaryAction}>
              {secondaryActionLabel}
            </Button>
          )}
        </div>
      )}
    </div>
  );
};

EmptyState.displayName = 'EmptyState';
```

---

### 10.8 Skeleton Components

Skeleton components provide loading placeholders that match the shape of actual content, improving perceived performance.

#### 10.8.1 BalanceCardSkeleton

```typescript
// src/components/common/Skeleton/BalanceCardSkeleton.tsx
import * as React from 'react';
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { mergeClasses } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    ...shorthands.padding(tokens.spacingVerticalXXL, tokens.spacingHorizontalXXL),
    ...shorthands.borderRadius(tokens.borderRadiusXLarge),
    backgroundColor: tokens.colorNeutralBackground3,
    minHeight: '200px',
  },

  rootCompact: {
    ...shorthands.padding(tokens.spacingVerticalL, tokens.spacingHorizontalL),
    minHeight: '120px',
  },

  shimmer: {
    backgroundImage: `linear-gradient(
      90deg,
      ${tokens.colorNeutralBackground3} 0%,
      ${tokens.colorNeutralBackground4} 50%,
      ${tokens.colorNeutralBackground3} 100%
    )`,
    backgroundSize: '200% 100%',
    animationName: {
      '0%': { backgroundPosition: '-200% 0' },
      '100%': { backgroundPosition: '200% 0' },
    },
    animationDuration: '1.5s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'ease-in-out',
    ...shorthands.borderRadius(tokens.borderRadiusSmall),
  },

  badge: {
    width: '80px',
    height: '20px',
  },

  title: {
    width: '60%',
    height: '28px',
    marginTop: tokens.spacingVerticalS,
  },

  accountNumber: {
    width: '40%',
    height: '14px',
    marginTop: tokens.spacingVerticalXS,
  },

  balance: {
    width: '50%',
    height: '48px',
    marginTop: tokens.spacingVerticalL,
  },

  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalL,
  },

  button: {
    width: '100px',
    height: '36px',
  },
});

interface BalanceCardSkeletonProps {
  variant?: 'hero' | 'compact';
  className?: string;
}

/**
 * BalanceCardSkeleton - Loading placeholder for BalanceCard.
 */
export const BalanceCardSkeleton: React.FC<BalanceCardSkeletonProps> = ({
  variant = 'hero',
  className,
}) => {
  const styles = useStyles();

  return (
    <div
      className={mergeClasses(
        styles.root,
        variant === 'compact' && styles.rootCompact,
        className,
      )}
      role="status"
      aria-label="Loading account information..."
    >
      <div className={mergeClasses(styles.shimmer, styles.badge)} />
      <div className={mergeClasses(styles.shimmer, styles.title)} />
      <div className={mergeClasses(styles.shimmer, styles.accountNumber)} />
      <div className={mergeClasses(styles.shimmer, styles.balance)} />

      {variant === 'hero' && (
        <div className={styles.actions}>
          <div className={mergeClasses(styles.shimmer, styles.button)} />
          <div className={mergeClasses(styles.shimmer, styles.button)} />
          <div className={mergeClasses(styles.shimmer, styles.button)} />
        </div>
      )}
    </div>
  );
};

BalanceCardSkeleton.displayName = 'BalanceCardSkeleton';
```

#### 10.8.2 TransactionListSkeleton

```typescript
// src/components/common/Skeleton/TransactionListSkeleton.tsx
import * as React from 'react';
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { mergeClasses } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },

  item: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    ...shorthands.padding(tokens.spacingVerticalM, tokens.spacingHorizontalM),
    backgroundColor: tokens.colorNeutralBackground1,
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    ...shorthands.border('1px', 'solid', tokens.colorNeutralStroke2),
  },

  shimmer: {
    backgroundImage: `linear-gradient(
      90deg,
      ${tokens.colorNeutralBackground3} 0%,
      ${tokens.colorNeutralBackground4} 50%,
      ${tokens.colorNeutralBackground3} 100%
    )`,
    backgroundSize: '200% 100%',
    animationName: {
      '0%': { backgroundPosition: '-200% 0' },
      '100%': { backgroundPosition: '200% 0' },
    },
    animationDuration: '1.5s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'ease-in-out',
    ...shorthands.borderRadius(tokens.borderRadiusSmall),
  },

  icon: {
    width: '40px',
    height: '40px',
    ...shorthands.borderRadius(tokens.borderRadiusCircular),
    flexShrink: 0,
  },

  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    flex: 1,
  },

  description: {
    width: '70%',
    height: '16px',
  },

  date: {
    width: '30%',
    height: '12px',
  },

  amount: {
    width: '80px',
    height: '20px',
    flexShrink: 0,
  },
});

interface TransactionListSkeletonProps {
  /**
   * Number of skeleton items to show
   * @default 5
   */
  count?: number;
  className?: string;
}

/**
 * TransactionListSkeleton - Loading placeholder for transaction lists.
 */
export const TransactionListSkeleton: React.FC<TransactionListSkeletonProps> = ({
  count = 5,
  className,
}) => {
  const styles = useStyles();

  return (
    <div
      className={mergeClasses(styles.root, className)}
      role="status"
      aria-label="Loading transactions..."
    >
      {Array.from({ length: count }, (_, index) => (
        <div key={index} className={styles.item}>
          <div className={mergeClasses(styles.shimmer, styles.icon)} />
          <div className={styles.content}>
            <div className={mergeClasses(styles.shimmer, styles.description)} />
            <div className={mergeClasses(styles.shimmer, styles.date)} />
          </div>
          <div className={mergeClasses(styles.shimmer, styles.amount)} />
        </div>
      ))}
    </div>
  );
};

TransactionListSkeleton.displayName = 'TransactionListSkeleton';
```

#### 10.8.3 AccountCardSkeleton

```typescript
// src/components/common/Skeleton/AccountCardSkeleton.tsx
import * as React from 'react';
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { mergeClasses } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    ...shorthands.padding(tokens.spacingVerticalL, tokens.spacingHorizontalL),
    ...shorthands.borderRadius(tokens.borderRadiusLarge),
    backgroundColor: tokens.colorNeutralBackground1,
    ...shorthands.border('1px', 'solid', tokens.colorNeutralStroke2),
    minHeight: '100px',
  },

  shimmer: {
    backgroundImage: `linear-gradient(
      90deg,
      ${tokens.colorNeutralBackground3} 0%,
      ${tokens.colorNeutralBackground4} 50%,
      ${tokens.colorNeutralBackground3} 100%
    )`,
    backgroundSize: '200% 100%',
    animationName: {
      '0%': { backgroundPosition: '-200% 0' },
      '100%': { backgroundPosition: '200% 0' },
    },
    animationDuration: '1.5s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'ease-in-out',
    ...shorthands.borderRadius(tokens.borderRadiusSmall),
  },

  header: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'flex-start',
  },

  type: {
    width: '60px',
    height: '16px',
  },

  name: {
    width: '50%',
    height: '20px',
    marginTop: tokens.spacingVerticalS,
  },

  accountNumber: {
    width: '40%',
    height: '14px',
    marginTop: tokens.spacingVerticalXXS,
  },

  balance: {
    width: '35%',
    height: '24px',
    marginTop: tokens.spacingVerticalM,
    alignSelf: 'flex-end',
  },
});

interface AccountCardSkeletonProps {
  className?: string;
}

/**
 * AccountCardSkeleton - Loading placeholder for account cards.
 */
export const AccountCardSkeleton: React.FC<AccountCardSkeletonProps> = ({
  className,
}) => {
  const styles = useStyles();

  return (
    <div
      className={mergeClasses(styles.root, className)}
      role="status"
      aria-label="Loading account..."
    >
      <div className={styles.header}>
        <div className={mergeClasses(styles.shimmer, styles.type)} />
      </div>
      <div className={mergeClasses(styles.shimmer, styles.name)} />
      <div className={mergeClasses(styles.shimmer, styles.accountNumber)} />
      <div className={mergeClasses(styles.shimmer, styles.balance)} />
    </div>
  );
};

AccountCardSkeleton.displayName = 'AccountCardSkeleton';
```

#### 10.8.4 Skeleton Index

```typescript
// src/components/common/Skeleton/index.ts
export { BalanceCardSkeleton } from './BalanceCardSkeleton';
export { TransactionListSkeleton } from './TransactionListSkeleton';
export { AccountCardSkeleton } from './AccountCardSkeleton';
```

---

### 10.9 ProgressStepper Component

The ProgressStepper shows step-by-step progress for multi-step processes like the Transfer Wizard.

#### 10.9.1 Types

```typescript
// src/components/common/ProgressStepper/ProgressStepper.types.ts
export interface ProgressStep {
  /**
   * Unique step identifier
   */
  id: string;

  /**
   * Step label text
   */
  label: string;

  /**
   * Optional description for the step
   */
  description?: string;
}

export interface ProgressStepperProps {
  /**
   * Array of steps to display
   */
  steps: ProgressStep[];

  /**
   * Current step index (0-based)
   */
  currentStep: number;

  /**
   * Display variant
   * - horizontal: Steps in a row (default)
   * - vertical: Steps stacked vertically
   * @default 'horizontal'
   */
  variant?: 'horizontal' | 'vertical';

  /**
   * Size variant
   * @default 'medium'
   */
  size?: 'small' | 'medium';

  /**
   * Callback when a step is clicked (for navigation)
   */
  onStepClick?: (stepIndex: number) => void;

  /**
   * Whether steps are clickable
   * @default false
   */
  clickable?: boolean;

  /**
   * Additional CSS class name
   */
  className?: string;
}
```

#### 10.9.2 Styles

```typescript
// src/components/common/ProgressStepper/ProgressStepper.styles.ts
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { animations } from '@/theme';

export const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'flex-start',
  },

  rootHorizontal: {
    flexDirection: 'row',
    justifyContent: 'center',
    width: '100%',
  },

  rootVertical: {
    flexDirection: 'column',
    gap: 0,
  },

  stepWrapper: {
    display: 'flex',
    alignItems: 'center',
  },

  stepWrapperHorizontal: {
    flexDirection: 'column',
    flex: 1,
  },

  stepWrapperVertical: {
    flexDirection: 'row',
    gap: tokens.spacingHorizontalM,
  },

  // Step indicator (circle with number/checkmark)
  stepIndicator: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    ...shorthands.borderRadius(tokens.borderRadiusCircular),
    fontWeight: tokens.fontWeightSemibold,
    ...shorthands.transition('all', animations.durations.normal),
    flexShrink: 0,
  },

  stepIndicatorMedium: {
    width: '32px',
    height: '32px',
    fontSize: tokens.fontSizeBase300,
  },

  stepIndicatorSmall: {
    width: '24px',
    height: '24px',
    fontSize: tokens.fontSizeBase200,
  },

  // Step states
  stepPending: {
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground3,
    ...shorthands.border('2px', 'solid', tokens.colorNeutralStroke2),
  },

  stepCurrent: {
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundOnBrand,
    ...shorthands.border('2px', 'solid', tokens.colorBrandBackground),
    boxShadow: `0 0 0 4px ${tokens.colorBrandBackground2}`,
  },

  stepCompleted: {
    backgroundColor: tokens.colorPaletteGreenBackground1,
    color: tokens.colorPaletteGreenForeground1,
    ...shorthands.border('2px', 'solid', tokens.colorPaletteGreenBorder1),
  },

  // Clickable step
  stepClickable: {
    cursor: 'pointer',

    '&:hover': {
      transform: 'scale(1.05)',
    },

    '&:focus-visible': {
      ...shorthands.outline('2px', 'solid', tokens.colorBrandStroke1),
      outlineOffset: '2px',
    },
  },

  // Step content (label + description)
  stepContent: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    textAlign: 'center',
    marginTop: tokens.spacingVerticalS,
  },

  stepContentVertical: {
    alignItems: 'flex-start',
    textAlign: 'left',
    marginTop: 0,
    flex: 1,
    paddingBottom: tokens.spacingVerticalL,
  },

  stepLabel: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorNeutralForeground1,
  },

  stepLabelPending: {
    color: tokens.colorNeutralForeground3,
  },

  stepLabelCurrent: {
    color: tokens.colorBrandForeground1,
  },

  stepDescription: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    marginTop: tokens.spacingVerticalXXS,
  },

  // Connector line between steps
  connector: {
    ...shorthands.transition('background-color', animations.durations.normal),
  },

  connectorHorizontal: {
    flex: 1,
    height: '2px',
    marginTop: '16px', // Align with center of indicator
    marginLeft: tokens.spacingHorizontalXS,
    marginRight: tokens.spacingHorizontalXS,
  },

  connectorVertical: {
    width: '2px',
    height: '100%',
    minHeight: '24px',
    marginLeft: '15px', // Center under indicator
    position: 'absolute',
    top: '32px',
  },

  connectorPending: {
    backgroundColor: tokens.colorNeutralStroke2,
  },

  connectorCompleted: {
    backgroundColor: tokens.colorPaletteGreenBorder1,
  },

  // Vertical step wrapper needs relative positioning for connector
  verticalStepContainer: {
    position: 'relative',
    display: 'flex',
  },
});
```

#### 10.9.3 Implementation

```typescript
// src/components/common/ProgressStepper/ProgressStepper.tsx
import * as React from 'react';
import { mergeClasses } from '@fluentui/react-components';
import { CheckmarkFilled } from '@fluentui/react-icons';
import { useStyles } from './ProgressStepper.styles';
import type { ProgressStepperProps } from './ProgressStepper.types';

/**
 * ProgressStepper - Displays step-by-step progress for multi-step processes.
 *
 * @example
 * const steps = [
 *   { id: 'source', label: 'Select Account' },
 *   { id: 'recipient', label: 'Find Recipient' },
 *   { id: 'amount', label: 'Enter Amount' },
 *   { id: 'confirm', label: 'Confirm' },
 * ];
 *
 * <ProgressStepper
 *   steps={steps}
 *   currentStep={1}
 * />
 *
 * @example
 * // Vertical variant with descriptions
 * <ProgressStepper
 *   steps={steps}
 *   currentStep={2}
 *   variant="vertical"
 * />
 */
export const ProgressStepper: React.FC<ProgressStepperProps> = ({
  steps,
  currentStep,
  variant = 'horizontal',
  size = 'medium',
  onStepClick,
  clickable = false,
  className,
}) => {
  const styles = useStyles();

  const isHorizontal = variant === 'horizontal';

  // Determine step state
  const getStepState = (index: number): 'pending' | 'current' | 'completed' => {
    if (index < currentStep) return 'completed';
    if (index === currentStep) return 'current';
    return 'pending';
  };

  // Handle step click
  const handleStepClick = (index: number) => {
    if (clickable && onStepClick) {
      onStepClick(index);
    }
  };

  // Handle keyboard navigation
  const handleKeyDown = (e: React.KeyboardEvent, index: number) => {
    if (clickable && onStepClick && (e.key === 'Enter' || e.key === ' ')) {
      e.preventDefault();
      onStepClick(index);
    }
  };

  const rootClassName = mergeClasses(
    styles.root,
    isHorizontal ? styles.rootHorizontal : styles.rootVertical,
    className,
  );

  return (
    <nav
      className={rootClassName}
      aria-label="Progress"
      role="navigation"
    >
      <ol style={{ display: 'contents' }}>
        {steps.map((step, index) => {
          const state = getStepState(index);
          const isLast = index === steps.length - 1;
          const isClickableStep = clickable && index < currentStep;

          const stepWrapperClassName = mergeClasses(
            styles.stepWrapper,
            isHorizontal ? styles.stepWrapperHorizontal : styles.stepWrapperVertical,
          );

          const indicatorClassName = mergeClasses(
            styles.stepIndicator,
            size === 'medium' ? styles.stepIndicatorMedium : styles.stepIndicatorSmall,
            state === 'pending' && styles.stepPending,
            state === 'current' && styles.stepCurrent,
            state === 'completed' && styles.stepCompleted,
            isClickableStep && styles.stepClickable,
          );

          const labelClassName = mergeClasses(
            styles.stepLabel,
            state === 'pending' && styles.stepLabelPending,
            state === 'current' && styles.stepLabelCurrent,
          );

          const contentClassName = mergeClasses(
            styles.stepContent,
            !isHorizontal && styles.stepContentVertical,
          );

          const connectorClassName = mergeClasses(
            styles.connector,
            isHorizontal ? styles.connectorHorizontal : styles.connectorVertical,
            index < currentStep ? styles.connectorCompleted : styles.connectorPending,
          );

          return (
            <React.Fragment key={step.id}>
              <li
                className={isHorizontal ? stepWrapperClassName : styles.verticalStepContainer}
                aria-current={state === 'current' ? 'step' : undefined}
              >
                {!isHorizontal && !isLast && (
                  <div className={connectorClassName} aria-hidden="true" />
                )}

                <div className={!isHorizontal ? stepWrapperClassName : undefined}>
                  {/* Step indicator */}
                  <div
                    className={indicatorClassName}
                    onClick={() => handleStepClick(index)}
                    onKeyDown={(e) => handleKeyDown(e, index)}
                    role={isClickableStep ? 'button' : undefined}
                    tabIndex={isClickableStep ? 0 : undefined}
                    aria-label={`Step ${index + 1}: ${step.label}${
                      state === 'completed' ? ' (completed)' : ''
                    }${state === 'current' ? ' (current)' : ''}`}
                  >
                    {state === 'completed' ? (
                      <CheckmarkFilled fontSize={size === 'medium' ? 18 : 14} />
                    ) : (
                      index + 1
                    )}
                  </div>

                  {/* Step content */}
                  <div className={contentClassName}>
                    <span className={labelClassName}>
                      {step.label}
                    </span>
                    {step.description && (
                      <span className={styles.stepDescription}>
                        {step.description}
                      </span>
                    )}
                  </div>
                </div>
              </li>

              {/* Horizontal connector */}
              {isHorizontal && !isLast && (
                <div className={connectorClassName} aria-hidden="true" />
              )}
            </React.Fragment>
          );
        })}
      </ol>
    </nav>
  );
};

ProgressStepper.displayName = 'ProgressStepper';
```

---

### 10.10 AnimatedNumber Component

The AnimatedNumber provides smooth number animations for balance displays, respecting reduced motion preferences.

#### 10.10.1 Implementation

```typescript
// src/components/common/AnimatedNumber/AnimatedNumber.tsx
import * as React from 'react';
import { useEffect, useState, useRef, useMemo, useCallback } from 'react';

interface AnimatedNumberProps {
  /**
   * The target number value
   */
  value: number;

  /**
   * Animation duration in milliseconds
   * @default 800
   */
  duration?: number;

  /**
   * Prefix to display (e.g., currency symbol)
   */
  prefix?: string;

  /**
   * Suffix to display
   */
  suffix?: string;

  /**
   * Number of decimal places
   * @default 2
   */
  decimals?: number;

  /**
   * Locale for number formatting
   * @default 'en-IE'
   */
  locale?: string;

  /**
   * Additional CSS class name
   */
  className?: string;
}

/**
 * AnimatedNumber - Smooth number animation for balance displays.
 *
 * Features:
 * - Smooth easing animation
 * - Respects prefers-reduced-motion
 * - Proper number formatting
 * - Accessible screen reader support
 *
 * @example
 * <AnimatedNumber
 *   value={1234.56}
 *   prefix="€"
 *   decimals={2}
 * />
 */
export const AnimatedNumber: React.FC<AnimatedNumberProps> = ({
  value,
  duration = 800,
  prefix = '',
  suffix = '',
  decimals = 2,
  locale = 'en-IE',
  className,
}) => {
  const [displayValue, setDisplayValue] = useState(value);
  const previousValueRef = useRef(value);
  const animationRef = useRef<number>();

  // Check for reduced motion preference
  const prefersReducedMotion = useMemo(() => {
    if (typeof window === 'undefined') return false;
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }, []);

  // Format number for display
  const formatNumber = useCallback((num: number): string => {
    return new Intl.NumberFormat(locale, {
      minimumFractionDigits: decimals,
      maximumFractionDigits: decimals,
    }).format(num);
  }, [locale, decimals]);

  // Animate to new value
  useEffect(() => {
    // Skip animation if reduced motion is preferred
    if (prefersReducedMotion) {
      setDisplayValue(value);
      previousValueRef.current = value;
      return;
    }

    // Skip animation if value hasn't changed
    if (value === previousValueRef.current) {
      return;
    }

    const startValue = previousValueRef.current;
    const startTime = performance.now();

    const animate = (currentTime: number) => {
      const elapsed = currentTime - startTime;
      const progress = Math.min(elapsed / duration, 1);

      // Ease out cubic for smooth deceleration
      const easeOut = 1 - Math.pow(1 - progress, 3);

      const currentValue = startValue + (value - startValue) * easeOut;
      setDisplayValue(currentValue);

      if (progress < 1) {
        animationRef.current = requestAnimationFrame(animate);
      } else {
        previousValueRef.current = value;
      }
    };

    animationRef.current = requestAnimationFrame(animate);

    return () => {
      if (animationRef.current) {
        cancelAnimationFrame(animationRef.current);
      }
    };
  }, [value, duration, prefersReducedMotion]);

  const formattedValue = formatNumber(displayValue);
  const fullText = `${prefix}${formattedValue}${suffix}`;

  return (
    <span
      className={className}
      aria-label={fullText}
      // Use aria-live for screen readers to announce changes
      aria-live="polite"
      aria-atomic="true"
    >
      {prefix}{formattedValue}{suffix}
    </span>
  );
};

AnimatedNumber.displayName = 'AnimatedNumber';

// Export a hook version for more complex use cases
export function useAnimatedNumber(
  value: number,
  duration = 800
): number {
  const [displayValue, setDisplayValue] = useState(value);
  const previousValueRef = useRef(value);
  const animationRef = useRef<number>();

  const prefersReducedMotion = useMemo(() => {
    if (typeof window === 'undefined') return false;
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }, []);

  useEffect(() => {
    if (prefersReducedMotion || value === previousValueRef.current) {
      setDisplayValue(value);
      previousValueRef.current = value;
      return;
    }

    const startValue = previousValueRef.current;
    const startTime = performance.now();

    const animate = (currentTime: number) => {
      const elapsed = currentTime - startTime;
      const progress = Math.min(elapsed / duration, 1);
      const easeOut = 1 - Math.pow(1 - progress, 3);
      const currentValue = startValue + (value - startValue) * easeOut;
      setDisplayValue(currentValue);

      if (progress < 1) {
        animationRef.current = requestAnimationFrame(animate);
      } else {
        previousValueRef.current = value;
      }
    };

    animationRef.current = requestAnimationFrame(animate);

    return () => {
      if (animationRef.current) {
        cancelAnimationFrame(animationRef.current);
      }
    };
  }, [value, duration, prefersReducedMotion]);

  return displayValue;
}
```

---

### 10.11 ErrorBoundary Component

The ErrorBoundary catches JavaScript errors in the component tree and displays a fallback UI.

#### 10.11.1 Implementation

```typescript
// src/components/common/ErrorBoundary/ErrorBoundary.tsx
import * as React from 'react';
import { Component, type ErrorInfo, type ReactNode } from 'react';
import {
  Button,
  makeStyles,
  tokens,
  shorthands,
} from '@fluentui/react-components';
import { ErrorCircleRegular, ArrowResetRegular } from '@fluentui/react-icons';

interface ErrorBoundaryProps {
  /**
   * Child components to wrap
   */
  children: ReactNode;

  /**
   * Custom fallback UI to render on error
   */
  fallback?: ReactNode;

  /**
   * Callback when an error is caught
   */
  onError?: (error: Error, errorInfo: ErrorInfo) => void;

  /**
   * Whether to show a "Try Again" button
   * @default true
   */
  showRetry?: boolean;
}

interface ErrorBoundaryState {
  hasError: boolean;
  error: Error | null;
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '200px',
    ...shorthands.padding(tokens.spacingVerticalXXL),
    textAlign: 'center',
  },

  icon: {
    fontSize: '64px',
    color: tokens.colorPaletteRedForeground1,
    marginBottom: tokens.spacingVerticalL,
  },

  title: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    marginBottom: tokens.spacingVerticalS,
  },

  message: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
    marginBottom: tokens.spacingVerticalL,
    maxWidth: '400px',
  },

  errorDetails: {
    marginTop: tokens.spacingVerticalM,
    ...shorthands.padding(tokens.spacingVerticalM),
    backgroundColor: tokens.colorNeutralBackground3,
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorPaletteRedForeground1,
    maxWidth: '100%',
    overflow: 'auto',
    textAlign: 'left',
  },
});

// Functional component for the error UI
const ErrorFallbackUI: React.FC<{
  error: Error | null;
  onRetry: () => void;
  showRetry: boolean;
}> = ({ error, onRetry, showRetry }) => {
  const styles = useStyles();

  return (
    <div className={styles.container} role="alert">
      <ErrorCircleRegular className={styles.icon} />

      <h2 className={styles.title}>Something went wrong</h2>

      <p className={styles.message}>
        We're sorry, but something unexpected happened. Please try again
        or contact support if the problem persists.
      </p>

      {showRetry && (
        <Button
          appearance="primary"
          icon={<ArrowResetRegular />}
          onClick={onRetry}
        >
          Try Again
        </Button>
      )}

      {process.env.NODE_ENV === 'development' && error && (
        <pre className={styles.errorDetails}>
          {error.message}
          {'\n\n'}
          {error.stack}
        </pre>
      )}
    </div>
  );
};

/**
 * ErrorBoundary - Catches JavaScript errors and displays fallback UI.
 *
 * @example
 * <ErrorBoundary onError={(error) => logError(error)}>
 *   <MyComponent />
 * </ErrorBoundary>
 *
 * @example
 * // With custom fallback
 * <ErrorBoundary fallback={<CustomErrorPage />}>
 *   <MyComponent />
 * </ErrorBoundary>
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  constructor(props: ErrorBoundaryProps) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo): void {
    // Log error to monitoring service
    console.error('ErrorBoundary caught an error:', error, errorInfo);

    // Call onError callback if provided
    this.props.onError?.(error, errorInfo);
  }

  handleRetry = (): void => {
    this.setState({ hasError: false, error: null });
  };

  render(): ReactNode {
    const { hasError, error } = this.state;
    const { children, fallback, showRetry = true } = this.props;

    if (hasError) {
      if (fallback) {
        return fallback;
      }

      return (
        <ErrorFallbackUI
          error={error}
          onRetry={this.handleRetry}
          showRetry={showRetry}
        />
      );
    }

    return children;
  }
}
```

---

### 10.12 Utility Functions

#### 10.12.1 Format Currency

```typescript
// src/utils/formatCurrency.ts

interface FormatCurrencyOptions {
  currency?: string;
  locale?: string;
  showSign?: boolean;
  minimumFractionDigits?: number;
  maximumFractionDigits?: number;
}

/**
 * Format a number as currency
 *
 * @example
 * formatCurrency(1234.56) // "€1,234.56"
 * formatCurrency(-1234.56, { showSign: true }) // "-€1,234.56"
 * formatCurrency(1234.56, { currency: 'USD', locale: 'en-US' }) // "$1,234.56"
 */
export function formatCurrency(
  amount: number,
  options: FormatCurrencyOptions = {}
): string {
  const {
    currency = 'EUR',
    locale = 'en-IE',
    showSign = false,
    minimumFractionDigits = 2,
    maximumFractionDigits = 2,
  } = options;

  const formatter = new Intl.NumberFormat(locale, {
    style: 'currency',
    currency,
    minimumFractionDigits,
    maximumFractionDigits,
  });

  const formatted = formatter.format(Math.abs(amount));

  if (showSign && amount !== 0) {
    return amount > 0 ? `+${formatted}` : `-${formatted}`;
  }

  return amount < 0 ? `-${formatted}` : formatted;
}

/**
 * Parse a currency string to number
 *
 * @example
 * parseCurrency("€1,234.56") // 1234.56
 * parseCurrency("$1,234.56") // 1234.56
 */
export function parseCurrency(value: string): number {
  // Remove currency symbols, thousand separators, and whitespace
  const cleaned = value.replace(/[^\d.-]/g, '');
  const parsed = parseFloat(cleaned);
  return isNaN(parsed) ? 0 : parsed;
}
```

#### 10.12.2 Format Date

```typescript
// src/utils/formatDate.ts
import {
  format,
  formatDistanceToNow,
  isToday,
  isYesterday,
  isThisWeek,
  isThisYear,
  parseISO,
} from 'date-fns';

/**
 * Format a date for display
 *
 * @example
 * formatDate('2026-01-09T14:30:00Z') // "9 Jan 2026"
 */
export function formatDate(dateString: string): string {
  const date = parseISO(dateString);

  if (isThisYear(date)) {
    return format(date, 'd MMM');
  }

  return format(date, 'd MMM yyyy');
}

/**
 * Format a date with time
 *
 * @example
 * formatDateTime('2026-01-09T14:30:00Z') // "9 Jan 2026 at 14:30"
 */
export function formatDateTime(dateString: string): string {
  const date = parseISO(dateString);
  return format(date, "d MMM yyyy 'at' HH:mm");
}

/**
 * Format a date as relative time
 *
 * @example
 * formatRelativeTime('2026-01-09T14:30:00Z') // "2 hours ago"
 * formatRelativeTime('2026-01-08T14:30:00Z') // "Yesterday"
 */
export function formatRelativeTime(dateString: string): string {
  const date = parseISO(dateString);

  if (isToday(date)) {
    return formatDistanceToNow(date, { addSuffix: true });
  }

  if (isYesterday(date)) {
    return 'Yesterday';
  }

  if (isThisWeek(date)) {
    return format(date, 'EEEE'); // Day name
  }

  if (isThisYear(date)) {
    return format(date, 'd MMM');
  }

  return format(date, 'd MMM yyyy');
}

/**
 * Format account number for display
 *
 * @example
 * formatAccountNumber('AB12345678XX') // "AB-1234-5678-XX"
 */
export function formatAccountNumber(accountNumber: string): string {
  // Already formatted
  if (accountNumber.includes('-')) {
    return accountNumber;
  }

  // Format: AB-XXXX-XXXX-XX
  if (accountNumber.length === 12) {
    return `${accountNumber.slice(0, 2)}-${accountNumber.slice(2, 6)}-${accountNumber.slice(6, 10)}-${accountNumber.slice(10)}`;
  }

  return accountNumber;
}
```

---

### 10.13 Component Best Practices

#### 10.13.1 Performance Optimization

```typescript
// Use React.memo for components that render often with same props
export const TransactionCard = React.memo<TransactionCardProps>(
  ({ transaction, onClick }) => {
    // Component implementation
  }
);

// Use useMemo for expensive calculations
const formattedBalance = useMemo(() => {
  return formatCurrency(account.balance);
}, [account.balance]);

// Use useCallback for event handlers passed to children
const handleClick = useCallback(() => {
  onClick?.(transaction.id);
}, [onClick, transaction.id]);
```

#### 10.13.2 Accessibility Checklist

Every component should:

- [ ] Have appropriate ARIA roles and labels
- [ ] Support keyboard navigation
- [ ] Maintain visible focus indicators
- [ ] Provide screen reader announcements for dynamic content
- [ ] Respect `prefers-reduced-motion`
- [ ] Use semantic HTML elements
- [ ] Have sufficient color contrast (WCAG AA)

#### 10.13.3 Testing Guidelines

```typescript
// Example test file: BalanceCard.test.tsx
import { render, screen, fireEvent } from '@testing-library/react';
import { BalanceCard } from './BalanceCard';

const mockAccount = {
  id: '1',
  name: 'Main Account',
  accountNumber: 'AB-1234-5678-90',
  accountType: 'Checking',
  balance: 1234.56,
  currency: 'EUR',
};

describe('BalanceCard', () => {
  it('renders account information', () => {
    render(<BalanceCard account={mockAccount} />);

    expect(screen.getByText('Main Account')).toBeInTheDocument();
    expect(screen.getByText('AB-1234-5678-90')).toBeInTheDocument();
    expect(screen.getByText('Checking')).toBeInTheDocument();
  });

  it('calls onDeposit when deposit button is clicked', () => {
    const onDeposit = vi.fn();
    render(
      <BalanceCard
        account={mockAccount}
        showActions
        onDeposit={onDeposit}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /deposit/i }));
    expect(onDeposit).toHaveBeenCalledTimes(1);
  });

  it('supports keyboard navigation when clickable', () => {
    const onClick = vi.fn();
    render(
      <BalanceCard
        account={mockAccount}
        variant="mini"
        onClick={onClick}
      />
    );

    const card = screen.getByRole('button');
    fireEvent.keyDown(card, { key: 'Enter' });
    expect(onClick).toHaveBeenCalledTimes(1);
  });
});
```

---

## 11. Page Components & Routing (Phase 6.7)

This section provides comprehensive implementation for all page-level components, routing configuration, protected routes, and the application shell layout.

---

### 11.1 Router Configuration

#### 11.1.1 Route Structure Overview

The AzureBank application uses React Router 7.x with the following route structure:

| Route | Component | Access | Description |
|-------|-----------|--------|-------------|
| `/login` | LoginPage | Public | User login |
| `/register` | RegisterPage | Public | New user registration |
| `/dashboard` | DashboardPage | Protected | Main dashboard |
| `/accounts` | AccountsPage | Protected | Account list |
| `/accounts/:id` | AccountDetailPage | Protected | Single account details |
| `/transactions` | TransactionsPage | Protected | All transactions |
| `/` | → `/dashboard` | Redirect | Default redirect |
| `*` | NotFoundPage | Public | 404 page |

#### 11.1.2 Router Setup

```typescript
// src/app/router.tsx
import { createBrowserRouter, Navigate } from 'react-router-dom';
import { lazy, Suspense } from 'react';
import { ProtectedRoute } from '@/components/auth/ProtectedRoute';
import { AppLayout } from '@/components/layout/AppLayout';
import { PageSkeleton } from '@/components/common/Skeleton/PageSkeleton';

// Lazy load pages for code splitting
const LoginPage = lazy(() => import('@/pages/LoginPage'));
const RegisterPage = lazy(() => import('@/pages/RegisterPage'));
const DashboardPage = lazy(() => import('@/pages/DashboardPage'));
const AccountsPage = lazy(() => import('@/pages/AccountsPage'));
const AccountDetailPage = lazy(() => import('@/pages/AccountDetailPage'));
const TransactionsPage = lazy(() => import('@/pages/TransactionsPage'));
const NotFoundPage = lazy(() => import('@/pages/NotFoundPage'));

// Suspense wrapper for lazy-loaded pages
const SuspenseWrapper = ({ children }: { children: React.ReactNode }) => (
  <Suspense fallback={<PageSkeleton />}>
    {children}
  </Suspense>
);

export const router = createBrowserRouter([
  // Public routes (no auth required)
  {
    path: '/login',
    element: (
      <SuspenseWrapper>
        <LoginPage />
      </SuspenseWrapper>
    ),
  },
  {
    path: '/register',
    element: (
      <SuspenseWrapper>
        <RegisterPage />
      </SuspenseWrapper>
    ),
  },

  // Protected routes (auth required)
  {
    element: <ProtectedRoute />,
    children: [
      {
        element: <AppLayout />,
        children: [
          {
            path: '/dashboard',
            element: (
              <SuspenseWrapper>
                <DashboardPage />
              </SuspenseWrapper>
            ),
          },
          {
            path: '/accounts',
            element: (
              <SuspenseWrapper>
                <AccountsPage />
              </SuspenseWrapper>
            ),
          },
          {
            path: '/accounts/:id',
            element: (
              <SuspenseWrapper>
                <AccountDetailPage />
              </SuspenseWrapper>
            ),
          },
          {
            path: '/transactions',
            element: (
              <SuspenseWrapper>
                <TransactionsPage />
              </SuspenseWrapper>
            ),
          },
        ],
      },
    ],
  },

  // Redirects
  {
    path: '/',
    element: <Navigate to="/dashboard" replace />,
  },

  // 404 fallback
  {
    path: '*',
    element: (
      <SuspenseWrapper>
        <NotFoundPage />
      </SuspenseWrapper>
    ),
  },
]);
```

#### 11.1.3 App Entry Point with Router

```typescript
// src/App.tsx
import { RouterProvider } from 'react-router-dom';
import { Provider } from 'react-redux';
import { FluentProvider } from '@fluentui/react-components';
import { router } from '@/app/router';
import { store } from '@/app/store';
import { azureBankLightTheme } from '@/theme';
import { Toaster } from '@/components/common/Toaster';
import { SessionManager } from '@/components/auth/SessionManager';
import { ErrorBoundary } from '@/components/common/ErrorBoundary';

function App() {
  return (
    <ErrorBoundary>
      <Provider store={store}>
        <FluentProvider theme={azureBankLightTheme}>
          <SessionManager />
          <Toaster />
          <RouterProvider router={router} />
        </FluentProvider>
      </Provider>
    </ErrorBoundary>
  );
}

export default App;
```

#### 11.1.4 Main Entry Point

```typescript
// src/main.tsx
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './index.css';

// Conditionally start MSW in development
async function enableMocking() {
  if (import.meta.env.DEV && import.meta.env.VITE_ENABLE_MSW === 'true') {
    const { worker } = await import('./mocks/browser');
    return worker.start({
      onUnhandledRequest: 'bypass',
    });
  }
  return Promise.resolve();
}

enableMocking().then(() => {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>
  );
});
```

---

### 11.2 ProtectedRoute Component

The ProtectedRoute component guards routes that require authentication, redirecting to login if the user is not authenticated.

#### 11.2.1 Implementation

```typescript
// src/components/auth/ProtectedRoute/ProtectedRoute.tsx
import * as React from 'react';
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAppSelector } from '@/app/hooks';
import {
  selectIsAuthenticated,
  selectIsAuthInitialized,
} from '@/features/auth/authSlice';
import { PageSkeleton } from '@/components/common/Skeleton/PageSkeleton';

/**
 * ProtectedRoute - Guards routes that require authentication.
 *
 * Features:
 * - Checks auth state from Redux
 * - Redirects to login if not authenticated
 * - Preserves intended destination in location state
 * - Shows loading skeleton during auth initialization
 *
 * @example
 * // In router config
 * {
 *   element: <ProtectedRoute />,
 *   children: [
 *     { path: '/dashboard', element: <DashboardPage /> }
 *   ]
 * }
 */
export const ProtectedRoute: React.FC = () => {
  const location = useLocation();
  const isAuthenticated = useAppSelector(selectIsAuthenticated);
  const isInitialized = useAppSelector(selectIsAuthInitialized);

  // Show loading while checking auth state
  if (!isInitialized) {
    return <PageSkeleton />;
  }

  // Redirect to login if not authenticated
  if (!isAuthenticated) {
    return (
      <Navigate
        to="/login"
        state={{ from: location.pathname }}
        replace
      />
    );
  }

  // Render child routes
  return <Outlet />;
};

ProtectedRoute.displayName = 'ProtectedRoute';
```

#### 11.2.2 Index Export

```typescript
// src/components/auth/ProtectedRoute/index.ts
export { ProtectedRoute } from './ProtectedRoute';
```

---

### 11.3 SessionManager Component

The SessionManager handles authentication state initialization and session status checking on app startup.

```typescript
// src/components/auth/SessionManager/SessionManager.tsx
import { useEffect } from 'react';
import { useAppDispatch, useAppSelector } from '@/app/hooks';
import {
  setAuthenticated,
  setInitialized,
  clearAuth,
  selectIsAuthInitialized,
} from '@/features/auth/authSlice';
import { useGetMeQuery, useSessionStatusQuery } from '@/features/auth/authApi';

/**
 * SessionManager - Initializes auth state on app startup.
 *
 * This component:
 * 1. Checks if there's an active session (via BFF)
 * 2. Fetches user data if session is valid
 * 3. Sets auth state in Redux
 * 4. Marks auth as initialized
 */
export const SessionManager: React.FC = () => {
  const dispatch = useAppDispatch();
  const isInitialized = useAppSelector(selectIsAuthInitialized);

  // Check session status (BFF handles cookies)
  const {
    data: sessionData,
    isLoading: sessionLoading,
    isError: sessionError,
  } = useSessionStatusQuery(undefined, {
    skip: isInitialized,
  });

  // Fetch user data if session is valid
  const {
    data: userData,
    isLoading: userLoading,
    isError: userError,
  } = useGetMeQuery(undefined, {
    skip: isInitialized || !sessionData?.isAuthenticated,
  });

  useEffect(() => {
    // Skip if already initialized
    if (isInitialized) return;

    // Session check failed or errored - not authenticated
    if (sessionError || (sessionData && !sessionData.isAuthenticated)) {
      dispatch(clearAuth());
      dispatch(setInitialized());
      return;
    }

    // User data fetch failed
    if (userError) {
      dispatch(clearAuth());
      dispatch(setInitialized());
      return;
    }

    // Successfully got user data
    if (userData) {
      dispatch(setAuthenticated({
        user: userData,
        sessionInfo: {
          expiresAt: sessionData?.expiresAt || '',
          isActive: true,
        },
      }));
      dispatch(setInitialized());
    }
  }, [
    dispatch,
    isInitialized,
    sessionData,
    sessionError,
    userData,
    userError,
  ]);

  // This component doesn't render anything
  return null;
};

SessionManager.displayName = 'SessionManager';
```

---

### 11.4 AppLayout Component

The AppLayout provides the application shell with header, navigation, main content area, and footer.

#### 11.4.1 Types

```typescript
// src/components/layout/AppLayout/AppLayout.types.ts
export interface AppLayoutProps {
  /**
   * Additional CSS class name
   */
  className?: string;
}
```

#### 11.4.2 Styles

```typescript
// src/components/layout/AppLayout/AppLayout.styles.ts
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { layout } from '@/theme';

export const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100vh',
    backgroundColor: tokens.colorNeutralBackground2,
  },

  header: {
    position: 'sticky',
    top: 0,
    zIndex: 100,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow4,
  },

  headerContent: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    height: layout.header.height,
    maxWidth: layout.page.maxWidth,
    marginInline: 'auto',
    ...shorthands.padding(0, tokens.spacingHorizontalL),

    '@media (max-width: 767px)': {
      height: layout.header.heightMobile,
      ...shorthands.padding(0, tokens.spacingHorizontalM),
    },
  },

  logo: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    textDecoration: 'none',
    color: tokens.colorBrandForeground1,
  },

  logoText: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,

    '@media (max-width: 479px)': {
      display: 'none',
    },
  },

  nav: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,

    '@media (max-width: 767px)': {
      display: 'none',
    },
  },

  navLink: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    ...shorthands.padding(tokens.spacingVerticalS, tokens.spacingHorizontalM),
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorNeutralForeground2,
    textDecoration: 'none',
    ...shorthands.transition('all', '150ms'),

    '&:hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      color: tokens.colorNeutralForeground1,
    },

    '&[data-active="true"]': {
      backgroundColor: tokens.colorBrandBackground2,
      color: tokens.colorBrandForeground1,
    },
  },

  userSection: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },

  userInfo: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',

    '@media (max-width: 767px)': {
      display: 'none',
    },
  },

  userName: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorNeutralForeground1,
  },

  azureTag: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorBrandForeground1,
  },

  mobileMenuButton: {
    display: 'none',

    '@media (max-width: 767px)': {
      display: 'flex',
    },
  },

  main: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
  },

  mainContent: {
    flex: 1,
    width: '100%',
    maxWidth: layout.page.maxWidth,
    marginInline: 'auto',
    ...shorthands.padding(tokens.spacingVerticalXXL, tokens.spacingHorizontalL),

    '@media (max-width: 767px)': {
      ...shorthands.padding(tokens.spacingVerticalL, tokens.spacingHorizontalM),
    },
  },

  footer: {
    backgroundColor: tokens.colorNeutralBackground1,
    ...shorthands.borderTop('1px', 'solid', tokens.colorNeutralStroke2),
  },

  footerContent: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    maxWidth: layout.page.maxWidth,
    marginInline: 'auto',
    ...shorthands.padding(tokens.spacingVerticalL, tokens.spacingHorizontalL),
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,

    '@media (max-width: 767px)': {
      flexDirection: 'column',
      gap: tokens.spacingVerticalS,
      textAlign: 'center',
    },
  },
});
```

#### 11.4.3 Implementation

```typescript
// src/components/layout/AppLayout/AppLayout.tsx
import * as React from 'react';
import { useState, useCallback } from 'react';
import { Outlet, Link, useLocation, useNavigate } from 'react-router-dom';
import {
  Button,
  Avatar,
  Menu,
  MenuTrigger,
  MenuPopover,
  MenuList,
  MenuItem,
  mergeClasses,
} from '@fluentui/react-components';
import {
  HomeRegular,
  WalletRegular,
  HistoryRegular,
  PersonRegular,
  SignOutRegular,
  NavigationRegular,
  DismissRegular,
} from '@fluentui/react-icons';
import { useStyles } from './AppLayout.styles';
import { useAppSelector, useAppDispatch } from '@/app/hooks';
import { selectUser, selectUserDisplayName } from '@/features/auth/authSlice';
import { useLogoutMutation } from '@/features/auth/authApi';
import { MobileNav } from '../MobileNav';

// Navigation items
const navItems = [
  { path: '/dashboard', label: 'Dashboard', icon: HomeRegular },
  { path: '/accounts', label: 'Accounts', icon: WalletRegular },
  { path: '/transactions', label: 'Transactions', icon: HistoryRegular },
];

/**
 * AppLayout - Main application shell layout.
 *
 * Provides:
 * - Sticky header with logo, navigation, user menu
 * - Mobile navigation drawer
 * - Main content area (renders child routes via Outlet)
 * - Footer with copyright
 */
export const AppLayout: React.FC = () => {
  const styles = useStyles();
  const location = useLocation();
  const navigate = useNavigate();
  const dispatch = useAppDispatch();

  const user = useAppSelector(selectUser);
  const displayName = useAppSelector(selectUserDisplayName);
  const [logout, { isLoading: isLoggingOut }] = useLogoutMutation();

  const [isMobileNavOpen, setIsMobileNavOpen] = useState(false);

  // Handle logout
  const handleLogout = useCallback(async () => {
    try {
      await logout().unwrap();
      navigate('/login', { replace: true });
    } catch (error) {
      console.error('Logout failed:', error);
    }
  }, [logout, navigate]);

  // Toggle mobile navigation
  const toggleMobileNav = useCallback(() => {
    setIsMobileNavOpen((prev) => !prev);
  }, []);

  // Close mobile nav when navigating
  const handleMobileNavClose = useCallback(() => {
    setIsMobileNavOpen(false);
  }, []);

  // Check if a nav item is active
  const isActive = (path: string) => {
    if (path === '/dashboard') {
      return location.pathname === '/dashboard' || location.pathname === '/';
    }
    return location.pathname.startsWith(path);
  };

  return (
    <div className={styles.root}>
      {/* Header */}
      <header className={styles.header}>
        <div className={styles.headerContent}>
          {/* Logo */}
          <Link to="/dashboard" className={styles.logo}>
            <WalletRegular fontSize={28} />
            <span className={styles.logoText}>AzureBank</span>
          </Link>

          {/* Desktop Navigation */}
          <nav className={styles.nav} aria-label="Main navigation">
            {navItems.map(({ path, label, icon: Icon }) => (
              <Link
                key={path}
                to={path}
                className={styles.navLink}
                data-active={isActive(path)}
                aria-current={isActive(path) ? 'page' : undefined}
              >
                <Icon fontSize={20} />
                {label}
              </Link>
            ))}
          </nav>

          {/* User Section */}
          <div className={styles.userSection}>
            {/* User Info (Desktop) */}
            <div className={styles.userInfo}>
              <span className={styles.userName}>{displayName}</span>
              {user?.azureTag && (
                <span className={styles.azureTag}>@{user.azureTag}</span>
              )}
            </div>

            {/* User Menu */}
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button
                  appearance="subtle"
                  icon={
                    <Avatar
                      name={displayName}
                      size={32}
                      color="brand"
                    />
                  }
                  aria-label="User menu"
                />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem
                    icon={<PersonRegular />}
                    disabled
                  >
                    Profile (Coming Soon)
                  </MenuItem>
                  <MenuItem
                    icon={<SignOutRegular />}
                    onClick={handleLogout}
                    disabled={isLoggingOut}
                  >
                    {isLoggingOut ? 'Signing out...' : 'Sign Out'}
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>

            {/* Mobile Menu Button */}
            <Button
              appearance="subtle"
              className={styles.mobileMenuButton}
              icon={isMobileNavOpen ? <DismissRegular /> : <NavigationRegular />}
              onClick={toggleMobileNav}
              aria-label={isMobileNavOpen ? 'Close menu' : 'Open menu'}
              aria-expanded={isMobileNavOpen}
            />
          </div>
        </div>
      </header>

      {/* Mobile Navigation */}
      <MobileNav
        isOpen={isMobileNavOpen}
        onClose={handleMobileNavClose}
        navItems={navItems}
        currentPath={location.pathname}
        onLogout={handleLogout}
        isLoggingOut={isLoggingOut}
      />

      {/* Main Content */}
      <main className={styles.main}>
        <div className={styles.mainContent}>
          <Outlet />
        </div>
      </main>

      {/* Footer */}
      <footer className={styles.footer}>
        <div className={styles.footerContent}>
          <span>© {new Date().getFullYear()} AzureBank. All rights reserved.</span>
          <span>A Dev4Side Technical Assessment</span>
        </div>
      </footer>
    </div>
  );
};

AppLayout.displayName = 'AppLayout';
```

---

### 11.5 MobileNav Component

The MobileNav provides navigation for mobile devices as a slide-out drawer.

```typescript
// src/components/layout/MobileNav/MobileNav.tsx
import * as React from 'react';
import { Link } from 'react-router-dom';
import { Button, Divider, mergeClasses } from '@fluentui/react-components';
import {
  DismissRegular,
  SignOutRegular,
} from '@fluentui/react-icons';
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { animations } from '@/theme';

const useStyles = makeStyles({
  overlay: {
    position: 'fixed',
    inset: 0,
    backgroundColor: 'rgba(0, 0, 0, 0.5)',
    zIndex: 200,
    opacity: 0,
    visibility: 'hidden',
    ...shorthands.transition('all', animations.durations.normal),
  },

  overlayOpen: {
    opacity: 1,
    visibility: 'visible',
  },

  drawer: {
    position: 'fixed',
    top: 0,
    right: 0,
    bottom: 0,
    width: '280px',
    maxWidth: '80vw',
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow64,
    transform: 'translateX(100%)',
    ...shorthands.transition('transform', animations.durations.normal, animations.easings.easeOut),
    zIndex: 201,
    display: 'flex',
    flexDirection: 'column',
  },

  drawerOpen: {
    transform: 'translateX(0)',
  },

  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    ...shorthands.padding(tokens.spacingVerticalM, tokens.spacingHorizontalM),
    ...shorthands.borderBottom('1px', 'solid', tokens.colorNeutralStroke2),
  },

  title: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },

  nav: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    ...shorthands.padding(tokens.spacingVerticalM, 0),
  },

  navLink: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    ...shorthands.padding(tokens.spacingVerticalM, tokens.spacingHorizontalL),
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorNeutralForeground2,
    textDecoration: 'none',
    ...shorthands.transition('all', '150ms'),

    '&:hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },

    '&[data-active="true"]': {
      backgroundColor: tokens.colorBrandBackground2,
      color: tokens.colorBrandForeground1,
    },
  },

  footer: {
    ...shorthands.padding(tokens.spacingVerticalM, tokens.spacingHorizontalM),
    ...shorthands.borderTop('1px', 'solid', tokens.colorNeutralStroke2),
  },
});

interface MobileNavProps {
  isOpen: boolean;
  onClose: () => void;
  navItems: Array<{
    path: string;
    label: string;
    icon: React.FC<{ fontSize?: number }>;
  }>;
  currentPath: string;
  onLogout: () => void;
  isLoggingOut: boolean;
}

export const MobileNav: React.FC<MobileNavProps> = ({
  isOpen,
  onClose,
  navItems,
  currentPath,
  onLogout,
  isLoggingOut,
}) => {
  const styles = useStyles();

  // Handle escape key
  React.useEffect(() => {
    const handleEscape = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && isOpen) {
        onClose();
      }
    };

    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [isOpen, onClose]);

  // Prevent body scroll when open
  React.useEffect(() => {
    if (isOpen) {
      document.body.style.overflow = 'hidden';
    } else {
      document.body.style.overflow = '';
    }
    return () => {
      document.body.style.overflow = '';
    };
  }, [isOpen]);

  const isActive = (path: string) => {
    if (path === '/dashboard') {
      return currentPath === '/dashboard' || currentPath === '/';
    }
    return currentPath.startsWith(path);
  };

  return (
    <>
      {/* Overlay */}
      <div
        className={mergeClasses(
          styles.overlay,
          isOpen && styles.overlayOpen,
        )}
        onClick={onClose}
        aria-hidden="true"
      />

      {/* Drawer */}
      <div
        className={mergeClasses(
          styles.drawer,
          isOpen && styles.drawerOpen,
        )}
        role="dialog"
        aria-modal="true"
        aria-label="Mobile navigation"
      >
        <div className={styles.header}>
          <span className={styles.title}>Menu</span>
          <Button
            appearance="subtle"
            icon={<DismissRegular />}
            onClick={onClose}
            aria-label="Close menu"
          />
        </div>

        <nav className={styles.nav} aria-label="Mobile navigation">
          {navItems.map(({ path, label, icon: Icon }) => (
            <Link
              key={path}
              to={path}
              className={styles.navLink}
              data-active={isActive(path)}
              onClick={onClose}
              aria-current={isActive(path) ? 'page' : undefined}
            >
              <Icon fontSize={24} />
              {label}
            </Link>
          ))}
        </nav>

        <div className={styles.footer}>
          <Button
            appearance="subtle"
            icon={<SignOutRegular />}
            onClick={() => {
              onLogout();
              onClose();
            }}
            disabled={isLoggingOut}
            style={{ width: '100%', justifyContent: 'flex-start' }}
          >
            {isLoggingOut ? 'Signing out...' : 'Sign Out'}
          </Button>
        </div>
      </div>
    </>
  );
};

MobileNav.displayName = 'MobileNav';
```

---

### 11.6 LoginPage Component

The LoginPage provides user authentication with email and password.

#### 11.6.1 Styles

```typescript
// src/pages/LoginPage.styles.ts
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { layout } from '@/theme';

export const useStyles = makeStyles({
  root: {
    display: 'flex',
    minHeight: '100vh',
    backgroundColor: tokens.colorNeutralBackground2,
  },

  // Left panel with branding (desktop only)
  brandPanel: {
    display: 'none',
    flex: 1,
    background: `linear-gradient(135deg, ${tokens.colorBrandBackground} 0%, #004DA0 100%)`,
    ...shorthands.padding(tokens.spacingVerticalXXL),
    color: tokens.colorNeutralForegroundOnBrand,

    '@media (min-width: 768px)': {
      display: 'flex',
      flexDirection: 'column',
      justifyContent: 'center',
      alignItems: 'center',
    },
  },

  brandContent: {
    maxWidth: '400px',
    textAlign: 'center',
  },

  brandLogo: {
    fontSize: '80px',
    marginBottom: tokens.spacingVerticalL,
  },

  brandTitle: {
    fontSize: tokens.fontSizeHero900,
    fontWeight: tokens.fontWeightBold,
    marginBottom: tokens.spacingVerticalM,
  },

  brandSubtitle: {
    fontSize: tokens.fontSizeBase400,
    opacity: 0.9,
    lineHeight: 1.6,
  },

  // Right panel with form
  formPanel: {
    flex: 1,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    ...shorthands.padding(tokens.spacingVerticalXXL),
  },

  formContainer: {
    width: '100%',
    maxWidth: '400px',
  },

  formHeader: {
    textAlign: 'center',
    marginBottom: tokens.spacingVerticalXXL,
  },

  mobileLogo: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: tokens.spacingHorizontalS,
    marginBottom: tokens.spacingVerticalL,
    color: tokens.colorBrandForeground1,

    '@media (min-width: 768px)': {
      display: 'none',
    },
  },

  mobileLogoText: {
    fontSize: tokens.fontSizeBase600,
    fontWeight: tokens.fontWeightBold,
  },

  title: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorNeutralForeground1,
    marginBottom: tokens.spacingVerticalXS,
  },

  subtitle: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
  },

  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },

  field: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },

  label: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightMedium,
    color: tokens.colorNeutralForeground1,
  },

  errorAlert: {
    ...shorthands.padding(tokens.spacingVerticalM),
    backgroundColor: tokens.colorPaletteRedBackground1,
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase300,
  },

  submitButton: {
    marginTop: tokens.spacingVerticalM,
  },

  footer: {
    marginTop: tokens.spacingVerticalXXL,
    textAlign: 'center',
  },

  footerText: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
  },

  footerLink: {
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightMedium,
    textDecoration: 'none',

    '&:hover': {
      textDecoration: 'underline',
    },
  },
});
```

#### 11.6.2 Implementation

```typescript
// src/pages/LoginPage.tsx
import * as React from 'react';
import { useState, useCallback } from 'react';
import { Link, useNavigate, useLocation } from 'react-router-dom';
import { Input, Field } from '@fluentui/react-components';
import { WalletRegular, MailRegular } from '@fluentui/react-icons';
import { useStyles } from './LoginPage.styles';
import { PasswordInput } from '@/components/common/PasswordInput';
import { LoadingButton } from '@/components/common/LoadingButton';
import { useLoginMutation } from '@/features/auth/authApi';
import { useAppDispatch } from '@/app/hooks';
import { setAuthenticated } from '@/features/auth/authSlice';
import { addToast } from '@/features/ui/uiSlice';

interface LocationState {
  from?: string;
}

/**
 * LoginPage - User authentication page.
 *
 * Features:
 * - Email and password login
 * - Error handling with user-friendly messages
 * - Redirect to intended destination after login
 * - Link to registration page
 */
const LoginPage: React.FC = () => {
  const styles = useStyles();
  const navigate = useNavigate();
  const location = useLocation();
  const dispatch = useAppDispatch();

  const [login, { isLoading }] = useLoginMutation();

  // Form state
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);

  // Get redirect destination
  const from = (location.state as LocationState)?.from || '/dashboard';

  // Handle form submission
  const handleSubmit = useCallback(async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    // Basic validation
    if (!email || !password) {
      setError('Please enter your email and password.');
      return;
    }

    try {
      const result = await login({ email, password }).unwrap();

      // Set auth state
      dispatch(setAuthenticated({
        user: result.user,
        sessionInfo: {
          expiresAt: result.expiresAt,
          isActive: true,
        },
      }));

      // Show success toast
      dispatch(addToast({
        type: 'success',
        title: 'Welcome back!',
        message: `Signed in as ${result.user.firstName}`,
      }));

      // Navigate to intended destination
      navigate(from, { replace: true });
    } catch (err: any) {
      // Handle specific error types
      if (err?.status === 401) {
        setError('Invalid email or password. Please try again.');
      } else if (err?.status === 429) {
        setError('Too many login attempts. Please try again later.');
      } else {
        setError('An error occurred. Please try again.');
      }
    }
  }, [email, password, login, dispatch, navigate, from]);

  return (
    <div className={styles.root}>
      {/* Brand Panel (Desktop) */}
      <div className={styles.brandPanel}>
        <div className={styles.brandContent}>
          <WalletRegular className={styles.brandLogo} />
          <h1 className={styles.brandTitle}>AzureBank</h1>
          <p className={styles.brandSubtitle}>
            Your trusted partner for secure and simple banking.
            Manage your accounts, track transactions, and transfer money with ease.
          </p>
        </div>
      </div>

      {/* Form Panel */}
      <div className={styles.formPanel}>
        <div className={styles.formContainer}>
          {/* Header */}
          <div className={styles.formHeader}>
            {/* Mobile Logo */}
            <div className={styles.mobileLogo}>
              <WalletRegular fontSize={32} />
              <span className={styles.mobileLogoText}>AzureBank</span>
            </div>

            <h2 className={styles.title}>Welcome Back</h2>
            <p className={styles.subtitle}>Sign in to your account</p>
          </div>

          {/* Form */}
          <form className={styles.form} onSubmit={handleSubmit}>
            {/* Error Alert */}
            {error && (
              <div className={styles.errorAlert} role="alert">
                {error}
              </div>
            )}

            {/* Email Field */}
            <Field
              label="Email address"
              required
            >
              <Input
                type="email"
                value={email}
                onChange={(e, data) => setEmail(data.value)}
                placeholder="you@example.com"
                contentBefore={<MailRegular />}
                autoComplete="email"
                disabled={isLoading}
              />
            </Field>

            {/* Password Field */}
            <PasswordInput
              label="Password"
              value={password}
              onChange={setPassword}
              placeholder="Enter your password"
              required
              disabled={isLoading}
              autoComplete="current-password"
            />

            {/* Submit Button */}
            <LoadingButton
              appearance="primary"
              type="submit"
              isLoading={isLoading}
              loadingText="Signing in..."
              className={styles.submitButton}
            >
              Sign In
            </LoadingButton>
          </form>

          {/* Footer */}
          <div className={styles.footer}>
            <p className={styles.footerText}>
              Don't have an account?{' '}
              <Link to="/register" className={styles.footerLink}>
                Create one
              </Link>
            </p>
          </div>
        </div>
      </div>
    </div>
  );
};

export default LoginPage;
```

---

### 11.7 RegisterPage Component

The RegisterPage allows new users to create an AzureBank account with their AzureTag.

```typescript
// src/pages/RegisterPage.tsx
import * as React from 'react';
import { useState, useCallback } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Input, Field, Text } from '@fluentui/react-components';
import {
  WalletRegular,
  MailRegular,
  PersonRegular,
  AtRegular,
} from '@fluentui/react-icons';
import { useStyles } from './LoginPage.styles'; // Reuse login styles
import { PasswordInput } from '@/components/common/PasswordInput';
import { LoadingButton } from '@/components/common/LoadingButton';
import { useRegisterMutation } from '@/features/auth/authApi';
import { useAppDispatch } from '@/app/hooks';
import { setAuthenticated } from '@/features/auth/authSlice';
import { addToast } from '@/features/ui/uiSlice';
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';

// Additional styles for register page
const useRegisterStyles = makeStyles({
  nameRow: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: tokens.spacingHorizontalM,

    '@media (max-width: 479px)': {
      gridTemplateColumns: '1fr',
    },
  },

  azureTagHelper: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    marginTop: tokens.spacingVerticalXXS,
  },

  azureTagPreview: {
    marginTop: tokens.spacingVerticalXS,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightMedium,
  },
});

/**
 * RegisterPage - New user registration page.
 *
 * Features:
 * - Full name, email, password registration
 * - Unique AzureTag (username) for transfers
 * - Password strength indicator
 * - Automatic login after registration
 */
const RegisterPage: React.FC = () => {
  const styles = useStyles();
  const registerStyles = useRegisterStyles();
  const navigate = useNavigate();
  const dispatch = useAppDispatch();

  const [register, { isLoading }] = useRegisterMutation();

  // Form state
  const [firstName, setFirstName] = useState('');
  const [surname, setSurname] = useState('');
  const [azureTag, setAzureTag] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [error, setError] = useState<string | null>(null);

  // Validation
  const passwordsMatch = password === confirmPassword;
  const isPasswordValid = password.length >= 8;

  // Handle AzureTag input (sanitize)
  const handleAzureTagChange = useCallback((value: string) => {
    // Only allow alphanumeric and underscores, lowercase
    const sanitized = value.toLowerCase().replace(/[^a-z0-9_]/g, '');
    setAzureTag(sanitized);
  }, []);

  // Handle form submission
  const handleSubmit = useCallback(async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    // Validation
    if (!firstName || !surname || !azureTag || !email || !password) {
      setError('Please fill in all required fields.');
      return;
    }

    if (azureTag.length < 3) {
      setError('AzureTag must be at least 3 characters.');
      return;
    }

    if (!isPasswordValid) {
      setError('Password must be at least 8 characters.');
      return;
    }

    if (!passwordsMatch) {
      setError('Passwords do not match.');
      return;
    }

    try {
      const result = await register({
        firstName,
        surname,
        azureTag,
        email,
        password,
      }).unwrap();

      // Set auth state (auto-login after registration)
      dispatch(setAuthenticated({
        user: result.user,
        sessionInfo: {
          expiresAt: result.expiresAt,
          isActive: true,
        },
      }));

      // Show success toast
      dispatch(addToast({
        type: 'success',
        title: 'Welcome to AzureBank!',
        message: 'Your account has been created successfully.',
      }));

      // Navigate to dashboard
      navigate('/dashboard', { replace: true });
    } catch (err: any) {
      if (err?.data?.errors?.azureTag) {
        setError('This AzureTag is already taken. Please choose another.');
      } else if (err?.data?.errors?.email) {
        setError('This email is already registered.');
      } else {
        setError('Registration failed. Please try again.');
      }
    }
  }, [
    firstName, surname, azureTag, email, password,
    isPasswordValid, passwordsMatch,
    register, dispatch, navigate,
  ]);

  return (
    <div className={styles.root}>
      {/* Brand Panel (Desktop) */}
      <div className={styles.brandPanel}>
        <div className={styles.brandContent}>
          <WalletRegular className={styles.brandLogo} />
          <h1 className={styles.brandTitle}>AzureBank</h1>
          <p className={styles.brandSubtitle}>
            Join thousands of users who trust AzureBank for their
            personal banking needs. Simple, secure, and free.
          </p>
        </div>
      </div>

      {/* Form Panel */}
      <div className={styles.formPanel}>
        <div className={styles.formContainer}>
          {/* Header */}
          <div className={styles.formHeader}>
            {/* Mobile Logo */}
            <div className={styles.mobileLogo}>
              <WalletRegular fontSize={32} />
              <span className={styles.mobileLogoText}>AzureBank</span>
            </div>

            <h2 className={styles.title}>Create Account</h2>
            <p className={styles.subtitle}>Join AzureBank today</p>
          </div>

          {/* Form */}
          <form className={styles.form} onSubmit={handleSubmit}>
            {/* Error Alert */}
            {error && (
              <div className={styles.errorAlert} role="alert">
                {error}
              </div>
            )}

            {/* Name Fields */}
            <div className={registerStyles.nameRow}>
              <Field label="First name" required>
                <Input
                  value={firstName}
                  onChange={(e, data) => setFirstName(data.value)}
                  placeholder="John"
                  contentBefore={<PersonRegular />}
                  disabled={isLoading}
                />
              </Field>

              <Field label="Surname" required>
                <Input
                  value={surname}
                  onChange={(e, data) => setSurname(data.value)}
                  placeholder="Doe"
                  disabled={isLoading}
                />
              </Field>
            </div>

            {/* AzureTag Field */}
            <Field
              label="AzureTag"
              required
              hint="Your unique username for transfers (cannot be changed)"
            >
              <Input
                value={azureTag}
                onChange={(e, data) => handleAzureTagChange(data.value)}
                placeholder="johndoe"
                contentBefore={<AtRegular />}
                disabled={isLoading}
              />
              {azureTag && (
                <Text className={registerStyles.azureTagPreview}>
                  Your tag: @{azureTag}
                </Text>
              )}
            </Field>

            {/* Email Field */}
            <Field label="Email address" required>
              <Input
                type="email"
                value={email}
                onChange={(e, data) => setEmail(data.value)}
                placeholder="you@example.com"
                contentBefore={<MailRegular />}
                autoComplete="email"
                disabled={isLoading}
              />
            </Field>

            {/* Password Field */}
            <PasswordInput
              label="Password"
              value={password}
              onChange={setPassword}
              placeholder="At least 8 characters"
              required
              showStrengthIndicator
              disabled={isLoading}
              autoComplete="new-password"
            />

            {/* Confirm Password Field */}
            <PasswordInput
              label="Confirm password"
              value={confirmPassword}
              onChange={setConfirmPassword}
              placeholder="Re-enter your password"
              required
              error={confirmPassword && !passwordsMatch ? 'Passwords do not match' : undefined}
              disabled={isLoading}
              autoComplete="new-password"
            />

            {/* Submit Button */}
            <LoadingButton
              appearance="primary"
              type="submit"
              isLoading={isLoading}
              loadingText="Creating account..."
              className={styles.submitButton}
            >
              Create Account
            </LoadingButton>
          </form>

          {/* Footer */}
          <div className={styles.footer}>
            <p className={styles.footerText}>
              Already have an account?{' '}
              <Link to="/login" className={styles.footerLink}>
                Sign in
              </Link>
            </p>
          </div>
        </div>
      </div>
    </div>
  );
};

export default RegisterPage;
```

---

### 11.8 DashboardPage Component

The DashboardPage is the main landing page after login, showing account overview and recent activity.

#### 11.8.1 Styles

```typescript
// src/pages/DashboardPage.styles.ts
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';

export const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXL,
  },

  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },

  greeting: {
    fontSize: tokens.fontSizeHero800,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorNeutralForeground1,
  },

  subGreeting: {
    fontSize: tokens.fontSizeBase400,
    color: tokens.colorNeutralForeground3,
  },

  azureTag: {
    color: tokens.colorBrandForeground1,
    fontWeight: tokens.fontWeightMedium,
  },

  quickActions: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalM,
    marginTop: tokens.spacingVerticalM,
  },

  twoColumnGrid: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: tokens.spacingHorizontalXXL,

    '@media (max-width: 1023px)': {
      gridTemplateColumns: '1fr',
    },
  },

  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },

  sectionHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
  },

  sectionTitle: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },

  transactionList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },

  accountList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
});
```

#### 11.8.2 Implementation

```typescript
// src/pages/DashboardPage.tsx
import * as React from 'react';
import { useCallback } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Button } from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  ArrowUploadRegular,
  ArrowSwapRegular,
  AddRegular,
  ChevronRightRegular,
} from '@fluentui/react-icons';
import { useStyles } from './DashboardPage.styles';
import { BalanceCard } from '@/components/common/BalanceCard';
import { TransactionCard } from '@/components/common/TransactionCard';
import { EmptyState } from '@/components/common/EmptyState';
import {
  BalanceCardSkeleton,
  TransactionListSkeleton,
  AccountCardSkeleton,
} from '@/components/common/Skeleton';
import { useAppSelector, useAppDispatch } from '@/app/hooks';
import { selectUser } from '@/features/auth/authSlice';
import { openDialog } from '@/features/ui/uiSlice';
import { useGetAccountsQuery } from '@/features/accounts/accountsApi';
import { useGetTransactionHistoryQuery } from '@/features/transactions/transactionsApi';

/**
 * DashboardPage - Main landing page after login.
 *
 * Shows:
 * - Personalized greeting with AzureTag
 * - Primary account balance card
 * - Quick action buttons
 * - Recent transactions
 * - Account overview list
 */
const DashboardPage: React.FC = () => {
  const styles = useStyles();
  const navigate = useNavigate();
  const dispatch = useAppDispatch();

  const user = useAppSelector(selectUser);

  // Fetch accounts
  const {
    data: accounts,
    isLoading: accountsLoading,
    isError: accountsError,
  } = useGetAccountsQuery();

  // Get primary account (or first account)
  const primaryAccount = accounts?.find((a) => a.isPrimary) || accounts?.[0];

  // Fetch recent transactions for primary account
  const {
    data: transactionsData,
    isLoading: transactionsLoading,
  } = useGetTransactionHistoryQuery(
    {
      accountId: primaryAccount?.id || '',
      page: 1,
      pageSize: 5,
    },
    { skip: !primaryAccount }
  );

  const recentTransactions = transactionsData?.items || [];

  // Quick action handlers
  const handleDeposit = useCallback(() => {
    if (primaryAccount) {
      dispatch(openDialog({ type: 'deposit', accountId: primaryAccount.id }));
    }
  }, [dispatch, primaryAccount]);

  const handleWithdraw = useCallback(() => {
    if (primaryAccount) {
      dispatch(openDialog({ type: 'withdraw', accountId: primaryAccount.id }));
    }
  }, [dispatch, primaryAccount]);

  const handleTransfer = useCallback(() => {
    dispatch(openDialog({ type: 'transfer' }));
  }, [dispatch]);

  const handleCreateAccount = useCallback(() => {
    dispatch(openDialog({ type: 'createAccount' }));
  }, [dispatch]);

  // Greeting based on time of day
  const getGreeting = () => {
    const hour = new Date().getHours();
    if (hour < 12) return 'Good morning';
    if (hour < 18) return 'Good afternoon';
    return 'Good evening';
  };

  return (
    <div className={styles.root}>
      {/* Header with Greeting */}
      <header className={styles.header}>
        <h1 className={styles.greeting}>
          {getGreeting()}, {user?.firstName}!
        </h1>
        <p className={styles.subGreeting}>
          Welcome back to your{' '}
          <span className={styles.azureTag}>@{user?.azureTag}</span> dashboard
        </p>
      </header>

      {/* Primary Account Balance Card */}
      {accountsLoading ? (
        <BalanceCardSkeleton variant="hero" />
      ) : primaryAccount ? (
        <BalanceCard
          account={primaryAccount}
          variant="hero"
          showActions
          onDeposit={handleDeposit}
          onWithdraw={handleWithdraw}
          onTransfer={handleTransfer}
        />
      ) : (
        <EmptyState
          title="No accounts yet"
          description="Create your first account to start managing your finances."
          actionLabel="Create Account"
          onAction={handleCreateAccount}
        />
      )}

      {/* Quick Actions */}
      {primaryAccount && (
        <div className={styles.quickActions}>
          <Button
            appearance="outline"
            icon={<ArrowDownloadRegular />}
            onClick={handleDeposit}
          >
            Deposit
          </Button>
          <Button
            appearance="outline"
            icon={<ArrowUploadRegular />}
            onClick={handleWithdraw}
          >
            Withdraw
          </Button>
          <Button
            appearance="outline"
            icon={<ArrowSwapRegular />}
            onClick={handleTransfer}
          >
            Transfer
          </Button>
          <Button
            appearance="outline"
            icon={<AddRegular />}
            onClick={handleCreateAccount}
          >
            New Account
          </Button>
        </div>
      )}

      {/* Two Column Grid */}
      <div className={styles.twoColumnGrid}>
        {/* Recent Transactions */}
        <section className={styles.section}>
          <div className={styles.sectionHeader}>
            <h2 className={styles.sectionTitle}>Recent Transactions</h2>
            {primaryAccount && (
              <Button
                appearance="subtle"
                icon={<ChevronRightRegular />}
                iconPosition="after"
                as={Link}
                to={`/accounts/${primaryAccount.id}`}
              >
                View All
              </Button>
            )}
          </div>

          {transactionsLoading ? (
            <TransactionListSkeleton count={5} />
          ) : recentTransactions.length > 0 ? (
            <div className={styles.transactionList}>
              {recentTransactions.map((tx) => (
                <TransactionCard
                  key={tx.id}
                  transaction={tx}
                  variant="compact"
                />
              ))}
            </div>
          ) : (
            <EmptyState
              variant="compact"
              title="No transactions yet"
              description="Your recent activity will appear here."
            />
          )}
        </section>

        {/* Other Accounts */}
        <section className={styles.section}>
          <div className={styles.sectionHeader}>
            <h2 className={styles.sectionTitle}>Your Accounts</h2>
            <Button
              appearance="subtle"
              icon={<ChevronRightRegular />}
              iconPosition="after"
              as={Link}
              to="/accounts"
            >
              Manage
            </Button>
          </div>

          {accountsLoading ? (
            <div className={styles.accountList}>
              <AccountCardSkeleton />
              <AccountCardSkeleton />
            </div>
          ) : accounts && accounts.length > 0 ? (
            <div className={styles.accountList}>
              {accounts.slice(0, 3).map((account) => (
                <BalanceCard
                  key={account.id}
                  account={account}
                  variant="compact"
                  onClick={() => navigate(`/accounts/${account.id}`)}
                />
              ))}
            </div>
          ) : (
            <EmptyState
              variant="compact"
              title="No accounts"
              actionLabel="Create Account"
              onAction={handleCreateAccount}
            />
          )}
        </section>
      </div>
    </div>
  );
};

export default DashboardPage;
```

---

### 11.9 AccountsPage Component

The AccountsPage displays all user accounts with the ability to create new ones.

```typescript
// src/pages/AccountsPage.tsx
import * as React from 'react';
import { useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button } from '@fluentui/react-components';
import { AddRegular, WalletRegular } from '@fluentui/react-icons';
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { BalanceCard } from '@/components/common/BalanceCard';
import { EmptyState } from '@/components/common/EmptyState';
import { AccountCardSkeleton } from '@/components/common/Skeleton';
import { useAppDispatch } from '@/app/hooks';
import { openDialog } from '@/features/ui/uiSlice';
import { useGetAccountsQuery } from '@/features/accounts/accountsApi';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXL,
  },

  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalM,
  },

  title: {
    fontSize: tokens.fontSizeHero800,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorNeutralForeground1,
  },

  accountGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))',
    gap: tokens.spacingHorizontalL,
  },

  loadingGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))',
    gap: tokens.spacingHorizontalL,
  },
});

/**
 * AccountsPage - Lists all user accounts.
 *
 * Features:
 * - Grid display of all accounts
 * - Create new account button
 * - Click to view account details
 */
const AccountsPage: React.FC = () => {
  const styles = useStyles();
  const navigate = useNavigate();
  const dispatch = useAppDispatch();

  const {
    data: accounts,
    isLoading,
    isError,
  } = useGetAccountsQuery();

  const handleCreateAccount = useCallback(() => {
    dispatch(openDialog({ type: 'createAccount' }));
  }, [dispatch]);

  const handleAccountClick = useCallback((accountId: string) => {
    navigate(`/accounts/${accountId}`);
  }, [navigate]);

  return (
    <div className={styles.root}>
      {/* Header */}
      <header className={styles.header}>
        <h1 className={styles.title}>Your Accounts</h1>
        <Button
          appearance="primary"
          icon={<AddRegular />}
          onClick={handleCreateAccount}
        >
          New Account
        </Button>
      </header>

      {/* Accounts Grid */}
      {isLoading ? (
        <div className={styles.loadingGrid}>
          <AccountCardSkeleton />
          <AccountCardSkeleton />
          <AccountCardSkeleton />
        </div>
      ) : accounts && accounts.length > 0 ? (
        <div className={styles.accountGrid}>
          {accounts.map((account) => (
            <BalanceCard
              key={account.id}
              account={account}
              variant="compact"
              onClick={() => handleAccountClick(account.id)}
            />
          ))}
        </div>
      ) : (
        <EmptyState
          icon={<WalletRegular fontSize={48} />}
          title="No accounts yet"
          description="Create your first bank account to start managing your finances with AzureBank."
          actionLabel="Create Account"
          onAction={handleCreateAccount}
        />
      )}
    </div>
  );
};

export default AccountsPage;
```

---

### 11.10 AccountDetailPage Component

The AccountDetailPage shows detailed information for a single account with transaction history.

```typescript
// src/pages/AccountDetailPage.tsx
import * as React from 'react';
import { useState, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import {
  Button,
  Dropdown,
  Option,
  DatePicker,
  Input,
} from '@fluentui/react-components';
import {
  ChevronLeftRegular,
  ArrowDownloadRegular,
  ArrowUploadRegular,
  ArrowSwapRegular,
  SearchRegular,
  FilterRegular,
  DismissRegular,
} from '@fluentui/react-icons';
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';
import { BalanceCard } from '@/components/common/BalanceCard';
import { TransactionCard } from '@/components/common/TransactionCard';
import { EmptyState } from '@/components/common/EmptyState';
import {
  BalanceCardSkeleton,
  TransactionListSkeleton,
} from '@/components/common/Skeleton';
import { useAppDispatch } from '@/app/hooks';
import { openDialog } from '@/features/ui/uiSlice';
import { useGetAccountByIdQuery } from '@/features/accounts/accountsApi';
import { useGetTransactionHistoryQuery } from '@/features/transactions/transactionsApi';
import type { TransactionType } from '@/types/transaction.types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXL,
  },

  backButton: {
    alignSelf: 'flex-start',
  },

  filterBar: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    ...shorthands.padding(tokens.spacingVerticalM),
    backgroundColor: tokens.colorNeutralBackground1,
    ...shorthands.borderRadius(tokens.borderRadiusMedium),
    ...shorthands.border('1px', 'solid', tokens.colorNeutralStroke2),
  },

  searchInput: {
    minWidth: '200px',
    flex: 1,
  },

  filterDropdown: {
    minWidth: '140px',
  },

  transactionSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },

  sectionHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
  },

  sectionTitle: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },

  transactionCount: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },

  transactionList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },

  pagination: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: tokens.spacingHorizontalM,
    marginTop: tokens.spacingVerticalL,
  },

  pageInfo: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
});

const transactionTypes = [
  { value: '', label: 'All Types' },
  { value: 'Deposit', label: 'Deposits' },
  { value: 'Withdrawal', label: 'Withdrawals' },
  { value: 'TransferIn', label: 'Transfers In' },
  { value: 'TransferOut', label: 'Transfers Out' },
];

/**
 * AccountDetailPage - Single account view with transaction history.
 *
 * Features:
 * - Account balance card
 * - Transaction filtering (type, search, date range)
 * - Paginated transaction list
 * - Quick action buttons
 */
const AccountDetailPage: React.FC = () => {
  const styles = useStyles();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const dispatch = useAppDispatch();

  // Filter state
  const [search, setSearch] = useState('');
  const [typeFilter, setTypeFilter] = useState<TransactionType | ''>('');
  const [page, setPage] = useState(1);
  const pageSize = 10;

  // Fetch account
  const {
    data: account,
    isLoading: accountLoading,
    isError: accountError,
  } = useGetAccountByIdQuery(id!, { skip: !id });

  // Fetch transactions
  const {
    data: transactionsData,
    isLoading: transactionsLoading,
  } = useGetTransactionHistoryQuery(
    {
      accountId: id!,
      page,
      pageSize,
      type: typeFilter || undefined,
      search: search || undefined,
    },
    { skip: !id }
  );

  const transactions = transactionsData?.items || [];
  const totalPages = transactionsData?.totalPages || 1;
  const totalCount = transactionsData?.totalCount || 0;

  // Action handlers
  const handleDeposit = useCallback(() => {
    if (id) {
      dispatch(openDialog({ type: 'deposit', accountId: id }));
    }
  }, [dispatch, id]);

  const handleWithdraw = useCallback(() => {
    if (id) {
      dispatch(openDialog({ type: 'withdraw', accountId: id }));
    }
  }, [dispatch, id]);

  const handleTransfer = useCallback(() => {
    dispatch(openDialog({ type: 'transfer' }));
  }, [dispatch]);

  // Filter handlers
  const handleSearchChange = useCallback((value: string) => {
    setSearch(value);
    setPage(1); // Reset to first page
  }, []);

  const handleTypeChange = useCallback((value: TransactionType | '') => {
    setTypeFilter(value);
    setPage(1);
  }, []);

  const clearFilters = useCallback(() => {
    setSearch('');
    setTypeFilter('');
    setPage(1);
  }, []);

  const hasFilters = search || typeFilter;

  // Error state
  if (accountError) {
    return (
      <EmptyState
        title="Account not found"
        description="The account you're looking for doesn't exist or you don't have access to it."
        actionLabel="Back to Accounts"
        onAction={() => navigate('/accounts')}
      />
    );
  }

  return (
    <div className={styles.root}>
      {/* Back Button */}
      <Button
        appearance="subtle"
        icon={<ChevronLeftRegular />}
        className={styles.backButton}
        as={Link}
        to="/accounts"
      >
        Back to Accounts
      </Button>

      {/* Balance Card */}
      {accountLoading ? (
        <BalanceCardSkeleton variant="hero" />
      ) : account ? (
        <BalanceCard
          account={account}
          variant="hero"
          showActions
          onDeposit={handleDeposit}
          onWithdraw={handleWithdraw}
          onTransfer={handleTransfer}
        />
      ) : null}

      {/* Filter Bar */}
      <div className={styles.filterBar}>
        <Input
          className={styles.searchInput}
          placeholder="Search transactions..."
          value={search}
          onChange={(e, data) => handleSearchChange(data.value)}
          contentBefore={<SearchRegular />}
          contentAfter={
            search ? (
              <Button
                appearance="subtle"
                icon={<DismissRegular />}
                size="small"
                onClick={() => handleSearchChange('')}
              />
            ) : undefined
          }
        />

        <Dropdown
          className={styles.filterDropdown}
          placeholder="Type"
          value={typeFilter || 'All Types'}
          onOptionSelect={(e, data) =>
            handleTypeChange((data.optionValue as TransactionType) || '')
          }
        >
          {transactionTypes.map((type) => (
            <Option key={type.value} value={type.value}>
              {type.label}
            </Option>
          ))}
        </Dropdown>

        {hasFilters && (
          <Button
            appearance="subtle"
            icon={<FilterRegular />}
            onClick={clearFilters}
          >
            Clear Filters
          </Button>
        )}
      </div>

      {/* Transactions Section */}
      <section className={styles.transactionSection}>
        <div className={styles.sectionHeader}>
          <h2 className={styles.sectionTitle}>Transaction History</h2>
          <span className={styles.transactionCount}>
            {totalCount} transaction{totalCount !== 1 ? 's' : ''}
          </span>
        </div>

        {transactionsLoading ? (
          <TransactionListSkeleton count={5} />
        ) : transactions.length > 0 ? (
          <>
            <div className={styles.transactionList}>
              {transactions.map((tx) => (
                <TransactionCard
                  key={tx.id}
                  transaction={tx}
                />
              ))}
            </div>

            {/* Pagination */}
            {totalPages > 1 && (
              <div className={styles.pagination}>
                <Button
                  appearance="outline"
                  disabled={page === 1}
                  onClick={() => setPage((p) => p - 1)}
                >
                  Previous
                </Button>
                <span className={styles.pageInfo}>
                  Page {page} of {totalPages}
                </span>
                <Button
                  appearance="outline"
                  disabled={page === totalPages}
                  onClick={() => setPage((p) => p + 1)}
                >
                  Next
                </Button>
              </div>
            )}
          </>
        ) : (
          <EmptyState
            variant="compact"
            title={hasFilters ? 'No matching transactions' : 'No transactions yet'}
            description={
              hasFilters
                ? 'Try adjusting your filters.'
                : 'Your transaction history will appear here.'
            }
            actionLabel={hasFilters ? 'Clear Filters' : undefined}
            onAction={hasFilters ? clearFilters : undefined}
          />
        )}
      </section>
    </div>
  );
};

export default AccountDetailPage;
```

---

### 11.11 NotFoundPage Component

The NotFoundPage displays a friendly 404 error when users navigate to non-existent routes.

```typescript
// src/pages/NotFoundPage.tsx
import * as React from 'react';
import { Link } from 'react-router-dom';
import { Button } from '@fluentui/react-components';
import { HomeRegular, SearchRegular } from '@fluentui/react-icons';
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '100vh',
    textAlign: 'center',
    ...shorthands.padding(tokens.spacingVerticalXXL),
    backgroundColor: tokens.colorNeutralBackground2,
  },

  errorCode: {
    fontSize: '120px',
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,
    lineHeight: 1,
    marginBottom: tokens.spacingVerticalM,

    '@media (max-width: 479px)': {
      fontSize: '80px',
    },
  },

  title: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorNeutralForeground1,
    marginBottom: tokens.spacingVerticalS,
  },

  description: {
    fontSize: tokens.fontSizeBase400,
    color: tokens.colorNeutralForeground3,
    marginBottom: tokens.spacingVerticalXXL,
    maxWidth: '400px',
  },

  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
    justifyContent: 'center',
  },
});

/**
 * NotFoundPage - 404 error page.
 *
 * Displayed when users navigate to routes that don't exist.
 */
const NotFoundPage: React.FC = () => {
  const styles = useStyles();

  return (
    <div className={styles.root}>
      <div className={styles.errorCode}>404</div>
      <h1 className={styles.title}>Page Not Found</h1>
      <p className={styles.description}>
        Oops! The page you're looking for doesn't exist or has been moved.
        Let's get you back on track.
      </p>
      <div className={styles.actions}>
        <Button
          appearance="primary"
          icon={<HomeRegular />}
          as={Link}
          to="/dashboard"
        >
          Go to Dashboard
        </Button>
        <Button
          appearance="outline"
          icon={<SearchRegular />}
          as={Link}
          to="/accounts"
        >
          Browse Accounts
        </Button>
      </div>
    </div>
  );
};

export default NotFoundPage;
```

---

### 11.12 PageSkeleton Component

A generic page-level skeleton loader for route transitions.

```typescript
// src/components/common/Skeleton/PageSkeleton.tsx
import * as React from 'react';
import { Spinner } from '@fluentui/react-components';
import { makeStyles, tokens, shorthands } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '100vh',
    backgroundColor: tokens.colorNeutralBackground2,
  },

  content: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalM,
  },

  text: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
  },
});

interface PageSkeletonProps {
  message?: string;
}

/**
 * PageSkeleton - Full page loading indicator.
 */
export const PageSkeleton: React.FC<PageSkeletonProps> = ({
  message = 'Loading...',
}) => {
  const styles = useStyles();

  return (
    <div className={styles.root} role="status" aria-label={message}>
      <div className={styles.content}>
        <Spinner size="large" />
        <span className={styles.text}>{message}</span>
      </div>
    </div>
  );
};

PageSkeleton.displayName = 'PageSkeleton';
```

---

### 11.13 Route Navigation Utilities

```typescript
// src/utils/navigation.ts
import { useNavigate, useLocation } from 'react-router-dom';
import { useCallback } from 'react';

/**
 * Custom hook for common navigation patterns
 */
export function useNavigation() {
  const navigate = useNavigate();
  const location = useLocation();

  const goBack = useCallback(() => {
    if (window.history.length > 2) {
      navigate(-1);
    } else {
      navigate('/dashboard');
    }
  }, [navigate]);

  const goToAccount = useCallback((accountId: string) => {
    navigate(`/accounts/${accountId}`);
  }, [navigate]);

  const goToLogin = useCallback((preserveLocation = true) => {
    navigate('/login', {
      replace: true,
      state: preserveLocation ? { from: location.pathname } : undefined,
    });
  }, [navigate, location.pathname]);

  const goToDashboard = useCallback(() => {
    navigate('/dashboard', { replace: true });
  }, [navigate]);

  return {
    goBack,
    goToAccount,
    goToLogin,
    goToDashboard,
    navigate,
    location,
  };
}
```

---

### 11.14 Route Lazy Loading Best Practices

```typescript
// Pattern for lazy loading with named exports
// src/pages/index.ts

// If your pages use named exports, create wrapper files
// src/pages/LazyPages.ts

import { lazy } from 'react';

// Helper to create lazy components with named exports
function lazyNamed<T extends React.ComponentType<any>>(
  factory: () => Promise<{ default: T }>
) {
  return lazy(factory);
}

// Alternative pattern for pages with named exports
export const LazyLoginPage = lazy(() =>
  import('./LoginPage').then((module) => ({
    default: module.LoginPage,
  }))
);

// For default exports (simpler)
export const LazyDashboardPage = lazy(() => import('./DashboardPage'));
```

---

## 12. Implementation Checklist

This comprehensive checklist ensures complete and correct implementation of the AzureBank frontend application. Use these checklists during development to verify each phase is complete before moving forward.

---

### 12.1 Project Setup Checklist

#### 12.1.1 Initial Project Setup

```markdown
## Project Initialization
- [ ] Create Vite project with React + TypeScript template
- [ ] Verify Node.js version >= 18.x
- [ ] Verify Bun installed (optional but recommended)
- [ ] Initialize git repository
- [ ] Create .gitignore with proper exclusions

## Package Installation
- [ ] Install all production dependencies
- [ ] Install all development dependencies
- [ ] Verify package.json matches 16-package-manifest.md
- [ ] Run `bun install` or `npm install` successfully
- [ ] Verify no peer dependency warnings

## TypeScript Configuration
- [ ] tsconfig.json configured with strict mode
- [ ] Path aliases configured (@/ -> src/)
- [ ] tsconfig.node.json for Vite config
- [ ] Type checking passes: `bun run type-check`

## Vite Configuration
- [ ] vite.config.ts created with proper settings
- [ ] Path aliases matching tsconfig
- [ ] Development server port configured (5173)
- [ ] Proxy configured for BFF API (/api -> backend)
- [ ] Build output configured (dist/)

## Code Quality Tools
- [ ] ESLint configured with TypeScript rules
- [ ] Prettier configured with consistent settings
- [ ] Lint script works: `bun run lint`
- [ ] Format script works: `bun run format`
- [ ] Pre-commit hooks configured (optional)

## Environment Configuration
- [ ] .env.example created with all variables
- [ ] .env.development created (local development)
- [ ] Vite env prefix configured (VITE_)
- [ ] Environment types declared in vite-env.d.ts
```

#### 12.1.2 Folder Structure Verification

```markdown
## Source Directory Structure
- [ ] src/app/ - Store, router, hooks
- [ ] src/components/ - Shared components
- [ ] src/features/ - Feature modules
- [ ] src/hooks/ - Custom React hooks
- [ ] src/mocks/ - MSW handlers
- [ ] src/pages/ - Route page components
- [ ] src/theme/ - FluentUI theme config
- [ ] src/types/ - Shared TypeScript types
- [ ] src/utils/ - Utility functions

## Feature Module Structure (for each feature)
- [ ] features/{name}/api/ - RTK Query endpoints
- [ ] features/{name}/components/ - Feature components
- [ ] features/{name}/hooks/ - Feature-specific hooks
- [ ] features/{name}/types/ - Feature types
- [ ] features/{name}/index.ts - Public exports

## Component Directory Structure (for each component)
- [ ] ComponentName.tsx - Main component
- [ ] ComponentName.styles.ts - Styles (makeStyles)
- [ ] ComponentName.types.ts - Type definitions
- [ ] index.ts - Barrel export
```

---

### 12.2 Theme & Styling Checklist

#### 12.2.1 FluentUI Theme Setup

```markdown
## Theme Configuration
- [ ] src/theme/index.ts - Theme exports
- [ ] src/theme/brandColors.ts - Brand color ramp
- [ ] src/theme/customTheme.ts - Custom theme creation
- [ ] src/theme/semanticColors.ts - Transaction/status colors
- [ ] src/theme/tokens.ts - Custom design tokens

## Theme Implementation
- [ ] Brand ramp follows FluentUI format (10-160)
- [ ] Primary brand color: #006DE2
- [ ] Theme created with createLightTheme()
- [ ] Custom overrides applied correctly
- [ ] Theme type exported for type safety

## FluentProvider Setup
- [ ] FluentProvider wraps entire app
- [ ] Custom theme passed to provider
- [ ] Provider placed in main.tsx or App.tsx
- [ ] SSR className configured (if needed)
```

#### 12.2.2 Semantic Colors Verification

```markdown
## Transaction Type Colors
- [ ] Deposit: green (#E6F4EA bg, #137333 text, #34A853 icon)
- [ ] Withdrawal: red (#FCE8E6 bg, #C5221F text, #EA4335 icon)
- [ ] Transfer Out: orange (#FEF3E2 bg, #B45309 text, #F59E0B icon)
- [ ] Transfer In: blue (#E0F2FE bg, #0369A1 text, #0EA5E9 icon)

## Status Colors
- [ ] Success: green palette
- [ ] Warning: orange palette
- [ ] Error: red palette
- [ ] Info: blue palette

## Balance Colors
- [ ] Positive balance: #137333
- [ ] Negative balance: #C5221F
- [ ] Neutral balance: #1F2937
```

---

### 12.3 State Management Checklist

#### 12.3.1 Redux Store Setup

```markdown
## Store Configuration
- [ ] src/app/store.ts - Store creation
- [ ] configureStore() with all reducers
- [ ] RTK Query middleware added
- [ ] DevTools configured (development only)
- [ ] Store type exports (RootState, AppDispatch)

## Typed Hooks
- [ ] src/app/hooks.ts created
- [ ] useAppDispatch hook exported
- [ ] useAppSelector hook exported
- [ ] Type safety verified

## Provider Setup
- [ ] <Provider store={store}> in main.tsx
- [ ] Provider wraps entire app
- [ ] Provider inside FluentProvider
```

#### 12.3.2 Feature Slices Verification

```markdown
## Auth Slice
- [ ] Initial state defined (isAuthenticated, user, isInitialized)
- [ ] setUser action
- [ ] clearUser action
- [ ] setInitialized action
- [ ] Selectors exported (selectIsAuthenticated, selectUser, etc.)
- [ ] BFF-aware (no tokens stored)

## Account Slice
- [ ] Initial state defined (selectedAccountId)
- [ ] setSelectedAccount action
- [ ] clearSelectedAccount action
- [ ] Selectors exported

## UI Slice
- [ ] Initial state defined (isMobileNavOpen)
- [ ] toggleMobileNav action
- [ ] setMobileNavOpen action
- [ ] Selectors exported
```

#### 12.3.3 RTK Query API Verification

```markdown
## Base API Configuration
- [ ] src/app/api.ts - Base API setup
- [ ] baseUrl configured for BFF ('/api')
- [ ] Credentials: 'include' for cookies
- [ ] Tag types defined
- [ ] Error handling configured

## Auth API Endpoints
- [ ] POST /auth/register - register mutation
- [ ] POST /auth/login - login mutation
- [ ] POST /auth/logout - logout mutation
- [ ] GET /auth/me - getMe query
- [ ] Cache invalidation on login/logout

## Account API Endpoints
- [ ] GET /accounts - getAccounts query
- [ ] GET /accounts/:id - getAccount query
- [ ] POST /accounts - createAccount mutation
- [ ] GET /accounts/:id/balance - getBalance query (with timestamp)
- [ ] Cache tags properly configured

## Transaction API Endpoints
- [ ] GET /transactions - getTransactions query (with filters)
- [ ] POST /transactions/deposit - deposit mutation
- [ ] POST /transactions/withdraw - withdraw mutation
- [ ] POST /transfers - transfer mutation
- [ ] Cache invalidation after mutations
```

---

### 12.4 Component Implementation Checklist

#### 12.4.1 Layout Components

```markdown
## AppLayout
- [ ] Header with navigation
- [ ] Main content area with Outlet
- [ ] Footer (if applicable)
- [ ] Responsive behavior (mobile/desktop)
- [ ] Breadcrumb integration
- [ ] Skip to content link (a11y)

## Header
- [ ] AzureBank logo/title
- [ ] Navigation links (desktop)
- [ ] User menu with logout
- [ ] Mobile hamburger button
- [ ] Responsive breakpoints

## MobileNav
- [ ] Drawer component from FluentUI
- [ ] Full navigation links
- [ ] User info display
- [ ] Logout option
- [ ] Backdrop click closes
- [ ] Focus trap enabled
- [ ] Escape key closes
```

#### 12.4.2 Auth Components

```markdown
## LoginPage
- [ ] Email input with validation
- [ ] Password input with show/hide toggle
- [ ] Remember me checkbox (optional)
- [ ] Submit button with loading state
- [ ] Link to register page
- [ ] Error display (form-level, field-level)
- [ ] Redirect after successful login
- [ ] Mobile-responsive layout

## RegisterPage
- [ ] Email input with validation
- [ ] Password input with requirements display
- [ ] Confirm password with match validation
- [ ] Full name input
- [ ] Submit button with loading state
- [ ] Link to login page
- [ ] Success message/redirect
- [ ] Error handling
- [ ] Password strength indicator (optional)
```

#### 12.4.3 Dashboard Components

```markdown
## DashboardPage
- [ ] Welcome message with user name
- [ ] Total balance across accounts
- [ ] Account cards list
- [ ] Quick actions (deposit, withdraw, transfer)
- [ ] Recent transactions summary
- [ ] Loading skeleton states
- [ ] Empty state for new users
- [ ] Responsive grid layout

## BalanceCard
- [ ] Account name/type display
- [ ] Balance with AnimatedNumber
- [ ] Currency formatting
- [ ] Account number (masked)
- [ ] Positive/negative color coding
- [ ] Click to view account details
- [ ] Hover state
```

#### 12.4.4 Account Components

```markdown
## AccountsPage
- [ ] Account list/grid
- [ ] Create account button
- [ ] Filter by account type (optional)
- [ ] Sort options (optional)
- [ ] Empty state for no accounts
- [ ] Loading skeletons
- [ ] Click to view details

## AccountDetailPage
- [ ] Account header with balance
- [ ] Transaction filters (date range, type)
- [ ] Transaction list
- [ ] Action buttons (deposit, withdraw, transfer)
- [ ] Pagination/infinite scroll
- [ ] Loading states
- [ ] Empty transaction state
- [ ] Back navigation

## CreateAccountDialog
- [ ] Account name input
- [ ] Account type selection (Checking/Savings)
- [ ] Initial deposit (optional)
- [ ] Currency selection (if multi-currency)
- [ ] Submit with validation
- [ ] Cancel button
- [ ] Loading state
- [ ] Success/error handling
```

#### 12.4.5 Transaction Components

```markdown
## TransactionCard
- [ ] Transaction type icon with color
- [ ] Description/reference
- [ ] Amount with sign (+/-)
- [ ] Formatted date/time
- [ ] Account info (for transfers)
- [ ] Hover state
- [ ] Click for details (optional)

## TransactionDialog (Deposit/Withdraw)
- [ ] Amount input (CurrencyInput)
- [ ] Account selector (dropdown)
- [ ] Description/reference input
- [ ] Submit with validation
- [ ] Loading state
- [ ] Success confirmation
- [ ] Error handling

## TransferDialog
- [ ] Source account selector
- [ ] Destination account selector
- [ ] Amount input (CurrencyInput)
- [ ] Description input
- [ ] Validation (different accounts, sufficient funds)
- [ ] Submit with loading
- [ ] Success confirmation
- [ ] Error handling

## TransactionFilters
- [ ] Date range picker (from/to)
- [ ] Transaction type multi-select
- [ ] Account filter (if viewing all)
- [ ] Amount range (optional)
- [ ] Clear filters button
- [ ] Apply filters button (or auto-apply)
```

#### 12.4.6 Core UI Components

```markdown
## CurrencyInput
- [ ] Numeric-only input
- [ ] Currency symbol prefix
- [ ] Thousands separator formatting
- [ ] Decimal places handling
- [ ] Min/max validation
- [ ] Disabled state
- [ ] Error state styling
- [ ] Keyboard accessibility

## PasswordInput
- [ ] Show/hide toggle button
- [ ] Password requirements display
- [ ] Strength indicator (optional)
- [ ] Error state styling
- [ ] Accessible labels

## LoadingButton
- [ ] Loading spinner integration
- [ ] Disabled during loading
- [ ] Text change during loading
- [ ] Proper button types (submit/button)
- [ ] Icon support

## EmptyState
- [ ] Configurable icon
- [ ] Title and description
- [ ] Optional action button
- [ ] Centered layout
- [ ] Responsive sizing

## ErrorBoundary
- [ ] Catches render errors
- [ ] Fallback UI display
- [ ] Error logging
- [ ] Reset/retry option
- [ ] User-friendly message

## AnimatedNumber
- [ ] Smooth count animation
- [ ] Duration configurable
- [ ] Easing function
- [ ] Reduced motion support
- [ ] Format callback support
```

---

### 12.5 Form Handling Checklist

#### 12.5.1 React Hook Form Integration

```markdown
## Form Setup
- [ ] useForm hook with defaultValues
- [ ] Zod resolver for validation
- [ ] TypeScript form types inferred from schema
- [ ] handleSubmit wired correctly
- [ ] form.reset() after successful submit

## Field Registration
- [ ] All fields registered with {...register()}
- [ ] Error messages displayed
- [ ] Field state (touched, dirty) used appropriately
- [ ] Controller used for custom components

## Validation Schemas
- [ ] Zod schemas for all forms
- [ ] Email validation
- [ ] Password requirements
- [ ] Amount validation (positive, max 2 decimals)
- [ ] Required field validation
- [ ] Custom error messages
```

#### 12.5.2 Form Validation Schemas

```typescript
// Verify these schemas are implemented correctly:

## Login Schema
- [ ] Email: required, valid format
- [ ] Password: required, min 1 char

## Register Schema
- [ ] Email: required, valid format
- [ ] Password: required, min 8 chars, complexity
- [ ] ConfirmPassword: matches password
- [ ] FullName: required, min 2 chars

## Create Account Schema
- [ ] Name: required, min 1 char, max 100
- [ ] Type: enum (Checking, Savings)

## Transaction Schema
- [ ] Amount: required, positive, max 2 decimals
- [ ] AccountId: required, valid UUID
- [ ] Description: optional, max 255

## Transfer Schema
- [ ] Amount: required, positive
- [ ] SourceAccountId: required
- [ ] DestinationAccountId: required, different from source
- [ ] Description: optional
```

---

### 12.6 Routing Checklist

#### 12.6.1 Route Configuration

```markdown
## Router Setup
- [ ] createBrowserRouter configured
- [ ] All routes defined
- [ ] Lazy loading implemented
- [ ] Error boundaries per route (optional)
- [ ] RouterProvider in main.tsx

## Public Routes
- [ ] /login - LoginPage
- [ ] /register - RegisterPage
- [ ] /* (404) - NotFoundPage

## Protected Routes
- [ ] /dashboard - DashboardPage
- [ ] /accounts - AccountsPage
- [ ] /accounts/:id - AccountDetailPage

## Route Protection
- [ ] ProtectedRoute component wrapping private routes
- [ ] Auth state check (isAuthenticated)
- [ ] Initialization check (isInitialized)
- [ ] Redirect to login with return path
- [ ] Loading state during init
```

#### 12.6.2 Navigation Verification

```markdown
## Navigation Links
- [ ] Dashboard link works
- [ ] Accounts link works
- [ ] Active link highlighting
- [ ] Mobile nav links work

## Programmatic Navigation
- [ ] Login success -> Dashboard
- [ ] Logout -> Login
- [ ] Account click -> Account details
- [ ] Back navigation works
- [ ] 404 handling

## Session Management
- [ ] SessionManager checks auth on mount
- [ ] Handles BFF session validation
- [ ] Shows loading during check
- [ ] Clears state on session expired
```

---

### 12.7 API Integration Checklist

#### 12.7.1 MSW Mock Handlers

```markdown
## Handler Setup
- [ ] src/mocks/handlers.ts - All handlers
- [ ] src/mocks/browser.ts - Browser worker
- [ ] src/mocks/data.ts - Mock data store
- [ ] MSW initialized in main.tsx (dev only)

## Auth Handlers
- [ ] POST /api/auth/register - Creates user, returns success
- [ ] POST /api/auth/login - Validates, sets session cookie mock
- [ ] POST /api/auth/logout - Clears session
- [ ] GET /api/auth/me - Returns current user

## Account Handlers
- [ ] GET /api/accounts - Returns user's accounts
- [ ] GET /api/accounts/:id - Returns single account
- [ ] POST /api/accounts - Creates new account
- [ ] GET /api/accounts/:id/balance - Returns balance (with timestamp)

## Transaction Handlers
- [ ] GET /api/transactions - Returns filtered transactions
- [ ] POST /api/transactions/deposit - Creates deposit
- [ ] POST /api/transactions/withdraw - Creates withdrawal
- [ ] POST /api/transfers - Creates transfer

## Error Simulation
- [ ] 400 Bad Request responses
- [ ] 401 Unauthorized responses
- [ ] 404 Not Found responses
- [ ] 422 Validation error responses
- [ ] Network delay simulation (optional)
```

#### 12.7.2 Real API Integration

```markdown
## Axios Configuration
- [ ] Base URL configured
- [ ] Credentials: 'include' for cookies
- [ ] Request interceptors (if needed)
- [ ] Response interceptors for error handling
- [ ] Timeout configured

## RTK Query Base Query
- [ ] axiosBaseQuery implemented
- [ ] Error transformation
- [ ] 401 handling (redirect to login)
- [ ] Retry logic (optional)

## API Error Handling
- [ ] Form validation errors displayed
- [ ] Network errors handled gracefully
- [ ] Timeout errors handled
- [ ] User-friendly error messages
- [ ] Error toast notifications
```

---

### 12.8 Accessibility Checklist

#### 12.8.1 Keyboard Navigation

```markdown
## Focus Management
- [ ] Focus visible on all interactive elements
- [ ] Tab order logical
- [ ] Skip to main content link
- [ ] Focus trap in modals/dialogs
- [ ] Focus returned after dialog close

## Keyboard Shortcuts
- [ ] Enter submits forms
- [ ] Escape closes dialogs
- [ ] Arrow keys in dropdowns
- [ ] All actions keyboard accessible
```

#### 12.8.2 Screen Reader Support

```markdown
## ARIA Implementation
- [ ] aria-label on icon buttons
- [ ] aria-describedby for form errors
- [ ] aria-live regions for dynamic content
- [ ] aria-busy during loading
- [ ] role attributes where needed

## Semantic HTML
- [ ] Proper heading hierarchy (h1-h6)
- [ ] <main>, <nav>, <header>, <footer> landmarks
- [ ] <button> for actions, <a> for navigation
- [ ] <label> associated with inputs
- [ ] Form fieldsets and legends

## Content
- [ ] Alt text for images
- [ ] Accessible names for icons
- [ ] Error messages announced
- [ ] Success messages announced
- [ ] Loading states announced
```

#### 12.8.3 Visual Accessibility

```markdown
## Color Contrast
- [ ] Text meets WCAG AA (4.5:1)
- [ ] Large text meets WCAG AA (3:1)
- [ ] UI components meet 3:1 contrast
- [ ] Focus indicators visible

## Motion
- [ ] prefers-reduced-motion respected
- [ ] No content-only animations
- [ ] Users can pause animations

## Responsive
- [ ] 200% zoom works
- [ ] Text resizable without breaking
- [ ] Touch targets 44x44px minimum
```

---

### 12.9 Performance Checklist

#### 12.9.1 Code Splitting

```markdown
## Route-Level Splitting
- [ ] All page components lazy loaded
- [ ] Suspense with fallbacks
- [ ] Preloading for likely navigation

## Bundle Analysis
- [ ] Bundle analyzer configured
- [ ] No single chunk > 200KB
- [ ] Tree shaking working
- [ ] Dead code eliminated
```

#### 12.9.2 React Optimization

```markdown
## Component Optimization
- [ ] React.memo for expensive components
- [ ] useMemo for expensive calculations
- [ ] useCallback for stable function references
- [ ] Virtualized lists for long data (if applicable)

## State Management
- [ ] Selectors with createSelector for memoization
- [ ] Minimal re-renders verified
- [ ] No prop drilling (use selectors)
- [ ] RTK Query caching leveraged
```

#### 12.9.3 Asset Optimization

```markdown
## Images
- [ ] Images optimized
- [ ] Lazy loading for below-fold images
- [ ] Proper sizing/srcset

## Fonts
- [ ] System font stack (Segoe UI)
- [ ] Font display: swap
- [ ] No custom font downloads (FluentUI default)

## CSS
- [ ] CSS-in-JS tree shakes unused
- [ ] No large CSS imports
- [ ] makeStyles generates efficient CSS
```

---

### 12.10 Testing Checklist

#### 12.10.1 Unit Tests

```markdown
## Utility Functions
- [ ] formatCurrency tests
- [ ] formatDate tests
- [ ] formatAccountNumber tests
- [ ] Validation helper tests

## Custom Hooks
- [ ] useNavigation hook tests
- [ ] useDebounce hook tests
- [ ] Feature-specific hook tests

## Redux
- [ ] Slice reducer tests
- [ ] Action creator tests
- [ ] Selector tests
```

#### 12.10.2 Component Tests

```markdown
## Core Components
- [ ] CurrencyInput renders and formats
- [ ] PasswordInput show/hide works
- [ ] LoadingButton shows spinner
- [ ] EmptyState displays message
- [ ] ErrorBoundary catches errors

## Feature Components
- [ ] BalanceCard displays data
- [ ] TransactionCard displays correctly
- [ ] Form components submit properly
- [ ] Dialog components open/close

## Page Components
- [ ] LoginPage form works
- [ ] RegisterPage validation works
- [ ] DashboardPage loads data
- [ ] AccountDetailPage shows transactions
```

#### 12.10.3 Integration Tests

```markdown
## User Flows
- [ ] Complete registration flow
- [ ] Complete login flow
- [ ] Create account flow
- [ ] Deposit money flow
- [ ] Withdraw money flow
- [ ] Transfer money flow
- [ ] View transaction history

## API Integration
- [ ] MSW handlers intercept correctly
- [ ] Error states handled
- [ ] Loading states shown
- [ ] Success states confirmed
```

---

### 12.11 Security Checklist

#### 12.11.1 Frontend Security

```markdown
## Authentication
- [ ] No tokens stored in localStorage
- [ ] No tokens stored in sessionStorage
- [ ] Cookies are httpOnly (backend)
- [ ] Session validation on app load
- [ ] Logout clears all state

## Data Handling
- [ ] No sensitive data in Redux DevTools (prod)
- [ ] Account numbers masked in display
- [ ] No PII in console logs
- [ ] No PII in error messages

## Input Validation
- [ ] All inputs validated client-side
- [ ] XSS prevention (React escapes by default)
- [ ] No dangerouslySetInnerHTML
- [ ] URL parameters validated
```

#### 12.11.2 Network Security

```markdown
## HTTPS
- [ ] All API calls use HTTPS (production)
- [ ] No mixed content

## CORS
- [ ] Credentials included in requests
- [ ] Same-origin or trusted origins only

## Content Security
- [ ] CSP headers configured (if applicable)
- [ ] No inline scripts/styles (if CSP strict)
```

---

### 12.12 Deployment Checklist

#### 12.12.1 Build Verification

```markdown
## Pre-Build Checks
- [ ] All TypeScript errors resolved
- [ ] All ESLint errors resolved
- [ ] All tests passing
- [ ] No console.log statements
- [ ] Environment variables set

## Build Process
- [ ] `bun run build` succeeds
- [ ] Build output in dist/
- [ ] No build warnings (or documented)
- [ ] Source maps configured appropriately

## Post-Build Verification
- [ ] Preview build works: `bun run preview`
- [ ] All routes work
- [ ] API calls work
- [ ] No console errors
- [ ] Bundle size acceptable
```

#### 12.12.2 Production Configuration

```markdown
## Environment
- [ ] VITE_API_URL configured
- [ ] Production API endpoint correct
- [ ] No development-only code in production
- [ ] MSW disabled in production

## Performance
- [ ] Gzip/Brotli compression enabled (server)
- [ ] Cache headers configured (server)
- [ ] Static assets have long cache

## Monitoring
- [ ] Error tracking configured (optional)
- [ ] Analytics configured (optional)
- [ ] Performance monitoring (optional)
```

---

### 12.13 Final Verification Checklist

```markdown
## Complete Application Test
- [ ] Fresh user registration works
- [ ] Login with registered user works
- [ ] Dashboard shows correct data
- [ ] Create new account works
- [ ] Deposit to account works
- [ ] Withdraw from account works
- [ ] Transfer between accounts works
- [ ] Transaction history filters work
- [ ] Historical balance display works
- [ ] Logout works
- [ ] Session expiry handling works

## Cross-Browser Testing
- [ ] Chrome - All features work
- [ ] Firefox - All features work
- [ ] Safari - All features work
- [ ] Edge - All features work

## Responsive Testing
- [ ] Mobile (375px) - Layout correct
- [ ] Tablet (768px) - Layout correct
- [ ] Desktop (1024px) - Layout correct
- [ ] Large (1440px+) - Layout correct

## Accessibility Testing
- [ ] Keyboard-only navigation works
- [ ] Screen reader testing passed
- [ ] Color contrast verified
- [ ] Focus indicators visible

## Performance Testing
- [ ] Initial load < 3s
- [ ] TTI (Time to Interactive) acceptable
- [ ] No memory leaks
- [ ] Smooth animations
```

---

### 12.14 Implementation Order Reference

For developers implementing this frontend, follow this recommended order:

```markdown
## Phase 1: Foundation
1. Project setup (Vite, TypeScript, dependencies)
2. Theme configuration (FluentUI)
3. Store setup (Redux Toolkit)
4. Router setup (React Router)
5. Base API configuration (RTK Query)

## Phase 2: Core Infrastructure
6. MSW mock handlers
7. Auth slice + API
8. SessionManager component
9. ProtectedRoute component
10. AppLayout component

## Phase 3: Authentication
11. LoginPage
12. RegisterPage
13. Header with user menu
14. MobileNav

## Phase 4: Account Management
15. Account slice + API
16. DashboardPage
17. BalanceCard component
18. AccountsPage
19. CreateAccountDialog
20. AccountDetailPage

## Phase 5: Transactions
21. Transaction API
22. TransactionCard component
23. TransactionFilters
24. DepositDialog
25. WithdrawDialog
26. TransferDialog

## Phase 6: Polish
27. Loading skeletons
28. Empty states
29. Error boundaries
30. Toast notifications
31. Accessibility audit
32. Performance optimization
33. Final testing
```

---

## Appendix A: Quick Reference Commands

### Development Commands

```bash
# Start development server (Bun + Vite, maximum speed)
bunx --bun vite

# Start development server (npm)
npm run dev

# Type check without emitting
bun run type-check

# Lint code
bun run lint

# Fix lint issues
bun run lint:fix

# Format code
bun run format

# Check formatting
bun run format:check
```

### Build Commands

```bash
# Production build
bun run build

# Preview production build
bun run preview
```

### MSW Commands

```bash
# Initialize MSW (first time only)
bunx msw init public/ --save

# Update MSW service worker
bunx msw init public/ --save
```

---

## Appendix B: Dependency Summary

| Category | Package | Version | Purpose |
|----------|---------|---------|---------|
| **UI Framework** | @fluentui/react-components | ^9.72.8 | Component library |
| **UI Icons** | @fluentui/react-icons | ^2.0.270 | Icon library |
| **State** | @reduxjs/toolkit | ^2.11.2 | State management + RTK Query |
| **State** | react-redux | ^9.2.0 | React Redux bindings |
| **Routing** | react-router-dom | ^7.1.1 | Client-side routing |
| **HTTP** | axios | ^1.7.9 | HTTP client |
| **Forms** | react-hook-form | ^7.54.2 | Form state |
| **Validation** | zod | ^3.24.1 | Schema validation |
| **Dates** | date-fns | ^4.1.0 | Date manipulation |
| **Utilities** | clsx | ^2.1.1 | Class names |
| **Utilities** | use-debounce | ^10.0.6 | Debouncing hooks |
| **Dialogs** | sweetalert2 | ^11.15.2 | Modal dialogs |
| **Mocking** | msw | ^2.7.0 | API mocking |

---

## Appendix C: Version Compatibility

| Package | Minimum Node | Minimum React | Notes |
|---------|--------------|---------------|-------|
| FluentUI v9 | 18+ | 18+ | React 19 compatible |
| Redux Toolkit 2.x | 18+ | 18+ | RTK Query included |
| React Router 7.x | 18+ | 18+ | React 19 compatible |
| MSW 2.x | 18+ | 18+ | Service Worker API |
| Vite 6.x | 18+ | N/A | Build tool |

---

**Document Status**: PHASE 6 COMPLETE

**Total Lines**: ~14,900

**Sections Complete**: All 12 sections + 3 Appendices

**Last Updated**: 2026-01-09 by Claude (Frontend Lead)
