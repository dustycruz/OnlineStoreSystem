-- File: sql/04_OrderDb.sql
-- =============================================================================
-- OnlineStoreSystem :: OrderDb
-- Owner service     : OnlineStoreSystem.OrderService (port 5002) - SAGA ORCHESTRATOR
-- Pattern           : Database-per-service, DATABASE-FIRST (scaffolded with EF Core)
--
-- Tables:
--   dbo.Orders          - order header (Draft -> Pending -> StockReserved -> Completed | Cancelled)
--   dbo.OrderItems      - order lines with a price snapshot taken from CatalogService
--   dbo.SagaStates      - one orchestrated-saga instance per order (durable state machine)
--   dbo.SagaStepLogs    - audit trail of every saga step / compensation
--   dbo.OutboxMessages  - transactional outbox: events committed atomically with state,
--                         then relayed to RabbitMQ (publisher confirms) by a background worker
--
-- HOW TO RUN (SSMS): open this file, press F5. Re-runnable: DROPS and RECREATES OrderDb.
-- HOW TO RUN (CLI) : sqlcmd -S "(localdb)\mssqllocaldb" -E -C -b -i sql/04_OrderDb.sql
-- =============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
-- Session options required for computed-column indexes and filtered indexes.
-- SSMS turns these on by default; sqlcmd does NOT (QUOTED_IDENTIFIER is OFF there),
-- so they are set explicitly to make the script behave the same in every client.
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

USE [master];
GO

IF DB_ID(N'OrderDb') IS NOT NULL
BEGIN
    PRINT N'Dropping existing database [OrderDb]...';
    ALTER DATABASE [OrderDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [OrderDb];
END
GO

PRINT N'Creating database [OrderDb]...';
CREATE DATABASE [OrderDb];
GO

ALTER DATABASE [OrderDb] SET READ_COMMITTED_SNAPSHOT ON;
GO

USE [OrderDb];
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.Orders
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[Orders]
(
    [OrderId]        UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_Orders_OrderId]      DEFAULT (NEWSEQUENTIALID()),
    [OrderNumber]    NVARCHAR(32)     NOT NULL,
    [CustomerId]     NVARCHAR(100)    NOT NULL,
    [CustomerEmail]  NVARCHAR(256)    NOT NULL,
    [Status]         NVARCHAR(30)     NOT NULL,
    [TotalAmount]    DECIMAL(18, 2)   NOT NULL CONSTRAINT [DF_Orders_TotalAmount]  DEFAULT (0),
    [Currency]       CHAR(3)          NOT NULL CONSTRAINT [DF_Orders_Currency]     DEFAULT ('USD'),
    [FailureReason]  NVARCHAR(1000)   NULL,
    [PaymentId]      UNIQUEIDENTIFIER NULL,
    [CorrelationId]  NVARCHAR(64)     NULL,
    [CreatedAtUtc]   DATETIME2(3)     NOT NULL CONSTRAINT [DF_Orders_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc]   DATETIME2(3)     NULL,
    [RowVersion]     ROWVERSION       NOT NULL,

    CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([OrderId] ASC),
    CONSTRAINT [CK_Orders_Status]
        CHECK ([Status] IN (N'Draft', N'Pending', N'StockReserved', N'Completed', N'Cancelled')),
    CONSTRAINT [CK_Orders_TotalAmount_NonNegative] CHECK ([TotalAmount] >= 0)
);
GO

CREATE UNIQUE NONCLUSTERED INDEX [UX_Orders_OrderNumber]
    ON [dbo].[Orders] ([OrderNumber] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId]
    ON [dbo].[Orders] ([CustomerId] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_Orders_Status_CreatedAtUtc]
    ON [dbo].[Orders] ([Status] ASC, [CreatedAtUtc] ASC);
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.OrderItems
-- UnitPrice / ProductName / Sku are a snapshot from CatalogService at checkout.
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[OrderItems]
(
    [OrderItemId]  INT              IDENTITY(1, 1) NOT NULL,
    [OrderId]      UNIQUEIDENTIFIER NOT NULL,
    [ProductId]    INT              NOT NULL,
    [Sku]          NVARCHAR(64)     NOT NULL,
    [ProductName]  NVARCHAR(200)    NOT NULL,
    [UnitPrice]    DECIMAL(18, 2)   NOT NULL,
    [Quantity]     INT              NOT NULL,
    [LineTotal]    AS (CONVERT(DECIMAL(18, 2), [UnitPrice] * [Quantity])) PERSISTED NOT NULL,

    CONSTRAINT [PK_OrderItems] PRIMARY KEY CLUSTERED ([OrderItemId] ASC),
    CONSTRAINT [FK_OrderItems_Orders]
        FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([OrderId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_OrderItems_Quantity_Positive]   CHECK ([Quantity] > 0),
    CONSTRAINT [CK_OrderItems_UnitPrice_NonNegative] CHECK ([UnitPrice] >= 0)
);
GO

-- One line per product per order (quantities are merged by the API).
CREATE UNIQUE NONCLUSTERED INDEX [UX_OrderItems_OrderId_ProductId]
    ON [dbo].[OrderItems] ([OrderId] ASC, [ProductId] ASC);
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.SagaStates
-- Durable state of the orchestrated checkout saga (one per order).
--   Status      : Running | Compensating | Completed | Compensated | Failed
--   CurrentStep : ValidateProducts | CreateOrder | ReserveStock | ProcessPayment
--                 | CompleteOrder | ReleaseStock | Done
-- ProtectedPaymentDetails holds card data encrypted with ASP.NET Core Data
-- Protection; it is wiped (set to NULL) as soon as the payment step finishes.
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[SagaStates]
(
    [SagaId]                  UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_SagaStates_SagaId]       DEFAULT (NEWSEQUENTIALID()),
    [OrderId]                 UNIQUEIDENTIFIER NOT NULL,
    [Status]                  NVARCHAR(30)     NOT NULL,
    [CurrentStep]             NVARCHAR(50)     NOT NULL,
    [StockReserved]           BIT              NOT NULL,
    [PaymentProcessed]        BIT              NOT NULL,
    [PaymentId]               UNIQUEIDENTIFIER NULL,
    [ProtectedPaymentDetails] NVARCHAR(2000)   NULL,
    [PaymentCardLast4]        CHAR(4)          NULL,
    [RetryCount]              INT              NOT NULL CONSTRAINT [DF_SagaStates_RetryCount]   DEFAULT (0),
    [LastError]               NVARCHAR(2000)   NULL,
    [CorrelationId]           NVARCHAR(64)     NULL,
    [StartedAtUtc]            DATETIME2(3)     NOT NULL CONSTRAINT [DF_SagaStates_StartedAtUtc] DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc]            DATETIME2(3)     NOT NULL CONSTRAINT [DF_SagaStates_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
    [CompletedAtUtc]          DATETIME2(3)     NULL,
    [RowVersion]              ROWVERSION       NOT NULL,

    CONSTRAINT [PK_SagaStates] PRIMARY KEY CLUSTERED ([SagaId] ASC),
    CONSTRAINT [FK_SagaStates_Orders]
        FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([OrderId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_SagaStates_Status]
        CHECK ([Status] IN (N'Running', N'Compensating', N'Completed', N'Compensated', N'Failed')),
    CONSTRAINT [CK_SagaStates_CurrentStep]
        CHECK ([CurrentStep] IN (N'ValidateProducts', N'CreateOrder', N'ReserveStock', N'ProcessPayment',
                                 N'CompleteOrder', N'ReleaseStock', N'Done')),
    CONSTRAINT [CK_SagaStates_RetryCount_NonNegative] CHECK ([RetryCount] >= 0)
);
GO

-- Exactly one saga per order (1:1 relationship).
CREATE UNIQUE NONCLUSTERED INDEX [UX_SagaStates_OrderId]
    ON [dbo].[SagaStates] ([OrderId] ASC);
GO

-- Recovery worker scans for stalled sagas: WHERE Status = ... AND CurrentStep = ... AND UpdatedAtUtc < ...
CREATE NONCLUSTERED INDEX [IX_SagaStates_Status_CurrentStep_UpdatedAtUtc]
    ON [dbo].[SagaStates] ([Status] ASC, [CurrentStep] ASC, [UpdatedAtUtc] ASC);
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.SagaStepLogs
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[SagaStepLogs]
(
    [SagaStepLogId]  BIGINT           IDENTITY(1, 1) NOT NULL,
    [SagaId]         UNIQUEIDENTIFIER NOT NULL,
    [StepName]       NVARCHAR(50)     NOT NULL,
    [Outcome]        NVARCHAR(20)     NOT NULL,
    [Detail]         NVARCHAR(2000)   NULL,
    [OccurredAtUtc]  DATETIME2(3)     NOT NULL CONSTRAINT [DF_SagaStepLogs_OccurredAtUtc] DEFAULT (SYSUTCDATETIME()),

    CONSTRAINT [PK_SagaStepLogs] PRIMARY KEY CLUSTERED ([SagaStepLogId] ASC),
    CONSTRAINT [FK_SagaStepLogs_SagaStates]
        FOREIGN KEY ([SagaId]) REFERENCES [dbo].[SagaStates] ([SagaId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_SagaStepLogs_Outcome]
        CHECK ([Outcome] IN (N'Started', N'Succeeded', N'Failed', N'Retrying', N'Compensating', N'Compensated', N'Skipped'))
);
GO

CREATE NONCLUSTERED INDEX [IX_SagaStepLogs_SagaId_OccurredAtUtc]
    ON [dbo].[SagaStepLogs] ([SagaId] ASC, [OccurredAtUtc] ASC);
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.OutboxMessages (transactional outbox)
-- OutboxMessageId doubles as the AMQP message-id / EventId so consumers can
-- de-duplicate. ProcessedAtUtc IS NULL means "not yet confirmed by the broker".
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[OutboxMessages]
(
    [OutboxMessageId] UNIQUEIDENTIFIER NOT NULL,
    [MessageType]     NVARCHAR(200)    NOT NULL,
    [RoutingKey]      NVARCHAR(200)    NOT NULL,
    [Payload]         NVARCHAR(MAX)    NOT NULL,
    [CorrelationId]   NVARCHAR(64)     NULL,
    [CreatedAtUtc]    DATETIME2(3)     NOT NULL CONSTRAINT [DF_OutboxMessages_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
    [ProcessedAtUtc]  DATETIME2(3)     NULL,
    [Attempts]        INT              NOT NULL CONSTRAINT [DF_OutboxMessages_Attempts]     DEFAULT (0),
    [LastError]       NVARCHAR(2000)   NULL,

    CONSTRAINT [PK_OutboxMessages] PRIMARY KEY CLUSTERED ([OutboxMessageId] ASC),
    CONSTRAINT [CK_OutboxMessages_Attempts_NonNegative] CHECK ([Attempts] >= 0)
);
GO

-- The relay only ever reads pending rows; a filtered index keeps that query tiny.
CREATE NONCLUSTERED INDEX [IX_OutboxMessages_Pending]
    ON [dbo].[OutboxMessages] ([CreatedAtUtc] ASC)
    WHERE [ProcessedAtUtc] IS NULL;
GO

PRINT N'OrderDb created (no seed data: orders are created through the API).';
SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
ORDER BY TABLE_NAME;
GO
