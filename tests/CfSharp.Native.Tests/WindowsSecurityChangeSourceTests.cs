using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace CfSharp.Native.Tests;

public sealed class WindowsSecurityChangeSourceTests
{
    [Fact]
    public async Task NativeSecuritySubscriptionSurvivesItsStartingThread()
    {
        string root = Path.Combine(Path.GetTempPath(), "CfSharp-security-thread", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "before.txt");
        File.WriteAllText(path, "unchanged");
        TaskCompletionSource<WindowsSecurityChange> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<WindowsSecurityChangeSource> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread starter = new(() =>
        {
            try { ready.TrySetResult(new(root, 4096, change => observed.TrySetResult(change), (error, _) => observed.TrySetException(new System.ComponentModel.Win32Exception(error)))); }
            catch (Exception exception) { ready.TrySetException(exception); }
        });
        try
        {
            starter.Start();
            Assert.True(starter.Join(TimeSpan.FromSeconds(10)));
            await using WindowsSecurityChangeSource source = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            FileSecurity descriptor = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            descriptor.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadPermissions, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(descriptor);
            WindowsSecurityChange change = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("before.txt", change.RelativePath);
            Assert.NotEqual(0ul, change.FileId);
            Assert.NotEqual(0ul, change.ParentFileId);
            Assert.False(change.IsDirectory);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExtendedSecurityRecordLayoutAndBoundsMatchTheWindowsContract()
    {
        Assert.Equal(88, Marshal.SizeOf<WindowsSecurityChangeSource.FileNotifyExtendedInformation>());
        Assert.Equal(64, Marshal.OffsetOf<WindowsSecurityChangeSource.FileNotifyExtendedInformation>("FileId").ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<WindowsSecurityChangeSource.FileNotifyExtendedInformation>("ParentFileId").ToInt32());
        Assert.Equal(84, Marshal.OffsetOf<WindowsSecurityChangeSource.FileNotifyExtendedInformation>("FileName").ToInt32());
        byte[] name = Encoding.Unicode.GetBytes("before.txt");
        byte[] record = new byte[84 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56), 0x10);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(64), 0x1000000000001234);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(72), 0x2000000000005678);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(80), (uint)name.Length);
        name.CopyTo(record, 84);
        WindowsSecurityChange change = Assert.Single(WindowsSecurityChangeSource.Decode(record));
        Assert.Equal("before.txt", change.RelativePath);
        Assert.Equal(0x1000000000001234ul, change.FileId);
        Assert.Equal(0x2000000000005678ul, change.ParentFileId);
        Assert.True(change.IsDirectory);
        byte[] unpaired = [.. record.AsSpan(0, 84), 0, 0xd8, 0x61, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(unpaired.AsSpan(80), 4);
        string nativeName = Assert.Single(WindowsSecurityChangeSource.Decode(unpaired)).RelativePath;
        Assert.Equal('\ud800', nativeName[0]);
        Assert.Equal('a', nativeName[1]);
        byte[] pair = [.. record, .. record];
        BinaryPrimitives.WriteUInt32LittleEndian(pair, (uint)record.Length);
        Assert.Equal(2, WindowsSecurityChangeSource.Decode(pair).Length);
        Assert.Throws<InvalidDataException>(() => WindowsSecurityChangeSource.Decode(record.AsSpan(0, 83)));
        byte[] invalid = (byte[])record.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(80), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => WindowsSecurityChangeSource.Decode(invalid));
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(80), 1);
        Assert.Throws<InvalidDataException>(() => WindowsSecurityChangeSource.Decode(invalid));
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(80), (uint)name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(invalid, 4);
        Assert.Throws<InvalidDataException>(() => WindowsSecurityChangeSource.Decode(invalid));
        BinaryPrimitives.WriteUInt32LittleEndian(invalid, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(4), 1);
        Assert.Throws<InvalidDataException>(() => WindowsSecurityChangeSource.Decode(invalid));
    }

    [Fact]
    public async Task PendingNativeSecurityReadIsCanceledBeforeItsResourcesAreReleased()
    {
        string root = Path.Combine(Path.GetTempPath(), "CfSharp-security-cancel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            System.Collections.Concurrent.ConcurrentQueue<int> unexpected = new();
            WindowsSecurityChangeSource source = new(root, 4096, _ => unexpected.Enqueue(-1), (error, _) => unexpected.Enqueue(error));
            Assert.NotEqual(0ul, source.VolumeSerialNumber);
            Task first = source.DisposeAsync().AsTask();
            Task repeated = source.DisposeAsync().AsTask();
            await Task.WhenAll(first, repeated).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(unexpected);
        }
        finally { Directory.Delete(root); }
    }
}
