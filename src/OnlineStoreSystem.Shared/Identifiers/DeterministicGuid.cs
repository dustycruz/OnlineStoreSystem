// File: src/OnlineStoreSystem.Shared/Identifiers/DeterministicGuid.cs
using System.Security.Cryptography;
using System.Text;

namespace OnlineStoreSystem.Shared.Identifiers;

/// <summary>
/// Name-based (RFC 4122 version 5, SHA-1) GUIDs: the same (namespace, name) pair always yields the
/// same id. Used so that a reply re-sent for a redelivered message carries the SAME EventId as the
/// original reply, keeping downstream de-duplication effective.
/// </summary>
public static class DeterministicGuid
{
    public static Guid Create(Guid namespaceId, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Span<byte> namespaceBytes = stackalloc byte[16];
        namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var buffer = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(buffer);
        nameBytes.CopyTo(buffer.AsSpan(namespaceBytes.Length));

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(buffer, hash);

        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50); // version 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant

        return new Guid(guidBytes, bigEndian: true);
    }
}
