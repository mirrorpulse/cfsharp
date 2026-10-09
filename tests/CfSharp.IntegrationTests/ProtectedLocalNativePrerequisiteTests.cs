using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Theory]
    [InlineData("overwrite")]
    [InlineData("rename")]
    [InlineData("replace")]
    [InlineData("link")]
    public async Task ProtectedLocalNativeConversionRetainsExclusiveObjectAcrossCompetitors(string action)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "original unuploaded local bytes"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        byte[] originalDacl = new FileInfo(fixture.File.FullPath).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        string target = Path.Combine(fixture.Root, "competitor.bin");
        if (action == "replace")
        {
            await File.WriteAllTextAsync(target, "replacement");
        }

        using SafeFileHandle access = NativeLocalAccessOpen(fixture.File.FullPath, 0x00060083, 0);
        Assert.False(access.IsInvalid, $"Access open error={Marshal.GetLastPInvokeError()}");
        using LocalCompetitor competitor = StartLocalCompetitor(fixture.File.FullPath, target, action);
        try
        {
            CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "local-unuploaded");
            long usn = NativeLocalConvert(access.DangerousGetHandle(), identity.Encode());
            Assert.Equal(binding, CloudLocalFileBindingPlatform.Read(access.DangerousGetHandle(), fixture.Root));
            LocalAccessSecurity descriptor = new(access);
            Assert.Equal(originalDacl, descriptor.GetSecurityDescriptorBinaryForm());
            descriptor.Apply(access);

            await competitor.StartAsync();
            // Require the independent kernel operation to finish while the original
            // exclusive object remains open. Absence of progress is not protection proof.
            await competitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(competitor.ExitCode is 32 or 33 or 396 || action == "replace" && competitor.ExitCode == 5,
                $"Competitor completed inside protection: {competitor.ExitCode}");

            CloudItemSnapshot held = await fixture.File.InspectAsync();
            Assert.Equal(binding, held.LocalBinding);
            Assert.True(held.IsPlaceholder);
            Assert.Equal(content.Length, held.Length);
            Assert.Equal(CloudSynchronizationState.NotInSync, held.SynchronizationState);
            Assert.Equal(identity.Encode(), held.PlaceholderIdentity.ToArray());
            Assert.Equal(originalDacl, new LocalAccessSecurity(access).GetSecurityDescriptorBinaryForm());
            Assert.Equal(0, fixture.Provider.Fetches);
            output.WriteLine($"OS={Environment.OSVersion.Version}; Architecture={RuntimeInformation.ProcessArchitecture}; Action={action}; ConvertUsn={usn}; HeldExit={(competitor.HasExited ? competitor.ExitCode : null)}");

            access.Dispose();
            await competitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (action == "link")
            {
                Assert.Equal(396, competitor.ExitCode);
                Assert.False(File.Exists(target));
                Assert.Equal(content, await File.ReadAllBytesAsync(fixture.File.FullPath));
            }
            else if (action == "replace" && competitor.ExitCode == 5)
            {
                // MoveFileEx may reject replacement of a placeholder with AccessDenied.
                // Record that policy result rather than attributing it to a sharing conflict.
                Assert.Equal(content, await File.ReadAllBytesAsync(fixture.File.FullPath));
            }
            else if (competitor.ExitCode is 32 or 33)
            {
                Assert.Equal(content, await File.ReadAllBytesAsync(fixture.File.FullPath));
                using LocalCompetitor retry = StartLocalCompetitor(fixture.File.FullPath, target, action);
                await retry.StartAsync();
                await retry.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, retry.ExitCode);
            }
            else
            {
                Assert.Equal(0, competitor.ExitCode);
            }
        }
        finally
        {
            access.Dispose();
        }
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("cold")]
    [InlineData("directory")]
    public async Task ProtectedLocalNativeAccessGuardPreservesObjectWithoutHydration(string kind)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        CloudItem item;
        if (kind == "cold")
        {
            await fixture.System.Root.CreatePlaceholderAsync(CloudFilePlaceholderSpec.CreateBuilder("content.bin", "cold-local", 4096).Build());
            item = fixture.File;
        }
        else if (kind == "directory")
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, "directory"));
            item = fixture.System.GetDirectory("directory");
        }
        else
        {
            await File.WriteAllTextAsync(fixture.File.FullPath, "local-only");
            item = fixture.File;
        }

        CloudItemSnapshot before = await item.InspectAsync();
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>(before.LocalBinding);
        using SafeFileHandle access = NativeLocalAccessOpen(item.FullPath,
            kind == "directory" ? 0x00060080u : 0x00060083u, kind == "directory" ? 3u : 0u);
        Assert.False(access.IsInvalid, $"Access open error={Marshal.GetLastPInvokeError()}");
        Assert.Equal(binding, CloudLocalFileBindingPlatform.Read(access.DangerousGetHandle(), fixture.Root));
        LocalAccessSecurity descriptor = new(access, kind == "directory");
        byte[] original = descriptor.GetSecurityDescriptorBinaryForm();
        await Task.Delay(20);
        descriptor.Apply(access);
        Assert.Equal(original, new LocalAccessSecurity(access, kind == "directory").GetSecurityDescriptorBinaryForm());
        Assert.Equal(binding, (await item.InspectAsync()).LocalBinding);
        Assert.Equal(0, fixture.Provider.Fetches);
        if (kind == "cold")
        {
            Assert.Equal(CloudContentAvailability.OnlineOnly, (await item.InspectAsync()).ContentAvailability);
        }

        output.WriteLine($"OS={Environment.OSVersion.Version}; Architecture={RuntimeInformation.ProcessArchitecture}; Kind={kind}; SourceReads={fixture.Provider.Fetches}; SameObjectAccessSucceeded=True");
    }

    [Fact]
    public async Task ProtectedLocalNativeExistingAliasesAreRejectedBeforeMutation()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "local-only"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        string alias = Path.Combine(fixture.Root, "alias.bin");
        using LocalCompetitor link = StartLocalCompetitor(fixture.File.FullPath, alias, "link");
        await link.StartAsync();
        await link.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, link.ExitCode);
        Assert.Throws<NotSupportedException>(() => new CloudProtectedContentSession(fixture.File.FullPath, fixture.Root));
        Assert.False((await fixture.File.InspectAsync()).IsPlaceholder);
        Assert.Equal(content, await File.ReadAllBytesAsync(alias));
        Assert.Equal(content, await File.ReadAllBytesAsync(fixture.File.FullPath));
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedLocalNativeExclusiveAccessRejectsExistingWriterOrMapping(bool mapped)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "local-only");
        using FileStream writer = new(fixture.File.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using MemoryMappedFile? mapping = mapped
            ? MemoryMappedFile.CreateFromFile(writer, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true) : null;
        using MemoryMappedViewAccessor? view = mapping?.CreateViewAccessor();
        if (mapped)
        {
            writer.Dispose();
        }
        using SafeFileHandle access = NativeLocalAccessOpen(fixture.File.FullPath, 0x00060083, 0);
        int error = Marshal.GetLastPInvokeError();
        Assert.True(access.IsInvalid, "Writable section or writer admitted an exclusive object handle.");
        Assert.Equal(32, error);
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    private static unsafe long NativeLocalConvert(nint handle, byte[] identity)
    {
        fixed (byte* pointer = identity)
        {
            long usn = 0;
            int result = CfApi.CfConvertToPlaceholder(handle, pointer, (uint)identity.Length, CfConvertFlags.None, &usn, null);
            Assert.True(result >= 0, $"CfConvertToPlaceholder HRESULT=0x{result:X8}");
            return usn;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle NativeLocalAccessOpen(string path, uint access = 0x00060080,
        uint share = 3, nint security = 0, uint creation = 3, uint flags = 0x02200000, nint template = 0);

    private sealed class LocalAccessSecurity : NativeObjectSecurity
    {
        internal LocalAccessSecurity(SafeFileHandle handle, bool directory = false)
            : base(directory, ResourceType.FileObject, handle, AccessControlSections.Access) { }
        internal void Apply(SafeFileHandle handle) => Persist(handle, AccessControlSections.Access);
        public override Type AccessRightType => typeof(FileSystemRights);
        public override Type AccessRuleType => typeof(FileSystemAccessRule);
        public override Type AuditRuleType => typeof(FileSystemAuditRule);
        public override AccessRule AccessRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type) =>
            new FileSystemAccessRule(identityReference, (FileSystemRights)accessMask, inheritanceFlags, propagationFlags, type);
        public override AuditRule AuditRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags) =>
            new FileSystemAuditRule(identityReference, (FileSystemRights)accessMask, inheritanceFlags, propagationFlags, flags);
    }

    private static LocalCompetitor StartLocalCompetitor(string path, string target, string action)
    {
        string name = "Local\\CfSharp-local-" + Guid.NewGuid().ToString("N");
        EventWaitHandle ready = new(false, EventResetMode.ManualReset, name);
        EventWaitHandle go = new(false, EventResetMode.ManualReset, name + "-go");
        EventWaitHandle attempted = new(false, EventResetMode.ManualReset, name + "-attempted");
        ProcessStartInfo start = new("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(GetHarnessPath());
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(name);
        start.ArgumentList.Add("protected-local-" + action);
        start.ArgumentList.Add(target);
        return new LocalCompetitor(Process.Start(start)!, ready, go, attempted);
    }

    private sealed class LocalCompetitor(Process process, EventWaitHandle ready, EventWaitHandle go, EventWaitHandle attempted) : IDisposable
    {
        internal bool HasExited => process.HasExited;
        internal int ExitCode => process.ExitCode;
        internal Task WaitForExitAsync() => process.WaitForExitAsync();
        internal async Task StartAsync()
        {
            await WaitAsync(ready);
            go.Set();
            await WaitAsync(attempted);
        }
        private async Task WaitAsync(EventWaitHandle signal)
        {
            long start = Stopwatch.GetTimestamp();
            while (!signal.WaitOne(0) && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(10))
            {
                if (process.HasExited)
                {
                    Assert.Fail($"Competitor exited early: {process.ExitCode}");
                }
                await Task.Delay(10);
            }
            Assert.True(signal.WaitOne(0));
        }
        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            process.Dispose();
            ready.Dispose();
            go.Dispose();
            attempted.Dispose();
        }
    }
}
