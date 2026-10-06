-- File: sql/99_ResetDemoData.sql
-- =============================================================================
-- OnlineStoreSystem :: reset transactional demo data WITHOUT dropping databases.
-- Use between demo runs (services may stay running).
--   * InventoryDb : stock restored to seed levels, reservations + idempotency ledger cleared
--   * PaymentDb   : all payment transactions and refunds deleted
--   * OrderDb     : all orders, saga states, step logs and outbox messages deleted
--   * CatalogDb   : seeded products 1-5 restored (price/active flag), API-created products kept
--
-- Requires scripts 01-04 to have been executed first.
-- HOW TO RUN (SSMS): open this file, press F5.
-- HOW TO RUN (CLI) : sqlcmd -S "(localdb)\mssqllocaldb" -E -C -b -i sql/99_ResetDemoData.sql
-- =============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

USE [InventoryDb];
GO
BEGIN TRANSACTION;
    DELETE FROM [dbo].[StockReservations];
    DELETE FROM [dbo].[ProcessedMessages];

    MERGE [dbo].[InventoryItems] AS target
    USING (VALUES
        (1, N'KB-MECH-001', 100, 10),
        (2, N'MS-WL-002',   250, 25),
        (3, N'MN-4K-003',    40,  5),
        (4, N'HP-ANC-004',   75, 10),
        (5, N'CM-8K-005',    10,  2)
    ) AS source ([ProductId], [Sku], [QuantityOnHand], [ReorderThreshold])
        ON target.[ProductId] = source.[ProductId]
    WHEN MATCHED THEN
        UPDATE SET target.[QuantityOnHand]   = source.[QuantityOnHand],
                   target.[QuantityReserved] = 0,
                   target.[ReorderThreshold] = source.[ReorderThreshold],
                   target.[UpdatedAtUtc]     = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT ([ProductId], [Sku], [QuantityOnHand], [QuantityReserved], [ReorderThreshold], [CreatedAtUtc])
        VALUES (source.[ProductId], source.[Sku], source.[QuantityOnHand], 0, source.[ReorderThreshold], SYSUTCDATETIME());
COMMIT TRANSACTION;
GO

USE [PaymentDb];
GO
BEGIN TRANSACTION;
    DELETE FROM [dbo].[PaymentRefunds];
    DELETE FROM [dbo].[PaymentTransactions];
COMMIT TRANSACTION;
GO

USE [OrderDb];
GO
BEGIN TRANSACTION;
    DELETE FROM [dbo].[OutboxMessages];
    -- Cascades to OrderItems, SagaStates and SagaStepLogs.
    DELETE FROM [dbo].[Orders];
COMMIT TRANSACTION;
GO

USE [CatalogDb];
GO
BEGIN TRANSACTION;
    UPDATE p
    SET p.[Price]        = s.[Price],
        p.[IsActive]     = 1,
        p.[UpdatedAtUtc] = SYSUTCDATETIME()
    FROM [dbo].[Products] AS p
    INNER JOIN (VALUES
        (1, 129.99), (2, 49.99), (3, 399.00), (4, 249.50), (5, 9999.00)
    ) AS s ([ProductId], [Price])
        ON s.[ProductId] = p.[ProductId];
COMMIT TRANSACTION;
GO

PRINT N'Demo data reset complete.';
SELECT [ProductId], [Sku], [QuantityOnHand], [QuantityReserved], [QuantityAvailable]
FROM [InventoryDb].[dbo].[InventoryItems]
ORDER BY [ProductId];
GO
