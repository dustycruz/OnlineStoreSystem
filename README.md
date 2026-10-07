<!-- File: README.md -->
# OnlineStoreSystem

A multi-service e-commerce platform on **.NET 10 / C# 14** that demonstrates, end to end and **without Docker**:

| Module | What you will see working |
|---|---|
| 2 – Contract-first API design | Hand-written OpenAPI 3.0 contracts in [`/contracts`](contracts) (+ an AsyncAPI document for the events), URI versioning `/api/v1/...`, **RFC 7807** problem details for every 4xx/5xx |
| 3 – Synchronous integration | Typed `HttpClient`s via `IHttpClientFactory` with the **standard resilience pipeline** (retry, exponential backoff, jitter, attempt timeout, total timeout, circuit breaker) and `X-Correlation-Id` propagation |
| 4 – Event-driven messaging | RabbitMQ topic exchange, **publisher confirms**, `BackgroundService` consumers with **manual ACK / NACK**, **dead-letter queues**, **idempotent consumer** (`ProcessedMessages` table), **transactional outbox** |
| 5 – Saga & data consistency | **Orchestrated saga** in OrderService with a persisted state machine, deterministic **compensation** (ReleaseStock) and a recovery worker |
| Data | **Database-first**: four SQL Server databases (one per service) created from raw DDL in SSMS, then reverse-engineered with `dotnet ef dbcontext scaffold` |

| Application | Port | Database | Swagger UI |
|---|---|---|---|
| `OnlineStoreSystem.Storefront` (ASP.NET Core MVC) | **5000** | – | – |
| `OnlineStoreSystem.CatalogService` | **5001** | `CatalogDb` | http://localhost:5001/swagger |
| `OnlineStoreSystem.OrderService` (saga orchestrator) | **5002** | `OrderDb` | http://localhost:5002/swagger |
| `OnlineStoreSystem.InventoryService` | **5003** | `InventoryDb` | http://localhost:5003/swagger |
| `OnlineStoreSystem.PaymentService` | **5004** | `PaymentDb` | http://localhost:5004/swagger |

> Every source file starts with a `File:` comment holding its exact relative path, so any snippet can be traced back to the repository.

---

## Table of contents

1. [Architecture](#architecture)
2. [Repository layout](#repository-layout)
3. [Prerequisites (no Docker)](#prerequisites-no-docker)
4. [STEP 1 – SQL Server databases (SSMS)](#step-1--sql-server-databases-ssms)
5. [STEP 2 – Solution, projects, packages and EF Core scaffolding](#step-2--solution-projects-packages-and-ef-core-scaffolding)
6. [STEP 3 – OpenAPI contracts](#step-3--openapi-contracts-module-2)
7. [STEP 4 – Shared library](#step-4--shared-library)
8. [STEP 5 – Catalog Service](#step-5--catalog-service-port-5001)
9. [STEP 6 – Inventory Service](#step-6--inventory-service-port-5003)
10. [STEP 7 – Payment Service](#step-7--payment-service-port-5004)
11. [STEP 8 – Order Service and the saga](#step-8--order-service-port-5002-and-the-saga)
12. [STEP 9 – Storefront](#step-9--storefront-port-5000)
13. [STEP 10 – Run and verify](#step-10--run-and-verify)
14. [Configuration reference](#configuration-reference)
15. [Design decisions](#design-decisions)
16. [Troubleshooting](#troubleshooting)
17. [How this repository was verified](#how-this-repository-was-verified)

---

## Architecture

```mermaid
flowchart LR
    browser([Browser]) --> SF[Storefront :5000<br/>MVC / Razor]
    SF -- "HTTP + X-Correlation-Id" --> CAT[CatalogService :5001]
    SF -- HTTP --> ORD[OrderService :5002<br/>saga orchestrator]
    SF -- HTTP --> INV[InventoryService :5003]
    ORD -- "sync, resilient HTTP<br/>(validate products)" --> CAT
    ORD -- "sync, resilient HTTP<br/>(charge, Idempotency-Key)" --> PAY[PaymentService :5004]
    ORD <-- "RabbitMQ<br/>onlinestore.events (topic)" --> MQ{{RabbitMQ}}
    INV <-- RabbitMQ --> MQ
    CAT --- CDB[(CatalogDb)]
    ORD --- ODB[(OrderDb<br/>Orders · SagaStates · Outbox)]
    INV --- IDB[(InventoryDb<br/>Stock · Reservations · ProcessedMessages)]
    PAY --- PDB[(PaymentDb)]
```

### The orchestrated checkout saga

| # | Step | Mechanism | Failure handling |
|---|---|---|---|
| 1 | **ValidateProducts** | sync HTTP → CatalogService (`CatalogClient`, resilience pipeline) | `400` (unknown/inactive product) or `503` (Catalog unreachable after retries); nothing persisted |
| 2 | **CreateOrder** | one local transaction: `Orders` (Pending) + `SagaStates` + `OutboxMessages(OrderPlacedEvent)` | – |
| 3 | **ReserveStock** | async: `OrderPlacedEvent` → InventoryService → `StockReservedEvent` / `StockReservationFailedEvent` | failure → order `Cancelled` (nothing to compensate); timeout → `OrderCancelledEvent` |
| 4 | **ProcessPayment** | sync HTTP → PaymentService (`PaymentClient`, `Idempotency-Key`) | declined → **compensation**: `PaymentFailedEvent` → InventoryService **ReleaseStock** → `StockReleasedEvent` → saga `Compensated`, order `Cancelled` |
| 5 | **CompleteOrder** | order `Completed` + `PaymentProcessedEvent` → InventoryService commits the stock | – |

```mermaid
sequenceDiagram
    autonumber
    participant C as Client / Storefront
    participant O as OrderService (orchestrator)
    participant Cat as CatalogService
    participant MQ as RabbitMQ
    participant I as InventoryService
    participant P as PaymentService
    C->>O: POST /api/v1/orders/checkout (X-Correlation-Id)
    O->>Cat: GET /api/v1/products?ids=… (retry + backoff + jitter)
    Cat-->>O: 200 products (authoritative prices)
    O->>O: TX: Order(Pending) + SagaState + Outbox(OrderPlaced)
    O-->>C: 202 Accepted + Location
    O->>MQ: OrderPlacedEvent (outbox relay, publisher confirm)
    MQ->>I: deliver (manual ACK)
    I->>I: TX: ProcessedMessages check + reserve stock
    I->>MQ: StockReservedEvent (publisher confirm), then BasicAck
    MQ->>O: StockReservedEvent
    O->>P: POST /api/v1/payments/charge (Idempotency-Key)
    alt approved (201)
        O->>O: TX: Order Completed + Outbox(PaymentProcessed)
        O->>MQ: PaymentProcessedEvent
        MQ->>I: commit reservation (on-hand stock decremented)
    else declined (402) – compensation
        O->>O: TX: Order Cancelled, saga Compensating + Outbox(PaymentFailed)
        O->>MQ: PaymentFailedEvent
        MQ->>I: ReleaseStock (reservation Released)
        I->>MQ: StockReleasedEvent
        MQ->>O: saga Compensated
    end
```

**Deterministic compensation triggers** – card `4000000000000002` (`card_declined`), card `4000000000009995`
(`insufficient_funds`), or an order total of exactly **9999.00** (product 5 *Cinema Camera Pro 8K*, quantity 1 →
`amount_limit_exceeded`). Card `4242424242424242` is always approved.

---

## Repository layout

```
OnlineStoreSystem/
├── OnlineStoreSystem.sln            # classic .sln (created with --format sln)
├── global.json                      # .NET 10 SDK (10.0.100, rollForward latestFeature)
├── dotnet-tools.json                # local tool manifest: dotnet-ef 10.0.12
├── contracts/                       # Module 2 - written BEFORE the controllers
│   ├── catalog-v1.yaml  orders-v1.yaml  inventory-v1.yaml  payment-v1.yaml   (OpenAPI 3.0.3)
│   └── events-asyncapi.yaml                                                   (AsyncAPI 2.6, RabbitMQ)
├── sql/                             # STEP 1 - run in SSMS
│   ├── 01_CatalogDb.sql  02_InventoryDb.sql  03_PaymentDb.sql  04_OrderDb.sql
│   └── 99_ResetDemoData.sql
├── src/
│   ├── OnlineStoreSystem.Shared/            # correlation id, events, RabbitMQ, DTOs, RFC 7807, Serilog
│   ├── OnlineStoreSystem.CatalogService/    # :5001
│   ├── OnlineStoreSystem.OrderService/      # :5002 saga orchestrator + outbox
│   ├── OnlineStoreSystem.InventoryService/  # :5003 idempotent consumers + DLQ
│   └── OnlineStoreSystem.PaymentService/    # :5004
├── web/
│   └── OnlineStoreSystem.Storefront/        # :5000 MVC storefront
├── postman/
│   └── ecommerce_integration.json           # 67 requests / 200+ assertions (Postman or newman)
├── scripts/                                 # create-databases, scaffold-dbcontexts, run-all, stop-all, demo (.sh + .ps1)
└── logs/                                    # created at runtime: one Serilog file per app (git-ignored)
```

---

## Prerequisites (no Docker)

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | **10.0.100+** | `dotnet --list-sdks` |
| SQL Server | LocalDB / Express / Developer 2019+ | Windows: LocalDB ships with Visual Studio and SQL Server Express. Linux: native SQL Server packages. macOS: use a SQL Server reachable over the network (e.g. Azure SQL Database free offer) – see [Troubleshooting](#troubleshooting) |
| SSMS | 20+ | or `sqlcmd` / Azure Data Studio |
| RabbitMQ | 3.12+ | native install **or** a CloudAMQP instance – see [Run and verify](#101-install-and-start-rabbitmq-no-docker) |
| curl | any | Windows 10+ ships `curl.exe`; PowerShell scripts are provided too |
| Node.js (optional) | 18+ | only to run the Postman collection with `npx newman` |

---

## STEP 1 – SQL Server databases (SSMS)

Each service owns exactly one database (**database-per-service**). No database ever references another one;
`ProductId` / `OrderId` values cross service boundaries only inside messages and HTTP payloads.

| Script | Database | Tables | Seed |
|---|---|---|---|
| [`sql/01_CatalogDb.sql`](sql/01_CatalogDb.sql) | `CatalogDb` | `Products` | 5 products (ids 1–5; product 5 costs exactly 9999.00) |
| [`sql/02_InventoryDb.sql`](sql/02_InventoryDb.sql) | `InventoryDb` | `InventoryItems` (computed `QuantityAvailable`), `StockReservations` (FK → InventoryItems), `ProcessedMessages` (idempotency ledger, PK `MessageId + Consumer`) | stock for products 1–5 |
| [`sql/03_PaymentDb.sql`](sql/03_PaymentDb.sql) | `PaymentDb` | `PaymentTransactions` (filtered unique `IdempotencyKey`), `PaymentRefunds` (FK) | – |
| [`sql/04_OrderDb.sql`](sql/04_OrderDb.sql) | `OrderDb` | `Orders`, `OrderItems` (FK, computed `LineTotal`), `SagaStates` (1:1 FK), `SagaStepLogs` (FK), `OutboxMessages` (filtered index on pending rows) | – |

All scripts include primary keys, foreign keys (with explicit `ON DELETE` behaviour), unique/filtered/composite
indexes, CHECK constraints (status vocabularies, non-negative stock, `Reserved <= OnHand`...), named defaults,
`READ_COMMITTED_SNAPSHOT ON`, and are **re-runnable** (they drop and recreate their database).

### 1.1 Run the scripts in SSMS

1. Start SSMS → **Connect → Database Engine**.
   * Windows LocalDB: Server name `(localdb)\mssqllocaldb`, Authentication *Windows Authentication*.
     (If the instance does not exist: `sqllocaldb create MSSQLLocalDB` then `sqllocaldb start MSSQLLocalDB`.)
   * SQL Express: `.\SQLEXPRESS`; Developer edition / Linux: `localhost` (SQL Server Authentication, e.g. `sa`).
   * SSMS 20+: tick **Trust server certificate** on the connection dialog.
2. **File → Open → File…** → `sql/01_CatalogDb.sql` → **Execute (F5)**.
3. Repeat for `02_InventoryDb.sql`, `03_PaymentDb.sql`, `04_OrderDb.sql`.
4. Refresh **Databases** in Object Explorer: `CatalogDb`, `InventoryDb`, `OrderDb`, `PaymentDb` appear. Each script
   ends with a `SELECT` showing what it created/seeded.

### 1.2 …or from the command line

```bash
# bash (Linux / macOS / Git Bash)
SQL_SERVER=localhost SQL_USER=sa SQL_PASSWORD='Your_strong_pwd1' ./scripts/create-databases.sh
```

```powershell
# PowerShell (Windows, LocalDB by default)
.\scripts\create-databases.ps1
.\scripts\create-databases.ps1 -Server '.\SQLEXPRESS'
.\scripts\create-databases.ps1 -Server 'localhost,1433' -User sa -Password 'Your_strong_pwd1'
```

Equivalent raw command per script: `sqlcmd -S "(localdb)\mssqllocaldb" -E -C -b -i sql/01_CatalogDb.sql`.

`sql/99_ResetDemoData.sql` restores seed stock/prices and deletes orders, payments and the idempotency ledger
without dropping anything – handy between demo runs.

---

## STEP 2 – Solution, projects, packages and EF Core scaffolding

The repository already contains the finished solution. This section shows how it is built from an empty folder
(every command below was executed against the .NET 10.0.1xx SDK).

### 2.1 Create the solution from scratch (bash)

```bash
mkdir OnlineStoreSystem && cd OnlineStoreSystem
dotnet new gitignore
dotnet new globaljson --sdk-version 10.0.100 --roll-forward latestFeature
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.12

# .NET 10 creates .slnx by default - request the classic OnlineStoreSystem.sln explicitly
dotnet new sln --format sln -n OnlineStoreSystem

dotnet new classlib -n OnlineStoreSystem.Shared -o src/OnlineStoreSystem.Shared -f net10.0
for s in Catalog Order Inventory Payment; do
  dotnet new webapi -n OnlineStoreSystem.${s}Service -o src/OnlineStoreSystem.${s}Service \
    -f net10.0 --use-controllers --no-https --no-openapi
done
dotnet new mvc -n OnlineStoreSystem.Storefront -o web/OnlineStoreSystem.Storefront -f net10.0 --no-https

dotnet sln OnlineStoreSystem.sln add --solution-folder src \
  src/OnlineStoreSystem.Shared/OnlineStoreSystem.Shared.csproj \
  src/OnlineStoreSystem.CatalogService/OnlineStoreSystem.CatalogService.csproj \
  src/OnlineStoreSystem.OrderService/OnlineStoreSystem.OrderService.csproj \
  src/OnlineStoreSystem.InventoryService/OnlineStoreSystem.InventoryService.csproj \
  src/OnlineStoreSystem.PaymentService/OnlineStoreSystem.PaymentService.csproj
dotnet sln OnlineStoreSystem.sln add --solution-folder web web/OnlineStoreSystem.Storefront/OnlineStoreSystem.Storefront.csproj

# project references
for s in Catalog Order Inventory Payment; do
  dotnet add src/OnlineStoreSystem.${s}Service reference src/OnlineStoreSystem.Shared
done
dotnet add web/OnlineStoreSystem.Storefront reference src/OnlineStoreSystem.Shared

# NuGet packages (all net10.0 compatible)
dotnet add src/OnlineStoreSystem.Shared package RabbitMQ.Client --version 7.2.2
dotnet add src/OnlineStoreSystem.Shared package Serilog.AspNetCore --version 10.0.0
for s in Catalog Order Inventory Payment; do
  p=src/OnlineStoreSystem.${s}Service
  dotnet add $p package Microsoft.EntityFrameworkCore.SqlServer --version 10.0.12
  dotnet add $p package Microsoft.EntityFrameworkCore.Design --version 10.0.12
  dotnet add $p package Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore --version 10.0.12
  dotnet add $p package Swashbuckle.AspNetCore --version 10.2.3
done
for s in Order Inventory; do dotnet add src/OnlineStoreSystem.${s}Service package RabbitMQ.Client --version 7.2.2; done
dotnet add src/OnlineStoreSystem.OrderService package Microsoft.Extensions.Http.Resilience --version 10.10.0
dotnet add web/OnlineStoreSystem.Storefront package Microsoft.Extensions.Http.Resilience --version 10.10.0
```

The same in PowerShell:

```powershell
mkdir OnlineStoreSystem; cd OnlineStoreSystem
dotnet new gitignore
dotnet new globaljson --sdk-version 10.0.100 --roll-forward latestFeature
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.12
dotnet new sln --format sln -n OnlineStoreSystem
dotnet new classlib -n OnlineStoreSystem.Shared -o src/OnlineStoreSystem.Shared -f net10.0
foreach ($s in 'Catalog','Order','Inventory','Payment') {
    dotnet new webapi -n "OnlineStoreSystem.${s}Service" -o "src/OnlineStoreSystem.${s}Service" -f net10.0 --use-controllers --no-https --no-openapi
    dotnet sln OnlineStoreSystem.sln add --solution-folder src "src/OnlineStoreSystem.${s}Service/OnlineStoreSystem.${s}Service.csproj"
    dotnet add "src/OnlineStoreSystem.${s}Service" reference src/OnlineStoreSystem.Shared
    foreach ($pkg in @('Microsoft.EntityFrameworkCore.SqlServer 10.0.12', 'Microsoft.EntityFrameworkCore.Design 10.0.12',
                       'Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore 10.0.12', 'Swashbuckle.AspNetCore 10.2.3')) {
        $name, $version = $pkg.Split(' ')
        dotnet add "src/OnlineStoreSystem.${s}Service" package $name --version $version
    }
}
dotnet new mvc -n OnlineStoreSystem.Storefront -o web/OnlineStoreSystem.Storefront -f net10.0 --no-https
dotnet sln OnlineStoreSystem.sln add --solution-folder src src/OnlineStoreSystem.Shared/OnlineStoreSystem.Shared.csproj
dotnet sln OnlineStoreSystem.sln add --solution-folder web web/OnlineStoreSystem.Storefront/OnlineStoreSystem.Storefront.csproj
dotnet add web/OnlineStoreSystem.Storefront reference src/OnlineStoreSystem.Shared
dotnet add src/OnlineStoreSystem.Shared package RabbitMQ.Client --version 7.2.2
dotnet add src/OnlineStoreSystem.Shared package Serilog.AspNetCore --version 10.0.0
dotnet add src/OnlineStoreSystem.OrderService package RabbitMQ.Client --version 7.2.2
dotnet add src/OnlineStoreSystem.InventoryService package RabbitMQ.Client --version 7.2.2
dotnet add src/OnlineStoreSystem.OrderService package Microsoft.Extensions.Http.Resilience --version 10.10.0
dotnet add web/OnlineStoreSystem.Storefront package Microsoft.Extensions.Http.Resilience --version 10.10.0
```

Then add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to the Shared project (it contains middleware,
`IExceptionHandler`, health checks) – see [`OnlineStoreSystem.Shared.csproj`](src/OnlineStoreSystem.Shared/OnlineStoreSystem.Shared.csproj).
Every `.csproj` in this repository targets `net10.0`, sets `LangVersion 14.0`, `Nullable` and `TreatWarningsAsErrors`.

### 2.2 Reverse-engineer the databases (database-first)

Run **after STEP 1**, from the repository root. The connection strings are literal so the command works before any
`Program.cs` exists. `--no-onconfiguring` keeps the connection string out of the generated code (it is read from
`appsettings.json` instead).

```bash
dotnet tool restore

dotnet ef dbcontext scaffold "Server=(localdb)\mssqllocaldb;Database=CatalogDb;Trusted_Connection=True;TrustServerCertificate=True" \
  Microsoft.EntityFrameworkCore.SqlServer --project src/OnlineStoreSystem.CatalogService \
  --context CatalogDbContext --context-dir Data --output-dir Data/Entities \
  --namespace OnlineStoreSystem.CatalogService.Models --context-namespace OnlineStoreSystem.CatalogService.Data \
  --no-onconfiguring --force

dotnet ef dbcontext scaffold "Server=(localdb)\mssqllocaldb;Database=InventoryDb;Trusted_Connection=True;TrustServerCertificate=True" \
  Microsoft.EntityFrameworkCore.SqlServer --project src/OnlineStoreSystem.InventoryService \
  --context InventoryDbContext --context-dir Data --output-dir Data/Entities \
  --namespace OnlineStoreSystem.InventoryService.Models --context-namespace OnlineStoreSystem.InventoryService.Data \
  --no-onconfiguring --force

dotnet ef dbcontext scaffold "Server=(localdb)\mssqllocaldb;Database=PaymentDb;Trusted_Connection=True;TrustServerCertificate=True" \
  Microsoft.EntityFrameworkCore.SqlServer --project src/OnlineStoreSystem.PaymentService \
  --context PaymentDbContext --context-dir Data --output-dir Data/Entities \
  --namespace OnlineStoreSystem.PaymentService.Models --context-namespace OnlineStoreSystem.PaymentService.Data \
  --no-onconfiguring --force

dotnet ef dbcontext scaffold "Server=(localdb)\mssqllocaldb;Database=OrderDb;Trusted_Connection=True;TrustServerCertificate=True" \
  Microsoft.EntityFrameworkCore.SqlServer --project src/OnlineStoreSystem.OrderService \
  --context OrderDbContext --context-dir Data --output-dir Data/Entities \
  --namespace OnlineStoreSystem.OrderService.Models --context-namespace OnlineStoreSystem.OrderService.Data \
  --no-onconfiguring --force
```

Or use the wrappers (they accept any server through a `{db}` template):

```bash
./scripts/scaffold-dbcontexts.sh
SQL_CONNECTION_TEMPLATE='Server=localhost,1433;Database={db};User Id=sa;Password=Your_strong_pwd1;TrustServerCertificate=True' ./scripts/scaffold-dbcontexts.sh
```

```powershell
.\scripts\scaffold-dbcontexts.ps1
.\scripts\scaffold-dbcontexts.ps1 -ConnectionTemplate 'Server=.\SQLEXPRESS;Database={db};Trusted_Connection=True;TrustServerCertificate=True'
```

Generated files (committed to the repository):

| Service | Context | Entities |
|---|---|---|
| Catalog | `Data/CatalogDbContext.cs` | `Data/Entities/Product.cs` |
| Inventory | `Data/InventoryDbContext.cs` | `InventoryItem.cs`, `StockReservation.cs`, `ProcessedMessage.cs` |
| Payment | `Data/PaymentDbContext.cs` | `PaymentTransaction.cs`, `PaymentRefund.cs` |
| Order | `Data/OrderDbContext.cs` | `Order.cs`, `OrderItem.cs`, `SagaState.cs`, `SagaStepLog.cs`, `OutboxMessage.cs` |

Hand-written additions never go into scaffolded files: they live in separate `partial` classes (e.g.
[`InventoryDbContext.Customizations.cs`](src/OnlineStoreSystem.InventoryService/Data/InventoryDbContext.Customizations.cs))
so `--force` can regenerate the model at any time. Entities never leave a service: controllers map them to the DTOs
in `OnlineStoreSystem.Shared/Contracts`.

---

## STEP 3 – OpenAPI contracts (Module 2)

| Contract | Service | Highlights |
|---|---|---|
| [`contracts/catalog-v1.yaml`](contracts/catalog-v1.yaml) | Catalog | CRUD, paging, batch lookup `ids=`, problem details |
| [`contracts/orders-v1.yaml`](contracts/orders-v1.yaml) | Order | checkout (202 + `Location`), draft CRUD, saga resource, 503 dependency problem |
| [`contracts/inventory-v1.yaml`](contracts/inventory-v1.yaml) | Inventory | stock CRUD, manual reserve/release/commit, insufficient-stock problem |
| [`contracts/payment-v1.yaml`](contracts/payment-v1.yaml) | Payment | charge with `Idempotency-Key`, 402 payment-declined problem, refunds |
| [`contracts/events-asyncapi.yaml`](contracts/events-asyncapi.yaml) | RabbitMQ | exchanges, routing keys, queues/DLQs, message schemas |

Workflow: the YAML is written first, then controllers implement it. Each service ships its contract
(`<Content Include="..\..\contracts\x-v1.yaml" ...>` in the `.csproj`), serves it at `/contracts/<file>` and Swagger
UI shows **both** the contract and the document generated from code (drop-down in the top bar) so drift is visible.

Conventions used by every contract: `/api/v1/...` URI versioning, optional `X-Correlation-Id` request header echoed
in the response, `application/problem+json` (RFC 7807 / RFC 9457) for every error with the extension members
`traceId`, `correlationId`, `service`, `timestamp`.

| Problem `type` | Status | When |
|---|---|---|
| `https://tools.ietf.org/html/rfc9110#section-15.5.1` | 400 | model validation / business validation (`errors` map) |
| `https://tools.ietf.org/html/rfc9110#section-15.5.5` | 404 | resource or route not found |
| `https://tools.ietf.org/html/rfc9110#section-15.5.10` | 409 | duplicate SKU / stock record |
| `urn:onlinestoresystem:problems:invalid-state-transition` | 409 | e.g. modifying a non-draft order, refunding a refunded payment |
| `urn:onlinestoresystem:problems:insufficient-stock` | 409 | manual reservation larger than availability (`shortages`) |
| `urn:onlinestoresystem:problems:concurrency-conflict` | 409 | optimistic concurrency (`rowversion`) |
| `urn:onlinestoresystem:problems:payment-declined` | 402 | `declineCode`, `paymentId` extensions |
| `urn:onlinestoresystem:problems:idempotency-key-reused` | 422 | same key, different payload |
| `urn:onlinestoresystem:problems:dependency-unavailable` | 503 | downstream unreachable after the resilience pipeline gave up (`Retry-After`) |

---

## STEP 4 – Shared library

`src/OnlineStoreSystem.Shared` – referenced by all five applications.

| Area | Files | Purpose |
|---|---|---|
| Correlation id | `Correlation/CorrelationIdMiddleware.cs`, `CorrelationIdDelegatingHandler.cs`, `CorrelationIdAccessor.cs`, `CorrelationIdExtensions.cs` | Edge generation / validation of `X-Correlation-Id`, `AsyncLocal` accessor (works for HTTP **and** RabbitMQ deliveries), `ILogger.BeginScope` so every log line carries `CorrelationId`, outgoing propagation on every `IHttpClientFactory` client |
| Events | `Events/OrderPlacedEvent.cs`, `StockReservedEvent.cs`, `StockReservationFailedEvent.cs`, `StockReleasedEvent.cs`, `PaymentProcessedEvent.cs`, `PaymentFailedEvent.cs`, `OrderCancelledEvent.cs`, `IntegrationEvent.cs` | Message contracts (`EventId` = AMQP `message-id` = idempotency key) |
| RabbitMQ | `Messaging/RabbitMqOptions.cs`, `RabbitMqConnectionFactoryHelper.cs`, `RabbitMqConnectionProvider.cs`, `RabbitMqConfiguration.cs`, `RabbitMqTopology.cs`, `RabbitMqPublisher.cs`, `RabbitMqConsumerBackgroundService.cs`, `RabbitMqHealthCheck.cs` | Options (local broker or CloudAMQP URI), auto-recovering connection, idempotent topology declaration incl. DLX/DLQ, **publisher with confirms + mandatory**, consumer base class with **manual ACK / NACK / dead-lettering** |
| DTOs | `Contracts/Catalog`, `Contracts/Orders`, `Contracts/Inventory`, `Contracts/Payments`, `Contracts/Common/PagedResult.cs` | Request/response models with DataAnnotations (Luhn check, ISO 4217, SKU pattern...) and shared status vocabularies |
| Problem details | `Problems/ProblemDetailsSetupExtensions.cs`, `GlobalExceptionHandler.cs`, `ProblemTypes.cs`, `ControllerProblemExtensions.cs` | RFC 7807 everywhere + exception mapping (`DependencyUnavailableException` → 503) |
| Logging | `Logging/SerilogSetupExtensions.cs` | Serilog console + `logs/<app>-<date>.log`, request logging with correlation id |
| Misc | `Diagnostics/FaultInjectionMiddleware.cs`, `Hosting/*`, `Identifiers/*`, `Json/*`, `Validation/*` | deterministic transient faults (retry demo), contract + health endpoints, SQL-Server-friendly sequential GUIDs, UTC JSON dates |

C# 14 *extension members* (`extension(IServiceCollection services) { ... }`) are used for all registration helpers.

---

## STEP 5 – Catalog Service (port 5001)

* Scaffolded `CatalogDbContext` / `Product` ([`Data/`](src/OnlineStoreSystem.CatalogService/Data)).
* [`ProductsController`](src/OnlineStoreSystem.CatalogService/Controllers/ProductsController.cs):

| Method | Route | Responses |
|---|---|---|
| GET | `/api/v1/products?search=&category=&ids=&includeInactive=&page=&pageSize=` | 200, 400 |
| GET | `/api/v1/products/{productId}` | 200, 404 |
| POST | `/api/v1/products` | 201 + `Location`, 400, 409 |
| PUT | `/api/v1/products/{productId}` | 200, 400, 404, 409 |
| DELETE | `/api/v1/products/{productId}` | 204, 404 |

* [`Program.cs`](src/OnlineStoreSystem.CatalogService/Program.cs): SQL Server (`EnableRetryOnFailure`), Swagger UI,
  problem details, correlation-id middleware, Serilog, health checks, optional fault injection.

---

## STEP 6 – Inventory Service (port 5003)

* Scaffolded `InventoryDbContext`, `InventoryItem`, `StockReservation`, `ProcessedMessage`.
* [`InventoryController`](src/OnlineStoreSystem.InventoryService/Controllers/InventoryController.cs) – stock CRUD
  (`/api/v1/inventory[/{productId}]`) plus manual `POST /api/v1/inventory/reservations`,
  `GET /api/v1/inventory/reservations/{orderId}`, `POST .../{orderId}/release`, `POST .../{orderId}/commit`.
* [`OrderPlacedConsumer`](src/OnlineStoreSystem.InventoryService/Messaging/OrderPlacedConsumer.cs) (queue
  `inventory.order-placed`) and [`OrderOutcomeConsumer`](src/OnlineStoreSystem.InventoryService/Messaging/OrderOutcomeConsumer.cs)
  (queue `inventory.order-outcomes`: commit on `payment.processed`, **ReleaseStock** on `payment.failed` /
  `order.cancelled`), both built on
  [`RabbitMqConsumerBackgroundService`](src/OnlineStoreSystem.Shared/Messaging/RabbitMqConsumerBackgroundService.cs).

Delivery handling of every consumer:

| Situation | Action | Result |
|---|---|---|
| handler succeeded | `BasicAck` | message removed |
| `PoisonMessageException` (bad JSON, unknown type, invalid payload) | `BasicNack(requeue: false)` | routed by `x-dead-letter-exchange=onlinestore.dlx` to **`<queue>.dlq`** |
| other exception, first delivery | wait 1 s, `BasicNack(requeue: true)` | one retry |
| other exception, redelivered | `BasicNack(requeue: false)` | dead-lettered |

Idempotency ([`ProcessedMessagesIdempotencyGuard`](src/OnlineStoreSystem.InventoryService/Messaging/ProcessedMessagesIdempotencyGuard.cs)):
the stock change and the `ProcessedMessages(MessageId, Consumer)` row commit in **one** transaction; a redelivered
message is detected and its reply is **re-sent with the same deterministic `EventId`**, so a reply lost before the
ACK is never lost for good, and stock is never reserved twice.

---

## STEP 7 – Payment Service (port 5004)

* Scaffolded `PaymentDbContext`, `PaymentTransaction`, `PaymentRefund`.
* [`PaymentsController`](src/OnlineStoreSystem.PaymentService/Controllers/PaymentsController.cs):
  `POST /api/v1/payments/charge` (201 / 400 / **402** / 422), `GET /api/v1/payments[?orderId=]`,
  `GET /api/v1/payments/{paymentId}`, `POST /api/v1/payments/{paymentId}/refunds` (200 / 400 / 404 / 409).
* [`SimulatedPaymentGateway`](src/OnlineStoreSystem.PaymentService/Services/SimulatedPaymentGateway.cs) – the
  deterministic decline rules (amount 9999.00, cards `…0002` / `…9995`).
* [`PaymentProcessor`](src/OnlineStoreSystem.PaymentService/Services/PaymentProcessor.cs) – `Idempotency-Key`
  replay (`Idempotent-Replayed: true`), payload hash check, partial / full refunds, `rowversion` concurrency.
  Only card brand + last four digits are stored.

---

## STEP 8 – Order Service (port 5002) and the saga

| File | Responsibility |
|---|---|
| [`Data/OrderDbContext.cs`](src/OnlineStoreSystem.OrderService/Data/OrderDbContext.cs) + `Data/Entities/*` | scaffolded `Order`, `OrderItem`, `SagaState`, `SagaStepLog`, `OutboxMessage` |
| [`Clients/CatalogClient.cs`](src/OnlineStoreSystem.OrderService/Clients/CatalogClient.cs) | typed client, batch product validation |
| [`Clients/PaymentClient.cs`](src/OnlineStoreSystem.OrderService/Clients/PaymentClient.cs) | typed client, `Idempotency-Key: order-{orderId}-charge`, parses 402 problem details |
| [`Clients/ResiliencePolicies.cs`](src/OnlineStoreSystem.OrderService/Clients/ResiliencePolicies.cs) | retry (exponential + jitter), attempt / total timeout, circuit breaker, retry logging |
| [`Shared/Messaging/RabbitMqPublisher.cs`](src/OnlineStoreSystem.Shared/Messaging/RabbitMqPublisher.cs) | publisher with confirms (shared with InventoryService) |
| [`Outbox/OutboxDispatcher.cs`](src/OnlineStoreSystem.OrderService/Outbox/OutboxDispatcher.cs) | transactional outbox relay (marks rows processed only after the broker confirm) |
| [`Saga/OrderSagaOrchestrator.cs`](src/OnlineStoreSystem.OrderService/Saga/OrderSagaOrchestrator.cs) | the state machine: start, replies, payment, compensation, recovery |
| [`Saga/SagaRecoveryService.cs`](src/OnlineStoreSystem.OrderService/Saga/SagaRecoveryService.cs) | resumes stalled sagas (payment retry, reservation timeout, unconfirmed compensation) with the original correlation id |
| [`Saga/PaymentDetailsProtector.cs`](src/OnlineStoreSystem.OrderService/Saga/PaymentDetailsProtector.cs) | card data encrypted at rest with ASP.NET Core Data Protection until the payment step, then wiped |
| [`Messaging/InventoryReplyConsumer.cs`](src/OnlineStoreSystem.OrderService/Messaging/InventoryReplyConsumer.cs) | feeds `StockReserved/StockReservationFailed/StockReleased` replies to the orchestrator |
| [`Controllers/OrdersController.cs`](src/OnlineStoreSystem.OrderService/Controllers/OrdersController.cs) | `GET/POST /api/v1/orders`, `GET/PUT/DELETE /api/v1/orders/{id}`, `GET /api/v1/orders/{id}/saga`, `POST /api/v1/orders/checkout`, `POST /api/v1/orders/{id}/checkout` |

Resilience configuration (`appsettings.json → Resilience:Catalog`): 1 + 4 attempts, base delay 500 ms doubling with
jitter, 5 s per attempt, 30 s overall, circuit breaker (50 % failures over ≥10 calls in 30 s → open 15 s).
Payment uses 1 + 3 attempts and 20 s overall; POST retries are safe because of the idempotency key.

Saga states:

| `Orders.Status` | `SagaStates.Status` / `CurrentStep` | Meaning |
|---|---|---|
| `Pending` | `Running` / `ReserveStock` | waiting for InventoryService |
| `StockReserved` | `Running` / `ProcessPayment` | charging the card |
| `Completed` | `Completed` / `Done` | happy path finished |
| `Cancelled` | `Failed` / `Done` | stock could not be reserved (no compensation needed) |
| `Cancelled` | `Compensating` / `ReleaseStock` | payment failed, release requested |
| `Cancelled` | `Compensated` / `Done` | stock released – compensation complete |

---

## STEP 9 – Storefront (port 5000)

* Typed clients [`CatalogApiClient`](web/OnlineStoreSystem.Storefront/Clients/CatalogApiClient.cs),
  [`OrdersApiClient`](web/OnlineStoreSystem.Storefront/Clients/OrdersApiClient.cs) (POST never retried – checkout is not
  idempotent) and [`InventoryApiClient`](web/OnlineStoreSystem.Storefront/Clients/InventoryApiClient.cs) (availability badges).
* Controllers `HomeController`, `CatalogController`, `OrdersController`; session cart; anonymous customer cookie.
* Views: `Catalog/Index.cshtml` (browse + stock), `Orders/Checkout.cshtml` (form + test cards),
  `Orders/Status.cshtml` (auto-refreshing status, saga timeline, failure details, the checkout correlation id).
* The Storefront is the **edge**: `CorrelationIdMiddleware` creates the id for each browser request and it travels
  through OrderService, CatalogService, RabbitMQ, InventoryService and PaymentService.
* Bootstrap 5.3.3 is vendored under `wwwroot/lib/bootstrap` (works offline).

---

## STEP 10 – Run and verify

### 10.1 Install and start RabbitMQ (no Docker)

| OS | Commands |
|---|---|
| Windows | `winget install Erlang.ErlangOTP` then `winget install RabbitMQ.RabbitMQ` (or `choco install rabbitmq`). The installer registers a Windows service. Enable the UI: `"C:\Program Files\RabbitMQ Server\rabbitmq_server-<ver>\sbin\rabbitmq-plugins.bat" enable rabbitmq_management` |
| macOS | `brew install rabbitmq && brew services start rabbitmq` |
| Ubuntu/Debian | `sudo apt install rabbitmq-server && sudo systemctl enable --now rabbitmq-server && sudo rabbitmq-plugins enable rabbitmq_management` |
| Cloud | Create a free CloudAMQP instance and set `RabbitMq__ConnectionString=amqps://user:pass@host/vhost` (or `-RabbitMqConnection` / `RABBITMQ_CONNECTION` for the run scripts) |

Management UI: http://localhost:15672 (guest / guest). Queues and exchanges are declared by the services on startup.

### 10.2 Start the five applications

Scripted (builds once, starts everything, waits for health):

```bash
./scripts/run-all.sh                    # stop with ./scripts/stop-all.sh
# SQL authentication instead of LocalDB:
SQL_CONNECTION_TEMPLATE='Server=localhost,1433;Database={db};User Id=sa;Password=Your_strong_pwd1;TrustServerCertificate=True' ./scripts/run-all.sh
```

```powershell
.\scripts\run-all.ps1                   # one console window per app on Windows; stop with .\scripts\stop-all.ps1
.\scripts\run-all.ps1 -SqlConnectionTemplate 'Server=.\SQLEXPRESS;Database={db};Trusted_Connection=True;TrustServerCertificate=True'
```

Manual (five terminals, from the repository root):

```bash
dotnet run --project src/OnlineStoreSystem.CatalogService      # :5001
dotnet run --project src/OnlineStoreSystem.InventoryService    # :5003
dotnet run --project src/OnlineStoreSystem.PaymentService      # :5004
dotnet run --project src/OnlineStoreSystem.OrderService        # :5002
dotnet run --project web/OnlineStoreSystem.Storefront          # :5000 (opens the browser)
```

Health (database + RabbitMQ):

```bash
for p in 5001 5002 5003 5004 5000; do curl -s http://localhost:$p/health; echo; done
# {"service":"OrderService","status":"Healthy","checks":[{"name":"order-db","status":"Healthy"...},{"name":"rabbitmq","status":"Healthy","description":"Connected to amqp://localhost:5672/"...}]}
```

Open http://localhost:5000, add products to the cart, check out with `4242 4242 4242 4242`, and watch the status page
move from `Pending` / `StockReserved` to `Completed`. Use `4000 0000 0000 0002` to watch the compensation.

### 10.3 Scenario 1 – Happy path (order Completed, stock decremented, payment recorded)

```bash
curl -s http://localhost:5003/api/v1/inventory/1          # quantityOnHand 100

curl -i -X POST http://localhost:5002/api/v1/orders/checkout \
  -H "Content-Type: application/json" -H "X-Correlation-Id: demo-happy-001" \
  -d '{"customerId":"cust-001","customerEmail":"alice@example.com",
       "items":[{"productId":1,"quantity":2},{"productId":2,"quantity":1}],
       "payment":{"cardNumber":"4242424242424242","cardHolderName":"Alice Doe","expiryMonth":12,"expiryYear":2030}}'
# HTTP/1.1 202 Accepted
# Location: http://localhost:5002/api/v1/orders/<orderId>
# X-Correlation-Id: demo-happy-001
# {"orderId":"<orderId>","orderNumber":"ORD-20261006-HYFPBXD9","status":"Pending",...}

curl -s http://localhost:5002/api/v1/orders/<orderId>     # poll until "status":"Completed"
curl -s http://localhost:5003/api/v1/inventory/1          # quantityOnHand 98
curl -s "http://localhost:5004/api/v1/payments?orderId=<orderId>"   # one payment, "status":"Captured"
curl -s http://localhost:5003/api/v1/inventory/reservations/<orderId> # reservations "Committed"
```

Saga timeline returned in `GET /api/v1/orders/{id}` (`saga.steps`):

```
ValidateProducts  Succeeded    CatalogService validated 2 product(s): 2 x KB-MECH-001 @ 129.99, 1 x MS-WL-002 @ 49.99
CreateOrder       Succeeded    Order ORD-20261006-HYFPBXD9 stored as Pending; total 309.97 USD
ReserveStock      Started      OrderPlacedEvent written to the transactional outbox; awaiting InventoryService
ReserveStock      Succeeded    InventoryService reserved 2 x product 1, 1 x product 2
ProcessPayment    Started      Charging 309.97 USD via PaymentService (card ****4242)
ProcessPayment    Succeeded    Payment d69497d1-... captured: 309.97 USD (Visa ****4242)
CompleteOrder     Succeeded    Order completed; PaymentProcessedEvent queued so InventoryService commits the reserved stock
```

### 10.4 Scenario 2 – Compensation (payment fails → ReleaseStock → order Cancelled)

```bash
curl -s http://localhost:5003/api/v1/inventory/3          # quantityAvailable 40

curl -s -X POST http://localhost:5002/api/v1/orders/checkout \
  -H "Content-Type: application/json" -H "X-Correlation-Id: demo-comp-001" \
  -d '{"customerId":"cust-002","customerEmail":"bob@example.com","items":[{"productId":3,"quantity":2}],
       "payment":{"cardNumber":"4000000000000002","cardHolderName":"Bob Smith","expiryMonth":11,"expiryYear":2029}}'

curl -s http://localhost:5002/api/v1/orders/<orderId>
# "status":"Cancelled","failureReason":"Payment failed: The card was declined by the issuer (test card 4000 0000 0000 0002).",
# "saga":{"status":"Compensated", ... steps:
#   ReserveStock   Succeeded     InventoryService reserved 2 x product 3
#   ProcessPayment Failed        The card was declined by the issuer (test card 4000 0000 0000 0002). [card_declined]
#   ReleaseStock   Compensating  COMPENSATION started: PaymentFailedEvent queued - InventoryService will release the reserved stock
#   ReleaseStock   Compensated   InventoryService released 2 x product 3 (triggered by PaymentFailedEvent)

curl -s http://localhost:5003/api/v1/inventory/3          # quantityAvailable 40 again
curl -s http://localhost:5003/api/v1/inventory/reservations/<orderId>   # "status":"Released"
curl -s "http://localhost:5004/api/v1/payments?orderId=<orderId>"       # "status":"Declined","declineCode":"card_declined"
```

The amount trigger: one *Cinema Camera Pro 8K* (`{"productId":5,"quantity":1}`) with the approved card → total
9999.00 → `amount_limit_exceeded` → same compensation. Stock shortage (`{"productId":3,"quantity":41}`) cancels the
order at step 3 with saga status `Failed` and `ProcessPayment Skipped` (nothing to compensate).

### 10.5 Scenario 3 – Resilient retry (Catalog failing / down)

**a) Transient failures** – start CatalogService with deterministic fault injection (the first 2 requests per
correlation id answer 503):

```bash
./scripts/stop-all.sh catalog && CATALOG_FAULTS=1 ./scripts/run-all.sh
# or: dotnet run --project src/OnlineStoreSystem.CatalogService -- --FaultInjection:Enabled=true

curl -s -o /dev/null -w "HTTP %{http_code} in %{time_total}s\n" -X POST http://localhost:5002/api/v1/orders/checkout \
  -H "Content-Type: application/json" -H "X-Correlation-Id: demo-retry-001" \
  -d '{"customerId":"cust-004","customerEmail":"erin@example.com","items":[{"productId":4,"quantity":1}],
       "payment":{"cardNumber":"4242424242424242","cardHolderName":"Erin Lee","expiryMonth":1,"expiryYear":2031}}'
# HTTP 202 in 1.9s

grep -h "demo-retry-001" logs/order-service-*.log logs/catalog-service-*.log | grep -E "RESILIENCE|FAULT|responded" | sort
```

```
[CatalogService] [cid:demo-retry-001] FAULT INJECTION: failing attempt 1/2 for correlation id demo-retry-001 with 503
[OrderService]   [cid:demo-retry-001] RESILIENCE: call to CatalogService failed (HTTP 503 Service Unavailable); retry 1/4 in 476 ms (exponential backoff + jitter)
[CatalogService] [cid:demo-retry-001] FAULT INJECTION: failing attempt 2/2 for correlation id demo-retry-001 with 503
[OrderService]   [cid:demo-retry-001] RESILIENCE: call to CatalogService failed (HTTP 503 Service Unavailable); retry 2/4 in 874 ms (exponential backoff + jitter)
[CatalogService] [cid:demo-retry-001] FAULT INJECTION: attempt 3 for correlation id demo-retry-001 passes through
[CatalogService] [cid:demo-retry-001] HTTP GET /api/v1/products responded 200 in 408.5 ms
[OrderService]   [cid:demo-retry-001] HTTP POST /api/v1/orders/checkout responded 202 in 1950.9 ms
```

**b) Catalog down** – stop it (`./scripts/stop-all.sh catalog` or Ctrl+C) and check out again:

```
HTTP/1.1 503 Service Unavailable
Content-Type: application/problem+json
Retry-After: 5
X-Correlation-Id: demo-down-001

{"type":"urn:onlinestoresystem:problems:dependency-unavailable","title":"CatalogService is unavailable","status":503,
 "detail":"CatalogService could not be reached (HttpRequestException: Connection refused (localhost:5001)). The request was retried with exponential backoff before giving up; please try again later.",
 "instance":"/api/v1/orders/checkout","dependency":"CatalogService","correlationId":"demo-down-001","service":"OrderService",...}
```

with four `RESILIENCE: ... retry n/4 in ... ms` lines in `logs/order-service-*.log`. After repeated failures the
circuit breaker opens and calls fail fast (`BrokenCircuitException`) until it half-opens again.

PaymentService outages are covered too: the charge is retried with the same `Idempotency-Key`; if PaymentService is
still down the saga stays at `ProcessPayment` and **SagaRecoveryService** retries it (`Saga:PaymentRetryIntervalSeconds`)
up to `Saga:MaxPaymentAttempts` times before compensating. A RabbitMQ outage leaves new orders `Pending` (events wait
in `OutboxMessages`) and everything resumes automatically when the broker is back.

### 10.6 Scenario 4 – One correlation id across all services

```bash
grep -h "demo-happy-001" logs/*.log | sort
```

```
[OrderService]     [cid:demo-happy-001] SAGA START: checkout for customer cust-001 with 2 line(s)
[OrderService]     [cid:demo-happy-001] CorrelationIdDelegatingHandler: Propagating X-Correlation-Id: demo-happy-001 to GET http://localhost:5001/api/v1/products?ids=1&ids=2...
[CatalogService]   [cid:demo-happy-001] Returned 2 of 2 products (page 1, ids filter: 1,2)
[OrderService]     [cid:demo-happy-001] Published OrderPlacedEvent 5b9b1215-... to onlinestore.events/order.placed (broker confirmed)
[InventoryService] [cid:demo-happy-001] Received OrderPlacedEvent 5b9b1215-... from inventory.order-placed (routing key order.placed, redelivered: False)
[InventoryService] [cid:demo-happy-001] Reserved 2 x product 1 for order 2f263d92-...; on hand 100, reserved 2, available 98
[InventoryService] [cid:demo-happy-001] IDEMPOTENCY: recorded 5b9b1215-... for OrderPlacedConsumer in ProcessedMessages
[InventoryService] [cid:demo-happy-001] Published StockReservedEvent d8ba379a-... (broker confirmed)
[InventoryService] [cid:demo-happy-001] BasicAck OrderPlacedEvent (delivery tag 1)
[OrderService]     [cid:demo-happy-001] Received StockReservedEvent d8ba379a-... from order.inventory-replies
[PaymentService]   [cid:demo-happy-001] Payment d69497d1-... CAPTURED: 309.97 USD for order 2f263d92-... (Visa ****4242)
[OrderService]     [cid:demo-happy-001] SAGA e60d3f30-... COMPLETED: order ORD-20261006-HYFPBXD9 paid with payment d69497d1-...
[InventoryService] [cid:demo-happy-001] Committed 2 x product 1 for order 2f263d92-...; on hand now 98
```

Orders placed through the Storefront carry the id generated at the edge; it is shown on the order status page with
the matching `grep` command, and appears in all five log files.

### 10.7 Everything in one go

```bash
./scripts/demo.sh all          # happy, compensation, outage, trace   (also: happy | compensation | retry | outage | trace <id>)
```

```powershell
.\scripts\demo.ps1 all
```

### 10.8 Postman / newman

Import [`postman/ecommerce_integration.json`](postman/ecommerce_integration.json) into Postman and run it with the
**Collection Runner** (the saga poll requests repeat themselves until the saga is final), or headless:

```bash
npx newman run postman/ecommerce_integration.json
# requests 70, assertions 204, failed 0
```

Folders: `00 Health`, `01 Catalog Service`, `02 Inventory Service`, `03 Payment Service`, `04 Saga - Happy path`,
`05 Saga - Compensation`, `06 Orders - CRUD & validation`, `07 Resilience & tracing (manual)`. A collection-level
script stamps every request with a fresh `X-Correlation-Id` and asserts that every error is
`application/problem+json` carrying that id.

### 10.9 Dead-letter queue and idempotency by hand

Publish a poison message from the RabbitMQ UI (*Exchanges → onlinestore.events → Publish message*, routing key
`order.placed`, property `type = OrderPlacedEvent`, payload `{not json`) – or with the management API:

```bash
curl -u guest:guest -H 'content-type: application/json' -X POST \
  http://localhost:15672/api/exchanges/%2F/onlinestore.events/publish \
  -d '{"properties":{"message_id":"6f1c...","type":"OrderPlacedEvent","delivery_mode":2},"routing_key":"order.placed","payload":"{not json","payload_encoding":"string"}'
```

InventoryService logs `Poison message ... -> BasicNack(requeue: false) => dead-letter queue inventory.order-placed.dlq`
and the message appears in `inventory.order-placed.dlq` with an `x-death` header (`reason: rejected`).
Publishing a **valid** `OrderPlacedEvent` twice with the same `message_id` reserves stock once; the second delivery
logs `IDEMPOTENCY: OrderPlacedEvent ... was already processed` and re-sends the identical reply.

---

## Configuration reference

All settings live in each project's `appsettings.json` and can be overridden with environment variables
(`Section__Key`) or command-line arguments (`--Section:Key=value`).

| Setting | Default | Used by |
|---|---|---|
| `ConnectionStrings:CatalogDb / OrderDb / InventoryDb / PaymentDb` | LocalDB `(localdb)\mssqllocaldb` | the four services |
| `RabbitMq:ConnectionString` | empty (→ HostName/Port/UserName/Password/VirtualHost) | Order, Inventory |
| `RabbitMq:ExchangeName` / `DeadLetterExchangeName` / `DeadLetterQueueSuffix` | `onlinestore.events` / `onlinestore.dlx` / `.dlq` | Order, Inventory |
| `RabbitMq:PrefetchCount` / `PublishConfirmTimeoutSeconds` / `RequeueDelayMilliseconds` | 10 / 10 / 1000 | Order, Inventory |
| `Services:CatalogBaseUrl` / `PaymentBaseUrl` | `http://localhost:5001` / `:5004` | Order |
| `Resilience:Catalog:*`, `Resilience:Payment:*` | see STEP 8 | Order |
| `Saga:*` (timeouts, retry budgets, recovery interval) | 120 s / 30 s / 3 / 60 s / 5 / 10 s | Order |
| `Outbox:PollIntervalMilliseconds` / `BatchSize` | 2000 / 50 | Order |
| `Apis:CatalogBaseUrl` / `OrderBaseUrl` / `InventoryBaseUrl` | `:5001` / `:5002` / `:5003` | Storefront |
| `FaultInjection:Enabled` / `FailuresPerCorrelationId` / `StatusCode` | false / 2 / 503 | Catalog, Payment |
| `PaymentGateway:SimulatedLatencyMilliseconds` | 150 | Payment |
| `Logging:Directory` | `<repo>/logs` (found by walking up to `OnlineStoreSystem.sln`) | all |
| `Serilog:MinimumLevel:*` | Information, `OnlineStoreSystem` = Debug in Development | all |

---

## Design decisions

* **Orchestration, not choreography** – OrderService owns the workflow and its state (`SagaStates`), so the business
  process is readable in one class and visible per order (`saga.steps`). Participants stay simple.
* **Async where it adds resilience, sync where the answer is needed now** – product validation and payment are
  synchronous (the caller needs prices / the payment outcome); stock reservation and all compensations are messages,
  so they survive outages of InventoryService or the broker.
* **Transactional outbox** – order + saga + event are one ACID transaction; the relay publishes with confirms and
  only then marks the row processed. No dual-write problem.
* **At-least-once + idempotency everywhere** – idempotent consumers (`ProcessedMessages`), deterministic reply ids,
  saga state guards for replies, `Idempotency-Key` for payments, idempotent release/commit.
* **Explicit acknowledgements and bounded retries** – one requeue for transient errors, then the DLQ; poison messages
  go straight to the DLQ instead of blocking the queue.
* **Card data** – never logged, stored only encrypted (Data Protection) until the payment step and then wiped;
  PaymentService keeps brand + last 4 only.
* **Optimistic concurrency** – `rowversion` on orders, sagas, stock and payments; conflicts become 409 problem details
  or discarded saga transitions.
* **SQL-Server-friendly keys** – sequential GUIDs generated in the application (so the outbox event can carry the
  id inside the same transaction) without clustered-index fragmentation.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `A network-related or instance-specific error ... (localdb)` | `sqllocaldb start MSSQLLocalDB`; LocalDB is Windows-only – elsewhere set `ConnectionStrings__XDb` (or `SQL_CONNECTION_TEMPLATE` for the scripts) |
| macOS without SQL Server | SQL Server has no native macOS build; point the connection strings at a SQL Server on another machine or an Azure SQL Database (free offer): `Server=tcp:<name>.database.windows.net,1433;Database={db};User Id=...;Password=...;Encrypt=True` |
| `PRECONDITION_FAILED - inequivalent arg 'x-dead-letter-exchange'` | a queue with the same name exists with other arguments – delete the `inventory.*` / `order.*` queues in the RabbitMQ UI and restart the services |
| `/health` shows `rabbitmq: Degraded/Unhealthy` | RabbitMQ not running / wrong credentials. Orders still get accepted (outbox) and are processed once the broker is reachable |
| Order stays `Pending` | InventoryService or RabbitMQ is down. After `Saga:StockReservationTimeoutSeconds` (120 s) the order is cancelled and any late reservation released |
| `dotnet ef` not found | `dotnet tool restore` (uses `dotnet-tools.json`) |
| Port already in use | change `Urls` in `appsettings.json` and the matching `launchSettings.json` / `Services:*` / `Apis:*` URLs |
| Re-scaffold overwrote my code | keep custom code in partial classes like `InventoryDbContext.Customizations.cs` – never in scaffolded files |

---

## How this repository was verified

* `dotnet build OnlineStoreSystem.sln` – 0 warnings, 0 errors (`TreatWarningsAsErrors` is on everywhere).
* All SQL scripts parse with Microsoft's T-SQL parser (`Microsoft.SqlServer.TransactSql.ScriptDom`, TSql170).
* The scaffolded EF Core models were compared with the DDL (`dotnet ef dbcontext script` vs `sql/0x_*.sql`):
  every table, column type, nullability, identity, primary key, foreign key (incl. cascade), index, uniqueness and
  filter matches.
* All four OpenAPI documents validate (`openapi-spec-validator`), expose exactly the operations and status codes
  the controllers implement, and 50 live requests (success and error cases) were validated against them with
  `openapi-core`.
* End-to-end runs of all five applications against a real RabbitMQ 3.12 broker: happy path, both compensation
  triggers, insufficient stock, Catalog transient faults and outage (retries, 503, circuit breaker), Payment faults
  and outage (idempotent retries, recovery worker), RabbitMQ outage (outbox + automatic recovery), duplicate
  delivery (idempotency), poison messages (DLQ), Storefront checkout, correlation id in all five log files, and the
  Postman collection through newman (204/204 assertions).
* Limitation of the verification environment: no SQL Server instance was available there, so the end-to-end runs used
  the services' real `Program.cs` with the EF Core provider swapped for InMemory. Everything SQL-specific was
  verified statically as described above; please run STEP 1 and STEP 10 against your SQL Server to confirm.
