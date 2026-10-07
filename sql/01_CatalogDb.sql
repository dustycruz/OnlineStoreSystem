-- File: sql/01_CatalogDb.sql
-- =============================================================================
-- OnlineStoreSystem :: CatalogDb
-- Owner service     : OnlineStoreSystem.CatalogService (port 5001)
-- Pattern           : Database-per-service, DATABASE-FIRST (scaffolded with EF Core)
--
-- HOW TO RUN (SSMS):
--   1. Connect Object Explorer to (localdb)\mssqllocaldb, .\SQLEXPRESS or localhost.
--   2. File > Open > File... > select this script.
--   3. Press F5 (Execute). The script is re-runnable: it DROPS and RECREATES CatalogDb.
--
-- HOW TO RUN (command line):
--   sqlcmd -S "(localdb)\mssqllocaldb" -E -C -b -i sql/01_CatalogDb.sql
--   sqlcmd -S localhost -U sa -P "<password>" -C -b -i sql/01_CatalogDb.sql
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

IF DB_ID(N'CatalogDb') IS NOT NULL
BEGIN
    PRINT N'Dropping existing database [CatalogDb]...';
    ALTER DATABASE [CatalogDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [CatalogDb];
END
GO

PRINT N'Creating database [CatalogDb]...';
CREATE DATABASE [CatalogDb];
GO

-- Readers never block writers (and vice versa) under row versioning.
ALTER DATABASE [CatalogDb] SET READ_COMMITTED_SNAPSHOT ON;
GO

USE [CatalogDb];
GO

-- -----------------------------------------------------------------------------
-- Table: dbo.Products
-- The product catalog. ProductId is the identifier other services reference
-- (OrderDb.OrderItems.ProductId, InventoryDb.InventoryItems.ProductId) WITHOUT a
-- foreign key, because each service owns its own database.
-- -----------------------------------------------------------------------------
CREATE TABLE [dbo].[Products]
(
    [ProductId]     INT             IDENTITY(1, 1) NOT NULL,
    [Sku]           NVARCHAR(64)    NOT NULL,
    [Name]          NVARCHAR(200)   NOT NULL,
    [Description]   NVARCHAR(2000)  NULL,
    [Category]      NVARCHAR(100)   NOT NULL CONSTRAINT [DF_Products_Category]     DEFAULT (N'General'),
    [Price]         DECIMAL(18, 2)  NOT NULL,
    [Currency]      CHAR(3)         NOT NULL CONSTRAINT [DF_Products_Currency]     DEFAULT ('USD'),
    [IsActive]      BIT             NOT NULL,
    [CreatedAtUtc]  DATETIME2(3)    NOT NULL CONSTRAINT [DF_Products_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc]  DATETIME2(3)    NULL,
    [RowVersion]    ROWVERSION      NOT NULL,

    CONSTRAINT [PK_Products] PRIMARY KEY CLUSTERED ([ProductId] ASC),
    CONSTRAINT [CK_Products_Price_NonNegative] CHECK ([Price] >= 0),
    CONSTRAINT [CK_Products_Currency_Iso4217] CHECK (LEN([Currency]) = 3 AND [Currency] = UPPER([Currency]) COLLATE Latin1_General_CS_AS),
    CONSTRAINT [CK_Products_Sku_NotBlank] CHECK (LEN(LTRIM(RTRIM([Sku]))) > 0),
    CONSTRAINT [CK_Products_Name_NotBlank] CHECK (LEN(LTRIM(RTRIM([Name]))) > 0)
);
GO

-- A SKU identifies exactly one product.
CREATE UNIQUE NONCLUSTERED INDEX [UX_Products_Sku]
    ON [dbo].[Products] ([Sku] ASC);
GO

-- Storefront browsing: "active products in a category".
CREATE NONCLUSTERED INDEX [IX_Products_Category_IsActive]
    ON [dbo].[Products] ([Category] ASC, [IsActive] ASC);
GO

-- Name search / ordering.
CREATE NONCLUSTERED INDEX [IX_Products_Name]
    ON [dbo].[Products] ([Name] ASC);
GO

-- -----------------------------------------------------------------------------
-- Seed data: 5 products with fixed identities so InventoryDb seed rows line up.
-- Product 5 is priced at exactly 9999.00: ordering ONE unit makes the order total
-- 9999.00, which PaymentService deterministically DECLINES -> saga compensation.
-- -----------------------------------------------------------------------------
SET IDENTITY_INSERT [dbo].[Products] ON;
GO

INSERT INTO [dbo].[Products]
    ([ProductId], [Sku], [Name], [Description], [Category], [Price], [Currency], [IsActive], [CreatedAtUtc])
VALUES
    (1, N'KB-MECH-001', N'Mechanical Keyboard',
        N'Hot-swappable 75% mechanical keyboard with tactile switches and PBT keycaps.',
        N'Peripherals', 129.99, 'USD', 1, SYSUTCDATETIME()),
    (2, N'MS-WL-002',   N'Wireless Mouse',
        N'Ergonomic 2.4 GHz / Bluetooth mouse, 4000 DPI sensor, 70-day battery.',
        N'Peripherals',  49.99, 'USD', 1, SYSUTCDATETIME()),
    (3, N'MN-4K-003',   N'27-inch 4K Monitor',
        N'27-inch IPS 3840x2160 monitor, 99% sRGB, USB-C 90 W power delivery.',
        N'Displays',    399.00, 'USD', 1, SYSUTCDATETIME()),
    (4, N'HP-ANC-004',  N'Noise-Cancelling Headphones',
        N'Over-ear active noise-cancelling headphones with 30-hour battery life.',
        N'Audio',       249.50, 'USD', 1, SYSUTCDATETIME()),
    (5, N'CM-8K-005',   N'Cinema Camera Pro 8K',
        N'Full-frame 8K cinema camera body. DEMO: an order total of exactly 9999.00 is declined by PaymentService and triggers the saga compensation path.',
        N'Cameras',    9999.00, 'USD', 1, SYSUTCDATETIME());
GO

-- Explicit identity values above the current seed advance the seed automatically,
-- so the next product created through the API receives ProductId = 6.
SET IDENTITY_INSERT [dbo].[Products] OFF;
GO

PRINT N'CatalogDb created and seeded:';
SELECT [ProductId], [Sku], [Name], [Category], [Price], [Currency], [IsActive]
FROM [dbo].[Products]
ORDER BY [ProductId];
GO
