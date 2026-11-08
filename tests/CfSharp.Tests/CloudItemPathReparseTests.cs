using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace CfSharp.Tests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed class CloudItemPathReparseTests
{
    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("junction")]
    [InlineData("chain")]
    [InlineData("dangling")]
    public async Task NameSurrogatesCannotEscapePhysicalContainment(string kind)
    {
        using Area area = new();
        string outsideFile = Path.Combine(area.Outside, "retained.txt");
        await File.WriteAllTextAsync(outsideFile, "foreign evidence");
        string link = Path.Combine(area.Root, "link");
        string relative = "link";
        if (kind == "junction")
        {
            await CreateJunctionAsync(link, area.Outside);
            relative += @"\retained.txt";
        }
        else if (kind == "directory")
        {
            Directory.CreateSymbolicLink(link, area.Outside);
            relative += @"\retained.txt";
        }
        else if (kind == "chain")
        {
            File.CreateSymbolicLink(link, "second");
            File.CreateSymbolicLink(Path.Combine(area.Root, "second"), outsideFile);
        }
        else { File.CreateSymbolicLink(link, kind == "dangling" ? Path.Combine(area.Outside, "absent.txt") : outsideFile); }

        Assert.Throws<ArgumentException>(() => CloudItemPathResolver.Resolve(area.Root, relative, false));
        Assert.Equal("foreign evidence", await File.ReadAllTextAsync(outsideFile));
        Assert.False(File.Exists(Path.Combine(area.Outside, "absent.txt")));
    }

    [Fact]
    public async Task RootAliasesAndMissingContainedTargetsRetainTheirPhysicalBoundary()
    {
        using Area area = new();
        string alias = Path.Combine(area.Path, "root-alias");
        await CreateJunctionAsync(alias, area.Root);
        string internalLink = Path.Combine(area.Root, "internal");
        Directory.CreateSymbolicLink(internalLink, "missing");
        CloudItemPath path = CloudItemPathResolver.Resolve(alias, @"internal\child.txt", false);
        Assert.Equal(Path.Combine(alias, "internal", "child.txt"), path.FullPath);
        Assert.Equal(@"internal\child.txt", path.RelativePath);
        Directory.CreateSymbolicLink(Path.Combine(area.Root, "escape"), area.Outside);
        Assert.Throws<ArgumentException>(() => CloudItemPathResolver.Resolve(alias, @"escape\absent.txt", false));
        Assert.Throws<CloudPathReparsePointException>(() => CloudPathHandleLease.OpenParentChains(alias, [path.FullPath]));
    }

    [Fact]
    public void CyclicLinksAreRefusedWithoutUnboundedTraversal()
    {
        using Area area = new();
        File.CreateSymbolicLink(Path.Combine(area.Root, "one"), "two");
        File.CreateSymbolicLink(Path.Combine(area.Root, "two"), "one");
        Assert.Throws<IOException>(() => CloudItemPathResolver.Resolve(area.Root, "one", false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownReparseTagsNeverAcquireContainmentAuthority(bool directory)
    {
        using Area area = new();
        string path = Path.Combine(area.Root, "foreign");
        if (directory) { Directory.CreateDirectory(path); }
        else { File.WriteAllText(path, "foreign metadata"); }
        using SafeFileHandle handle = OpenReparse(path, 0x40000000, 7, 0, 3, 0x02200000, 0);
        Assert.False(handle.IsInvalid, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        // A private non-Microsoft name-surrogate tag with its own GUID. No filter owns it
        // and no substitute path is supplied; the resolver must refuse rather than follow it.
        byte[] buffer = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0x20000042);
        Assert.True(Guid.NewGuid().TryWriteBytes(buffer.AsSpan(8)));
        Assert.True(ReparseControl(handle, 0x000900A4, buffer, (uint)buffer.Length, 0, 0, out _, 0),
            new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        int cleanupError = 0;
        try
        {
            Assert.Throws<CloudPathReparsePointException>(() => CloudItemPathResolver.Resolve(area.Root, "foreign", true));
        }
        finally
        {
            // Remove only this fixture's exact tag and GUID before ordinary directory cleanup.
            if (!ReparseControl(handle, 0x000900AC, buffer, (uint)buffer.Length, 0, 0, out _, 0))
            {
                cleanupError = Marshal.GetLastPInvokeError();
            }
        }
        Assert.Equal(0, cleanupError);
    }

    [Fact]
    public void MetadataAccessDenialRemainsARefusal()
    {
        using Area area = new();
        string path = Path.Combine(area.Root, "denied");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "child.txt"), "retained");
        DirectoryInfo directory = new(path);
        DirectorySecurity original = directory.GetAccessControl(AccessControlSections.Access);
        DirectorySecurity denied = directory.GetAccessControl(AccessControlSections.Access);
        using SafeFileHandle restore = OpenReparse(path, 0x00060000, 7, 0, 3, 0x02200000, 0);
        Assert.False(restore.IsInvalid, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        byte[] descriptor = original.GetSecurityDescriptorBinaryForm();
        int daclOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(16)));
        int daclSize = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(daclOffset + 2));
        byte[] dacl = descriptor.AsSpan(daclOffset, daclSize).ToArray();
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new(user.User!, FileSystemRights.ReadAttributes | FileSystemRights.ListDirectory,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
        directory.SetAccessControl(denied);
        uint restoreError = 0;
        try
        {
            UnauthorizedAccessException error = Assert.Throws<UnauthorizedAccessException>(() =>
                CloudItemPathResolver.Resolve(area.Root, @"denied\child.txt", false));
            Assert.Equal(unchecked((int)0x80070005), error.HResult);
        }
        finally
        {
            // A path-based ACL restore itself queries attributes. Retain WRITE_DAC before
            // denying metadata so cleanup uses the original directory object instead.
            restoreError = SetSecurityInfo(restore, 1, 4, 0, 0, dacl, 0);
        }
        Assert.Equal(0u, restoreError);
        Assert.Equal("retained", File.ReadAllText(Path.Combine(path, "child.txt")));
    }

    [Fact]
    public void VolumeGuidMountTargetsCannotBecomeWorkingDirectoryRelativePaths()
    {
        using Area area = new();
        string path = Path.Combine(area.Root, "volume");
        Directory.CreateDirectory(path);
        char[] volume = new char[64];
        Assert.True(GetVolumeNameForVolumeMountPointW(Path.GetPathRoot(path)!, volume, 64),
            new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        byte[] target = Encoding.Unicode.GetBytes(@"\??\" + new string(volume, 4, Array.IndexOf(volume, '\0') - 4));
        byte[] buffer = new byte[16 + target.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)target.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(target.Length + 2)));
        target.CopyTo(buffer, 16);
        using SafeFileHandle handle = OpenReparse(path, 0x40000000, 7, 0, 3, 0x02200000, 0);
        Assert.False(handle.IsInvalid, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        // This is an actual mount-point reparse tag on an owned empty directory. No volume
        // is attached or changed, and neither the test nor its cleanup enumerates the target.
        Assert.True(ReparseControl(handle, 0x000900A4, buffer, (uint)buffer.Length, 0, 0, out _, 0),
            new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        int cleanupError = 0;
        try { Assert.Throws<IOException>(() => CloudItemPathResolver.Resolve(area.Root, @"volume\foreign.txt", false)); }
        finally
        {
            byte[] deletion = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(deletion, 0xA0000003);
            if (!ReparseControl(handle, 0x000900AC, deletion, (uint)deletion.Length, 0, 0, out _, 0)) { cleanupError = Marshal.GetLastPInvokeError(); }
        }
        Assert.Equal(0, cleanupError);
    }

    private static async Task CreateJunctionAsync(string path, string target)
    {
        ProcessStartInfo start = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", path, target }) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.ExitCode == 0, $"Junction creation failed: {await output}; {await error}");
    }

    private sealed class Area : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CfSharp-path-links", Guid.NewGuid().ToString("N"));
        internal string Root => System.IO.Path.Combine(Path, "root");
        internal string Outside => System.IO.Path.Combine(Path, "outside");
        internal Area() { Directory.CreateDirectory(Root); Directory.CreateDirectory(Outside); }
        public void Dispose() => RemoveDirectory(new(Path));

        private void RemoveDirectory(DirectoryInfo directory)
        {
            Assert.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, directory.FullName + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                foreach (FileSystemInfo entry in directory.GetFileSystemInfos())
                {
                    if (entry is DirectoryInfo child) { RemoveDirectory(child); }
                    else { entry.Delete(); }
                }
            }
            directory.Delete();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenReparse(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReparseControl(SafeFileHandle handle, uint code, byte[] input, uint inputLength, nint output,
        uint outputLength, out uint returned, nint overlapped);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInformation,
        nint owner, nint group, byte[] dacl, nint sacl);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string root, [Out] char[] name, uint capacity);
}
