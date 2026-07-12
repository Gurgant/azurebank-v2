# Post-MVP Roadmap & Future Enhancements

**Document Version**: 1.0
**Created**: 2026-01-08
**Status**: LIVING DOCUMENT

---

## Purpose

This document captures ideas, features, and architectural improvements that are **intentionally deferred** from MVP scope. These are NOT forgotten - they are documented here for future consideration.

> **REMINDER**: When you encounter a good idea during development that's out of MVP scope, add it here instead of implementing it!

---

## 1. Security & Authentication Enhancements

### 1.1 Redis Session Store
- **Current MVP**: In-memory session storage (single server)
- **Future**: Redis for distributed session storage
- **Benefit**: Horizontal scaling, session persistence across restarts
- **Source**: BFF Pattern analysis (2026-01-08)

### 1.2 OTP (One-Time Password) Authentication
- **Current MVP**: PIN verification only (Level 2 auth)
- **Future**: SMS/Email OTP for high-value transactions (Level 3 auth)
- **Benefit**: Additional security layer for transfers > €500
- **Source**: Step-up authentication research

### 1.3 Biometric Authentication
- **Current MVP**: Username/Password + PIN
- **Future**: WebAuthn/FIDO2 support, fingerprint, face recognition
- **Benefit**: Passwordless authentication option

### 1.4 Re-Authentication Flow
- **Current MVP**: Session expires, user logs in again
- **Future**: In-session re-auth for security changes (Level 4)
- **Benefit**: Password/email changes require fresh credentials

### 1.5 Device Trust & Fingerprinting
- **Current MVP**: No device tracking
- **Future**: Remember trusted devices, alert on new device login
- **Benefit**: Fraud detection, suspicious login alerts

---

## 2. Architecture Improvements

### 2.1 Microservices Migration
- **Current MVP**: Monolith API + BFF
- **Future**: Split into Identity, Accounts, Transfers, Notifications services
- **Benefit**: Independent scaling, team autonomy
- **Source**: Tokens-Analyze.md comprehensive architecture
- **Note**: Only if scale justifies complexity

### 2.2 Event-Driven Architecture
- **Current MVP**: Synchronous API calls
- **Future**: RabbitMQ/Azure Service Bus for async operations
- **Benefit**: Decoupled services, better fault tolerance
- **Use Cases**: Notification sending, audit logging, reporting

### 2.3 CQRS Pattern
- **Current MVP**: Single database, read/write same model
- **Future**: Separate read/write models, read replicas
- **Benefit**: Optimized queries, better scalability

### 2.4 GraphQL Gateway
- **Current MVP**: REST API via YARP
- **Future**: GraphQL for flexible frontend queries
- **Benefit**: Reduced over-fetching, better mobile experience

---

## 3. Feature Enhancements

### 3.1 Card Management
- **Current MVP**: Not implemented
- **Future**: Virtual cards, card freeze/unfreeze, spending limits
- **Benefit**: Complete banking experience

### 3.2 Scheduled Transfers
- **Current MVP**: Immediate transfers only
- **Future**: One-time scheduled, recurring transfers
- **Benefit**: Bill pay automation

### 3.3 Standing Orders
- **Current MVP**: Not implemented
- **Future**: Automatic recurring payments
- **Benefit**: Rent, subscriptions automation

### 3.4 Direct Debits
- **Current MVP**: Not implemented
- **Future**: Authorize merchants to pull funds
- **Benefit**: Utility payments, subscriptions

### 3.5 Push Notifications
- **Current MVP**: In-app only
- **Future**: Web Push, Mobile Push (if mobile app)
- **Benefit**: Real-time transaction alerts

### 3.6 Multi-Currency Accounts
- **Current MVP**: EUR only
- **Future**: USD, GBP, CHF accounts
- **Benefit**: International users, FX features

### 3.7 Interest Calculation
- **Current MVP**: No interest
- **Future**: Savings account interest accrual
- **Benefit**: Real savings product

### 3.8 Account Statements
- **Current MVP**: Transaction history only
- **Future**: PDF statements, monthly summaries
- **Benefit**: Official documents for users

---

## 4. Performance & Scalability

### 4.1 Cursor-Based Pagination
- **Current MVP**: Page-based pagination
- **Future**: Cursor pagination for large datasets
- **Benefit**: Better performance on transaction history

### 4.2 Balance Snapshots
- **Current MVP**: Calculate from transactions
- **Future**: Periodic balance snapshots (daily/weekly)
- **Benefit**: Faster historical balance queries

### 4.3 CDN Integration
- **Current MVP**: Single origin
- **Future**: Static assets on CDN
- **Benefit**: Faster load times globally

### 4.4 Database Read Replicas
- **Current MVP**: Single database
- **Future**: Read replicas for reporting
- **Benefit**: Offload read queries from primary

---

## 5. Developer Experience

### 5.1 API Versioning
- **Current MVP**: Single version (v1)
- **Future**: Multiple API versions (v1, v2)
- **Benefit**: Non-breaking changes, gradual migration

### 5.2 Automated E2E Tests
- **Current MVP**: Unit tests, integration tests
- **Future**: Playwright/Cypress E2E tests
- **Benefit**: Full user flow testing

### 5.3 Performance Testing
- **Current MVP**: Manual testing
- **Future**: k6/Artillery load tests
- **Benefit**: Identify bottlenecks before production

### 5.4 Feature Flags
- **Current MVP**: No feature flags
- **Future**: LaunchDarkly or similar
- **Benefit**: Gradual rollouts, A/B testing

---

## 6. Compliance & Reporting

### 6.1 Audit Dashboard
- **Current MVP**: Audit logs in database
- **Future**: Admin dashboard for audit trail
- **Benefit**: Compliance reporting, investigations

### 6.2 Suspicious Activity Detection
- **Current MVP**: Basic rate limiting
- **Future**: ML-based fraud detection
- **Benefit**: Proactive fraud prevention

### 6.3 GDPR Data Export
- **Current MVP**: Not implemented
- **Future**: User data export functionality
- **Benefit**: GDPR Article 20 compliance

### 6.4 Account Closure Process
- **Current MVP**: Soft delete
- **Future**: Full account closure workflow
- **Benefit**: Regulatory compliance, data retention policy

---

## 7. Infrastructure

### 7.1 Kubernetes Deployment
- **Current MVP**: Single server / Azure App Service
- **Future**: Kubernetes orchestration
- **Benefit**: Auto-scaling, rolling deployments
- **Source**: Tokens-Analyze.md

### 7.2 Blue-Green Deployments
- **Current MVP**: Standard deployment
- **Future**: Zero-downtime deployments
- **Benefit**: No service interruption

### 7.3 Disaster Recovery
- **Current MVP**: Database backups
- **Future**: Multi-region deployment, automated failover
- **Benefit**: High availability

---

## How to Use This Document

1. **When you have a good idea** during development:
   - Add it to the appropriate section
   - Include: Current state, Future state, Benefit, Source (if applicable)

2. **When planning next iteration**:
   - Review this document
   - Prioritize based on user feedback and business value
   - Move items to active development backlog

3. **When implementing**:
   - Remove item from this document
   - Add to proper technical documentation

---

## Change Log

| Date | Change | Author |
|------|--------|--------|
| 2026-01-08 | Initial creation with BFF analysis items | Claude |

---

**Remember**: This is a LIVING DOCUMENT. Update it whenever you defer a feature or have a good idea!
