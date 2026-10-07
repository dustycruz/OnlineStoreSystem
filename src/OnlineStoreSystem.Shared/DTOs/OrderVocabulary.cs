// File: src/OnlineStoreSystem.Shared/DTOs/OrderVocabulary.cs
namespace OnlineStoreSystem.Shared.DTOs;

/// <summary>Values of <c>OrderDb.Orders.Status</c> (see CK_Orders_Status).</summary>
public static class OrderStatuses
{
    public const string Draft = "Draft";
    public const string Pending = "Pending";
    public const string StockReserved = "StockReserved";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";

    public static bool IsTerminal(string status) => status is Completed or Cancelled;
}

/// <summary>Values of <c>OrderDb.SagaStates.Status</c> (see CK_SagaStates_Status).</summary>
public static class SagaStatuses
{
    public const string Running = "Running";
    public const string Compensating = "Compensating";
    public const string Completed = "Completed";
    public const string Compensated = "Compensated";
    public const string Failed = "Failed";

    public static bool IsTerminal(string status) => status is Completed or Compensated or Failed;
}

/// <summary>Values of <c>OrderDb.SagaStates.CurrentStep</c> / <c>SagaStepLogs.StepName</c>.</summary>
public static class SagaSteps
{
    public const string ValidateProducts = "ValidateProducts";
    public const string CreateOrder = "CreateOrder";
    public const string ReserveStock = "ReserveStock";
    public const string ProcessPayment = "ProcessPayment";
    public const string CompleteOrder = "CompleteOrder";
    public const string ReleaseStock = "ReleaseStock";
    public const string Done = "Done";
}

/// <summary>Values of <c>OrderDb.SagaStepLogs.Outcome</c> (see CK_SagaStepLogs_Outcome).</summary>
public static class SagaStepOutcomes
{
    public const string Started = "Started";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Retrying = "Retrying";
    public const string Compensating = "Compensating";
    public const string Compensated = "Compensated";
    public const string Skipped = "Skipped";
}
