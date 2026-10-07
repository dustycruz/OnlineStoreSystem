-- File: sql/03_PaymentDb.sql
-- =============================================================================
-- OnlineStoreSystem :: PaymentDb
-- Owner service     : OnlineStoreSystem.PaymentService (port 5004)
-- Pattern           : Database-per-service, DATABASE-FIRST (scaffolded with EF Core)
--
-- Tables:
--   dbo.PaymentTransactions - one row per charge attempt (Captured / Declined / Refunded ...)
--   dbo.PaymentRefunds      - refund history (FK -> PaymentTransactions)
--
-- PCI note: only the last 4 digits and brand of a card are ever stored.
--
-- HOW TO RUN (SSMS): open this file, press F5. Re-runnable: DROPS and RECREATES PaymentDb.
-- HOW TO RUN (CLI) : sqlcmd -S "(localdb)\mssqllocaldb" -E -C -b -i sql/03_PaymentDb.sql
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

IF DB_ID(N'PaymentDb') IS NOT NULL
BEGIN
    PRINT N'Dropping existing database [PaymentDb]...';
    ALTER DATABASE [PaymentDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [PaymentDb];
END
GO

PRINT N'Creating database [PaymentDb]...';
CREATE DATABASE [PaymentDb];
GO

ALTER DATABASE [PaymentDb] SET READ_COMMITTED_SNAPSHOT ON;
GO

USE [PaymentDb];
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.PaymentTransactions
-- IdempotencyKey (from the Idempotency-Key HTTP header) makes POST /charge safe to
-- retry: a replayed request returns the originally stored outcome instead of
-- charging twice. RequestHash detects a key being reused with a different payload.
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[PaymentTransactions]
(
    [PaymentId]       UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_PaymentTransactions_PaymentId]      DEFAULT (NEWSEQUENTIALID()),
    [OrderId]         UNIQUEIDENTIFIER NOT NULL,
    [Amount]          DECIMAL(18, 2)   NOT NULL,
    [Currency]        CHAR(3)          NOT NULL,
    [Status]          NVARCHAR(20)     NOT NULL,
    [CardHolderName]  NVARCHAR(200)    NOT NULL,
    [CardLast4]       CHAR(4)          NOT NULL,
    [CardBrand]       NVARCHAR(20)     NOT NULL,
    [DeclineCode]     NVARCHAR(50)     NULL,
    [FailureReason]   NVARCHAR(500)    NULL,
    [RefundedAmount]  DECIMAL(18, 2)   NOT NULL CONSTRAINT [DF_PaymentTransactions_RefundedAmount] DEFAULT (0),
    [IdempotencyKey]  NVARCHAR(100)    NULL,
    [RequestHash]     CHAR(64)         NULL,
    [CorrelationId]   NVARCHAR(64)     NULL,
    [CreatedAtUtc]    DATETIME2(3)     NOT NULL CONSTRAINT [DF_PaymentTransactions_CreatedAtUtc]   DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc]    DATETIME2(3)     NULL,
    [RowVersion]      ROWVERSION       NOT NULL,

    CONSTRAINT [PK_PaymentTransactions] PRIMARY KEY CLUSTERED ([PaymentId] ASC),
    CONSTRAINT [CK_PaymentTransactions_Amount_Positive] CHECK ([Amount] > 0),
    CONSTRAINT [CK_PaymentTransactions_Status]
        CHECK ([Status] IN (N'Captured', N'Declined', N'PartiallyRefunded', N'Refunded')),
    CONSTRAINT [CK_PaymentTransactions_RefundedAmount_Range]
        CHECK ([RefundedAmount] >= 0 AND [RefundedAmount] <= [Amount]),
    CONSTRAINT [CK_PaymentTransactions_CardLast4_Digits]
        CHECK ([CardLast4] NOT LIKE '%[^0-9]%')
);
GO

-- Idempotency: a key can be used once. Filtered so many NULL keys are allowed.
CREATE UNIQUE NONCLUSTERED INDEX [UX_PaymentTransactions_IdempotencyKey]
    ON [dbo].[PaymentTransactions] ([IdempotencyKey] ASC)
    WHERE [IdempotencyKey] IS NOT NULL;
GO

-- Lookup of all payment attempts for an order.
CREATE NONCLUSTERED INDEX [IX_PaymentTransactions_OrderId]
    ON [dbo].[PaymentTransactions] ([OrderId] ASC);
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.PaymentRefunds
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[PaymentRefunds]
(
    [RefundId]       UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_PaymentRefunds_RefundId]     DEFAULT (NEWSEQUENTIALID()),
    [PaymentId]      UNIQUEIDENTIFIER NOT NULL,
    [Amount]         DECIMAL(18, 2)   NOT NULL,
    [Reason]         NVARCHAR(500)    NULL,
    [CorrelationId]  NVARCHAR(64)     NULL,
    [CreatedAtUtc]   DATETIME2(3)     NOT NULL CONSTRAINT [DF_PaymentRefunds_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),

    CONSTRAINT [PK_PaymentRefunds] PRIMARY KEY CLUSTERED ([RefundId] ASC),
    CONSTRAINT [FK_PaymentRefunds_PaymentTransactions]
        FOREIGN KEY ([PaymentId]) REFERENCES [dbo].[PaymentTransactions] ([PaymentId]),
    CONSTRAINT [CK_PaymentRefunds_Amount_Positive] CHECK ([Amount] > 0)
);
GO

CREATE NONCLUSTERED INDEX [IX_PaymentRefunds_PaymentId]
    ON [dbo].[PaymentRefunds] ([PaymentId] ASC);
GO

PRINT N'PaymentDb created (no seed data: transactions are produced by the saga).';
SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
ORDER BY TABLE_NAME;
GO
