// File: src/OnlineStoreSystem.InventoryService/Data/DbUpdateExceptionExtensions.cs
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace OnlineStoreSystem.InventoryService.Data;

public static class DbUpdateExceptionExtensions
{
    extension(DbUpdateException exception)
    {
        /// <summary>SQL Server 2601 (unique index) / 2627 (unique / primary key constraint).</summary>
        public bool IsUniqueConstraintViolation =>
            exception.InnerException is SqlException { Number: 2601 or 2627 };
    }
}
