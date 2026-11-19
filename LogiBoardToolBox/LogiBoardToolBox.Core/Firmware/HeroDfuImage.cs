using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LogiBoardToolBox.Core.Firmware;

/// <summary>
/// Offline inspection of the observed U127_D1 container. Does not implement
/// flash transport or authenticate the vendor's opaque check data. This parser
/// deliberately accepts only the geometry of the researched HERO reference.
/// </summary>
public sealed class HeroDfuImage
{
    public const string ReferenceContainerSha256 = "85FCFC43BA988E361AF5CCFB31C0B1F8238080CE699ADEC9D12F9CE4E381D268";
    public const string ReferenceApplicationSha256 = "82E7FF3B08440ED6F75A1226C6ABB6C95EDD7605947E25A724C2B66260411286";
    public const uint ApplicationAddress = 0x08004000;
    public const int ApplicationSize = 44800;
    public const int PacketSize = 16;
    public const int CheckDataSize = 256;
    public const int ContainerSize = PacketSize * 4 + ApplicationSize + CheckDataSize;
    private readonly byte[] bytes;
    public string Magic { get; }
    public byte Entity { get; }
    public byte TransferMode { get; }
    public string Sha256 { get; }
    public string ApplicationSha256 { get; }
    public string CheckDataSha256 { get; }
    public bool MatchesOfficialReference => Sha256 == ReferenceContainerSha256;
    public bool ApplicationMatchesResearchReference => ApplicationSha256 == ReferenceApplicationSha256;
    public bool CheckDataAuthenticated => false;
    public bool BootloaderIncluded => false;
    public int PacketCount => bytes.Length / PacketSize;

    private HeroDfuImage(byte[] image)
    {
        bytes = image;
        Entity = image[0]; TransferMode = image[1];
        Magic = Encoding.ASCII.GetString(image, 2, 7);
        Sha256 = Convert.ToHexString(SHA256.HashData(image));
        ApplicationSha256 = Convert.ToHexString(SHA256.HashData(image.AsSpan(32, ApplicationSize)));
        CheckDataSha256 = Convert.ToHexString(SHA256.HashData(image.AsSpan(48 + ApplicationSize, CheckDataSize)));
    }

    public static HeroDfuImage Parse(ReadOnlySpan<byte> image)
    {
        if (image.Length != ContainerSize) throw new InvalidDataException("U127_D1 镜像长度与研究基线不符；不支持推测或补齐截断内容。");
        if (image[0] != 0 || image[1] != 1 || !image.Slice(2, 7).SequenceEqual("U127_D1"u8) || !Zeros(image.Slice(9, 7)))
            throw new InvalidDataException("不是当前支持的 HERO U127_D1 头部。");
        CheckDescriptor(image.Slice(16, 16), 1, ApplicationAddress, ApplicationSize);
        CheckDescriptor(image.Slice(32 + ApplicationSize, 16), 2, 0, CheckDataSize);
        CheckDescriptor(image.Slice(48 + ApplicationSize + CheckDataSize, 16), 3, 0, 0);
        return new(image.ToArray());
    }

    private static void CheckDescriptor(ReadOnlySpan<byte> descriptor, byte kind, uint address, int length)
    {
        if (descriptor[0] != kind || BinaryPrimitives.ReadUInt32BigEndian(descriptor[1..5]) != address ||
            BinaryPrimitives.ReadUInt32BigEndian(descriptor[5..9]) != (uint)length || !Zeros(descriptor[9..]))
            throw new InvalidDataException($"U127_D1 段描述不匹配（段 {kind}）。未生成可刷写镜像。");
    }

    private static bool Zeros(ReadOnlySpan<byte> data) => data.IndexOfAnyExcept((byte)0) < 0;

    /// <summary>A copy for offline analysis; mutating it does not change this inspection result.</summary>
    public byte[] CopyApplication() => bytes.AsSpan(32, ApplicationSize).ToArray();

    public byte[] CopyPacket(int packetIndex)
    {
        _ = PacketFunction(packetIndex);
        return bytes.AsSpan(packetIndex * PacketSize, PacketSize).ToArray();
    }

    /// <summary>Observed HID++ 00D0 window sequence. No device is opened or written.</summary>
    public static byte PacketFunction(int packetIndex)
    {
        if (packetIndex is < 0 or >= ContainerSize / PacketSize) throw new ArgumentOutOfRangeException(nameof(packetIndex));
        return packetIndex == 0 ? (byte)4 : (byte)(packetIndex % 4);
    }
}
