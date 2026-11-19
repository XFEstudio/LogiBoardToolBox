using System.Buffers.Binary;
using System.Security.Cryptography;
using LogiBoardToolBox.Core.Firmware;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class HeroDfuImageTests
{
    // Synthetic payload with the observed container layout; no proprietary code
    // or end-user identifiers are included in this fixture.
    internal static byte[] Container()
    {
        var image = new byte[HeroDfuImage.ContainerSize];
        Convert.FromHexString("0001553132375F44310000000000000000").CopyTo(image, 0);
        Descriptor(16, 1, HeroDfuImage.ApplicationAddress, HeroDfuImage.ApplicationSize);
        Descriptor(32 + HeroDfuImage.ApplicationSize, 2, 0, HeroDfuImage.CheckDataSize);
        Descriptor(48 + HeroDfuImage.ApplicationSize + HeroDfuImage.CheckDataSize, 3, 0, 0);
        for (var i = 0; i < HeroDfuImage.ApplicationSize; i++) image[32 + i] = (byte)(i % 251);
        image.AsSpan(48 + HeroDfuImage.ApplicationSize, HeroDfuImage.CheckDataSize).Fill(0xA5);
        return image;
        void Descriptor(int offset, byte kind, uint address, int length)
        {
            image[offset] = kind;
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset + 1, 4), address);
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset + 5, 4), (uint)length);
        }
    }

    [Test]
    public void InspectionDistinguishesApplicationOpaqueCheckDataAndBootloader()
    {
        var raw = Container(); var image = HeroDfuImage.Parse(raw);
        Assert.Equal("U127_D1", image.Magic); Assert.Equal((byte)0, image.Entity);
        Assert.Equal((byte)1, image.TransferMode); Assert.Equal(2820, image.PacketCount);
        Assert.Equal(false, image.BootloaderIncluded);
        Assert.Equal(false, image.MatchesOfficialReference);
        Assert.Equal(false, image.ApplicationMatchesResearchReference);
        Assert.Equal(false, image.CheckDataAuthenticated);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(raw.AsSpan(32, 44800))), image.ApplicationSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(raw.AsSpan(44848, 256))), image.CheckDataSha256);
    }

    [Test]
    public void WrongTargetAddressSizesReservedFieldsAndTruncationAreRejected()
    {
        var raw = Container();
        foreach (var offset in new[] { 0, 1, 2, 9, 15, 16, 17, 20, 21, 24, 25, 31, 44832, 44833, 44840, 44841, 45104, 45119 })
        { var bad = raw.ToArray(); bad[offset] ^= 1; Reject(() => HeroDfuImage.Parse(bad)); }
        foreach (var length in new[] { 0, 15, 16, 32, 44832, raw.Length - 16, raw.Length - 1 })
            Reject(() => HeroDfuImage.Parse(raw.AsSpan(0, length)));
        Reject(() => HeroDfuImage.Parse(raw.Concat(new byte[16]).ToArray()));
    }

    [Test]
    public void ApplicationEditsAndCopiedCheckDataCannotBeMistakenForAuthenticatedFirmware()
    {
        var raw = Container(); var original = HeroDfuImage.Parse(raw);
        raw[32 + 50] ^= 1; var modified = HeroDfuImage.Parse(raw);
        Assert.Equal(false, modified.Sha256 == original.Sha256);
        Assert.Equal(false, modified.ApplicationSha256 == original.ApplicationSha256);
        Assert.Equal(original.CheckDataSha256, modified.CheckDataSha256);
        Assert.Equal(false, modified.CheckDataAuthenticated);
        var copy = modified.CopyApplication(); copy[0] ^= 1;
        Assert.Equal(false, copy.SequenceEqual(modified.CopyApplication()));
        raw[44848] ^= 1;
        Assert.Equal(modified.CheckDataSha256, HeroDfuImage.Parse(Container()).CheckDataSha256);
    }

    [Test]
    public void PacketWindowUsesStartThenOneTwoThreeZeroWithoutRestartOrImplicitPadding()
    {
        Assert.Equal("4,1,2,3,0,1,2,3,0", string.Join(',', Enumerable.Range(0, 9).Select(HeroDfuImage.PacketFunction)));
        Assert.Equal((byte)3, HeroDfuImage.PacketFunction(2819));
        foreach (var i in new[] { -1, 2820, int.MaxValue })
        {
            try { HeroDfuImage.PacketFunction(i); throw new Exception("Expected bounds rejection"); }
            catch (ArgumentOutOfRangeException) { }
        }
    }

    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected invalid DFU rejection"); }
}
