// File: src/OnlineStoreSystem.Shared/Identifiers/SequentialGuid.cs
namespace OnlineStoreSystem.Shared.Identifiers;

/// <summary>
/// Generates GUIDs that are ever-increasing under SQL Server's <c>uniqueidentifier</c> sort order
/// (which compares bytes 10-15 first, then 8-9). Using them as clustered primary keys avoids page
/// splits, exactly like <c>NEWSEQUENTIALID()</c>, while letting the application know the id BEFORE
/// the INSERT (needed to write the order and its outbox event in one transaction).
/// Same algorithm as EF Core's <c>SequentialGuidValueGenerator</c>.
/// </summary>
public static class SequentialGuid
{
    private static long _counter = DateTime.UtcNow.Ticks;

    public static Guid NewGuid()
    {
        Span<byte> guidBytes = stackalloc byte[16];
        Guid.NewGuid().TryWriteBytes(guidBytes);

        var counter = Interlocked.Increment(ref _counter);
        Span<byte> counterBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(counterBytes, counter);
        if (!BitConverter.IsLittleEndian)
        {
            counterBytes.Reverse();
        }

        guidBytes[08] = counterBytes[1];
        guidBytes[09] = counterBytes[0];
        guidBytes[10] = counterBytes[7];
        guidBytes[11] = counterBytes[6];
        guidBytes[12] = counterBytes[5];
        guidBytes[13] = counterBytes[4];
        guidBytes[14] = counterBytes[3];
        guidBytes[15] = counterBytes[2];

        return new Guid(guidBytes);
    }
}
