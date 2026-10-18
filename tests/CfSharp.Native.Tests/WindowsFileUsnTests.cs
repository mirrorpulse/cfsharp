using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CfSharp.Native.Tests;

public sealed class WindowsFileUsnTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.16299")]
    public void DirectoryUsnDistinguishesOwnMetadataFromChildNamespaceChanges()
    {
        string path = Path.Combine(Path.GetTempPath(), "CfSharp-directory-usn", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            long original = WindowsFileUsn.Read(path, expectedDirectory: true);
            string child = Path.Combine(path, "child.txt");
            File.WriteAllText(child, "local content");
            File.Delete(child);
            Assert.Equal(original, WindowsFileUsn.Read(path, expectedDirectory: true));
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            Assert.NotEqual(original, WindowsFileUsn.Read(path, expectedDirectory: true));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void ReadFileUsnDataLayoutMatchesWindowsSdk()
    {
        Assert.Equal(4, Marshal.SizeOf<WindowsFileUsn.ReadFileUsnData>());
        Assert.Equal(0, Marshal.OffsetOf<WindowsFileUsn.ReadFileUsnData>("MinMajorVersion").ToInt32());
        Assert.Equal(2, Marshal.OffsetOf<WindowsFileUsn.ReadFileUsnData>("MaxMajorVersion").ToInt32());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<WindowsFileUsn.ReadFileUsnData>());
        Assert.Equal(0x000900ebu, WindowsFileUsn.ReadFileUsnDataControlCode);
    }

    [Theory]
    [InlineData(2, 60, 24)]
    [InlineData(3, 76, 40)]
    public void ParsesVersionedRecordsWithoutConfusingFileIdWithUsn(int version, int headerLength, int usnOffset)
    {
        byte[] record = CreateRecord(version, headerLength);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), 17);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(usnOffset), 0x123456789abcdef);
        Assert.Equal(0x123456789abcdef, WindowsFileUsn.Parse(record));

        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(usnOffset), 0);
        Assert.Equal(0, WindowsFileUsn.Parse(record));
    }

    [Fact]
    public void RejectsMalformedAndUnsupportedRecords()
    {
        Assert.Throws<InvalidDataException>(() => WindowsFileUsn.Parse(new byte[7]));
        byte[] record = CreateRecord(2, 60);
        BinaryPrimitives.WriteUInt32LittleEndian(record, 24);
        Assert.Throws<InvalidDataException>(() => WindowsFileUsn.Parse(record));
        BinaryPrimitives.WriteUInt32LittleEndian(record, 61);
        Assert.Throws<InvalidDataException>(() => WindowsFileUsn.Parse(record));
        Assert.Throws<InvalidDataException>(() => WindowsFileUsn.Parse(CreateRecord(3, 60)));
        Assert.Throws<NotSupportedException>(() => WindowsFileUsn.Parse(CreateRecord(4, 80)));
    }

    private static byte[] CreateRecord(int version, int length)
    {
        byte[] record = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, checked((uint)length));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), checked((ushort)version));
        return record;
    }
}
