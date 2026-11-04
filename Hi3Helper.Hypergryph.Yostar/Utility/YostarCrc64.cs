using System;
using System.Buffers;
using System.IO;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Hypergryph.Yostar.Utility;

internal static class YostarCrc64
{
    private const ulong Polynomial = 0xC96C5795D7870F42UL;
    private static readonly ulong[] Table = CreateTable();

    public static async Task<string> ComputeFileAsync(string filePath, CancellationToken token)
    {
        ulong crc = ulong.MaxValue;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
                crc = Append(crc, buffer.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (~crc).ToString(CultureInfo.InvariantCulture);
    }

    private static ulong Append(ulong crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
            crc = Table[(byte)(crc ^ value)] ^ (crc >> 8);
        return crc;
    }

    private static ulong[] CreateTable()
    {
        var table = new ulong[256];
        for (int i = 0; i < table.Length; i++)
        {
            ulong value = (byte)i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            table[i] = value;
        }

        return table;
    }
}
