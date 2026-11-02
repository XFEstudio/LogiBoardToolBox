using System.Buffers.Binary;

namespace LogiBoardToolBox.Core.Onboard;

public static class SectorChecksum
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    public static bool IsValid(ReadOnlySpan<byte> sector) => sector.Length > 2 &&
        BinaryPrimitives.ReadUInt16BigEndian(sector[^2..]) == Compute(sector[..^2]);

    public static bool IsErased(ReadOnlySpan<byte> sector) => sector.Length > 2 && sector.IndexOfAnyExcept((byte)0xFF) < 0;

    public static byte[] Seal(byte[] sector)
    {
        if (sector.Length < 3) throw new ArgumentException("扇区长度不足。", nameof(sector));
        BinaryPrimitives.WriteUInt16BigEndian(sector.AsSpan(sector.Length - 2), Compute(sector.AsSpan(0, sector.Length - 2)));
        return sector;
    }
}
