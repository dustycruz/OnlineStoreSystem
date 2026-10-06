-- File: sql/02_InventoryDb.sql
-- =============================================================================
-- OnlineStoreSystem :: InventoryDb
-- Owner service     : OnlineStoreSystem.InventoryService (port 5003)
-- Pattern           : Database-per-service, DATABASE-FIRST (scaffolded with EF Core)
--
-- Tables:
--   dbo.InventoryItems     - stock level per product (on hand / reserved / available)
--   dbo.StockReservations  - per-order reservations (Reserved -> Committed | Released)
--   dbo.ProcessedMessages  - idempotent-consumer ledger (MessageId + Consumer)
--
-- HOW TO RUN (SSMS): open this file, press F5. Re-runnable: DROPS and RECREATES InventoryDb.
-- HOW TO RUN (CLI) : sqlcmd -S "(localdb)\mssqllocaldb" -E -C -b -i sql/02_InventoryDb.sql
-- =============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

USE [master];
GO

IF DB_ID(N'InventoryDb') IS NOT NULL
BEGIN
    PRINT N'Dropping existing database [InventoryDb]...';
    ALTER DATABASE [InventoryDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [InventoryDb];
END
GO

PRINT N'Creating database [InventoryDb]...';
CREATE DATABASE [InventoryDb];
GO

ALTER DATABASE [InventoryDb] SET READ_COMMITTED_SNAPSHOT ON;
GO

USE [InventoryDb];
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.InventoryItems
-- QuantityAvailable is a PERSISTED computed column (OnHand - Reserved) so SSMS
-- reports and the API always agree on what can still be sold.
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[InventoryItems]
(
    [InventoryItemId]   INT             IDENTITY(1, 1) NOT NULL,
    [ProductId]         INT             NOT NULL,
    [Sku]               NVARCHAR(64)    NOT NULL,
    [QuantityOnHand]    INT             NOT NULL,
    [QuantityReserved]  INT             NOT NULL CONSTRAINT [DF_InventoryItems_QuantityReserved] DEFAULT (0),
    [QuantityAvailable] AS ([QuantityOnHand] - [QuantityReserved]) PERSISTED NOT NULL,
    [ReorderThreshold]  INT             NOT NULL CONSTRAINT [DF_InventoryItems_ReorderThreshold] DEFAULT (5),
    [CreatedAtUtc]      DATETIME2(3)    NOT NULL CONSTRAINT [DF_InventoryItems_CreatedAtUtc]     DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc]      DATETIME2(3)    NULL,
    [RowVersion]        ROWVERSION      NOT NULL,

    CONSTRAINT [PK_InventoryItems] PRIMARY KEY CLUSTERED ([InventoryItemId] ASC),
    CONSTRAINT [CK_InventoryItems_QuantityOnHand_NonNegative]   CHECK ([QuantityOnHand] >= 0),
    CONSTRAINT [CK_InventoryItems_QuantityReserved_NonNegative] CHECK ([QuantityReserved] >= 0),
    CONSTRAINT [CK_InventoryItems_Reserved_LTE_OnHand]          CHECK ([QuantityReserved] <= [QuantityOnHand]),
    CONSTRAINT [CK_InventoryItems_ReorderThreshold_NonNegative] CHECK ([ReorderThreshold] >= 0)
);
GO

-- One stock row per catalog product.
CREATE UNIQUE NONCLUSTERED INDEX [UX_InventoryItems_ProductId]
    ON [dbo].[InventoryItems] ([ProductId] ASC);
GO

CREATE UNIQUE NONCLUSTERED INDEX [UX_InventoryItems_Sku]
    ON [dbo].[InventoryItems] ([Sku] ASC);
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.StockReservations
-- One row per (order, inventory item). Status lifecycle:
--   Reserved  -> Committed  (payment succeeded: stock leaves the warehouse)
--   Reserved  -> Released   (compensation: payment failed / order cancelled)
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[StockReservations]
(
    [StockReservationId] BIGINT           IDENTITY(1, 1) NOT NULL,
    [OrderId]            UNIQUEIDENTIFIER NOT NULL,
    [InventoryItemId]    INT              NOT NULL,
    [ProductId]          INT              NOT NULL,
    [Quantity]           INT              NOT NULL,
    [Status]             NVARCHAR(20)     NOT NULL,
    [CorrelationId]      NVARCHAR(64)     NULL,
    [CreatedAtUtc]       DATETIME2(3)     NOT NULL CONSTRAINT [DF_StockReservations_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc]       DATETIME2(3)     NULL,

    CONSTRAINT [PK_StockReservations] PRIMARY KEY CLUSTERED ([StockReservationId] ASC),
    CONSTRAINT [FK_StockReservations_InventoryItems]
        FOREIGN KEY ([InventoryItemId]) REFERENCES [dbo].[InventoryItems] ([InventoryItemId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_StockReservations_Quantity_Positive] CHECK ([Quantity] > 0),
    CONSTRAINT [CK_StockReservations_Status] CHECK ([Status] IN (N'Reserved', N'Committed', N'Released'))
);
GO

-- An order reserves a given inventory item at most once (natural idempotency key).
CREATE UNIQUE NONCLUSTERED INDEX [UX_StockReservations_OrderId_InventoryItemId]
    ON [dbo].[StockReservations] ([OrderId] ASC, [InventoryItemId] ASC);
GO

-- Supporting index for the foreign key (joins / cascade deletes).
CREATE NONCLUSTERED INDEX [IX_StockReservations_InventoryItemId]
    ON [dbo].[StockReservations] ([InventoryItemId] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_StockReservations_Status]
    ON [dbo].[StockReservations] ([Status] ASC);
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.ProcessedMessages
-- Idempotent consumer ledger. A row is inserted in the SAME transaction as the
-- business change, so a redelivered RabbitMQ message (same MessageId) is detected
-- and never applied twice. The composite key lets several consumers share the DB.
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[ProcessedMessages]
(
    [MessageId]      UNIQUEIDENTIFIER NOT NULL,
    [Consumer]       NVARCHAR(200)    NOT NULL,
    [MessageType]    NVARCHAR(200)    NOT NULL,
    [CorrelationId]  NVARCHAR(64)     NULL,
    [ProcessedAtUtc] DATETIME2(3)     NOT NULL CONSTRAINT [DF_ProcessedMessages_ProcessedAtUtc] DEFAULT (SYSUTCDATETIME()),

    CONSTRAINT [PK_ProcessedMessages] PRIMARY KEY CLUSTERED ([MessageId] ASC, [Consumer] ASC)
);
GO

-- Housekeeping: purge old ledger rows by age.
CREATE NONCLUSTERED INDEX [IX_ProcessedMessages_ProcessedAtUtc]
    ON [dbo].[ProcessedMessages] ([ProcessedAtUtc] ASC);
GO

-- -----------------------------------------------------------------------------
-- Seed stock for the 5 catalog products (ProductId values match CatalogDb).
-- -----------------------------------------------------------------------------
INSERT INTO [dbo].[InventoryItems]
    ([ProductId], [Sku], [QuantityOnHand], [QuantityReserved], [ReorderThreshold], [CreatedAtUtc])
VALUES
    (1, N'KB-MECH-001', 100, 0, 10, SYSUTCDATETIME()),
    (2, N'MS-WL-002',   250, 0, 25, SYSUTCDATETIME()),
    (3, N'MN-4K-003',    40, 0,  5, SYSUTCDATETIME()),
    (4, N'HP-ANC-004',   75, 0, 10, SYSUTCDATETIME()),
    (5, N'CM-8K-005',    10, 0,  2, SYSUTCDATETIME());
GO

PRINT N'InventoryDb created and seeded:';
SELECT [InventoryItemId], [ProductId], [Sku], [QuantityOnHand], [QuantityReserved], [QuantityAvailable], [ReorderThreshold]
FROM [dbo].[InventoryItems]
ORDER BY [ProductId];
GO
