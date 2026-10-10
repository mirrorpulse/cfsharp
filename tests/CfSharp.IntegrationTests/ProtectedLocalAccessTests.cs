using System.Security.AccessControl;
using System.Security.Principal;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Theory]
    [InlineData("ordinary")]
    [InlineData("cold")]
    [InlineData("directory")]
    [InlineData("managed-directory")]
    public async Task ProtectedLocalAccessUsesOnlyOriginalDaclWithoutHydration(string kind)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        CloudItem item;
        CloudPlaceholderIdentity identity;
        if (kind == "cold")
        {
            await fixture.System.Root.CreatePlaceholderAsync(CloudFilePlaceholderSpec.CreateBuilder("content.bin", "cold-local", 4096).Build());
            item = fixture.File;
            identity = CloudPlaceholderIdentity.Decode((await item.InspectAsync()).PlaceholderIdentity.Span);
        }
        else if (kind is "directory" or "managed-directory")
        {
            if (kind == "managed-directory")
            {
                await fixture.System.Root.CreatePlaceholderAsync(CloudDirectoryPlaceholderSpec.CreateBuilder("directory", "cold-directory").Build());
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(fixture.Root, "directory"));
            }
            item = fixture.System.GetDirectory("directory");
            identity = CloudPlaceholderIdentity.Create("unused-directory-identity");
        }
        else
        {
            await File.WriteAllTextAsync(fixture.File.FullPath, "local-original");
            item = fixture.File;
            identity = CloudPlaceholderIdentity.Create("local-access");
        }
        bool directory = item.Kind == CloudItemKind.Directory;
        FileSystemSecurity original = directory
            ? new DirectoryInfo(item.FullPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
            : new FileInfo(item.FullPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await item.InspectAsync()).LocalBinding);
        CloudProtectedLocalOperationRequest request = directory
            ? new(binding, CloudProtectedLocalOperationMode.DirectoryMetadata)
            : CloudProtectedLocalOperationRequest.ForLocalConversion(binding, identity);
        CloudProtectedLocalOperationContext? escaped = null;
        FileSystemSecurity? saved = null;
        CloudProtectedLocalOperationResult result = await item.RunProtectedLocalOperationAsync(request, async (scope, token) =>
        {
            escaped = scope;
            FileSystemSecurity access = await scope.ReadAccessDescriptorAsync(token);
            Assert.Equal(directory ? typeof(DirectorySecurity) : typeof(FileSecurity), access.GetType());
            AssertSameAccessDacl(original.GetSecurityDescriptorBinaryForm(), access.GetSecurityDescriptorBinaryForm());
            Assert.Equal(original.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(rule => rule.IsInherited),
                access.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(rule => rule.IsInherited));
            // Deliberately include unrelated sections: Access-only application must ignore them.
            access.SetOwner(new SecurityIdentifier(WellKnownSidType.NullSid, null));
            access.SetGroup(new SecurityIdentifier(WellKnownSidType.NullSid, null));
            access.AddAuditRule(new FileSystemAuditRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.WriteData, AuditFlags.Failure));
            saved = await scope.ApplyAccessDescriptorAsync(access, token);
            AssertSameAccessDacl(original.GetSecurityDescriptorBinaryForm(), saved.GetSecurityDescriptorBinaryForm());
            await Task.Yield();
            Assert.Equal(binding, (await scope.InspectAsync(token)).LocalBinding);
            if (directory)
            {
                await File.WriteAllTextAsync(Path.Combine(item.FullPath, "unfrozen-child.txt"), "membership can change", token);
                Assert.Throws<IOException>(() => Directory.Move(item.FullPath, item.FullPath + "-renamed"));
            }
        });
        Assert.True(result.Outcome == CloudProtectedLocalOperationOutcome.Completed, result.Error?.ToString());
        Assert.True(result.AccessDescriptorApplied);
        Assert.True(result.AccessDescriptorReadBack);
        FileSystemSecurity observed = directory
            ? new DirectoryInfo(item.FullPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
            : new FileInfo(item.FullPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        Assert.Equal(original.GetOwner(typeof(SecurityIdentifier)), observed.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(original.GetGroup(typeof(SecurityIdentifier)), observed.GetGroup(typeof(SecurityIdentifier)));
        AssertSameAccessDacl(original.GetSecurityDescriptorBinaryForm(), observed.GetSecurityDescriptorBinaryForm());
        AssertSameAccessDacl(original.GetSecurityDescriptorBinaryForm(), saved!.GetSecurityDescriptorBinaryForm());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => escaped!.ReadAccessDescriptorAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => escaped!.ApplyAccessDescriptorAsync(saved).AsTask());
        if (kind == "cold")
        {
            Assert.Equal(CloudContentAvailability.OnlineOnly, (await item.InspectAsync()).ContentAvailability);
        }
        else if (!directory)
        {
            Assert.Equal("local-original", await File.ReadAllTextAsync(item.FullPath));
        }
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Fact]
    public async Task ProtectedLocalAccessDoesNotApplyPermissionsBeforeRequiredAliasProtection()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "ordinary");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        byte[] original = new FileInfo(fixture.File.FullPath).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(new(binding), async (scope, token) =>
        {
            FileSystemSecurity descriptor = await scope.ReadAccessDescriptorAsync(token);
            await Assert.ThrowsAsync<ArgumentException>(() => scope.ApplyAccessDescriptorAsync(new DirectorySecurity()).AsTask());
            await scope.ApplyAccessDescriptorAsync(descriptor, token);
        });
        Assert.Equal(CloudProtectedLocalOperationOutcome.Unsupported, result.Outcome);
        Assert.False(result.AccessDescriptorApplied);
        Assert.False(result.AccessDescriptorReadBack);
        Assert.Equal(original, new FileInfo(fixture.File.FullPath).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm());
        Assert.Equal("ordinary", await File.ReadAllTextAsync(fixture.File.FullPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedLocalAccessRejectsExistingAliasesBeforeCallback(bool outsideRoot)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "all-aliases-original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        string alias = Path.Combine(outsideRoot ? Path.GetDirectoryName(fixture.Root)! : fixture.Root, "alias.bin");
        using LocalCompetitor link = StartLocalCompetitor(fixture.File.FullPath, alias, "link");
        await link.StartAsync();
        await link.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, link.ExitCode);
        byte[] original = new FileInfo(alias).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            CloudProtectedLocalOperationRequest.ForLocalConversion(binding, CloudPlaceholderIdentity.Create("aliased-local")),
            (_, _) => throw new InvalidOperationException("Aliased files must never enter a permission callback."));
        Assert.Equal(CloudProtectedLocalOperationOutcome.NotApplicable, result.Outcome);
        Assert.False(result.CallbackStarted);
        Assert.False(result.NativeConverted);
        Assert.False(result.AccessDescriptorApplied);
        Assert.Equal(original, new FileInfo(alias).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm());
        Assert.Equal("all-aliases-original", await File.ReadAllTextAsync(alias));
        Assert.Equal("all-aliases-original", await File.ReadAllTextAsync(fixture.File.FullPath));
    }

    [Fact]
    public async Task ProtectedLocalAccessRetainsAppliedFactsWhenCallbackFailsAndReleasesTheObject()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        IOException failure = new("Application policy verification failed.");
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            CloudProtectedLocalOperationRequest.ForLocalConversion(binding, CloudPlaceholderIdentity.Create("permission-pending")),
            async (scope, token) =>
            {
                await scope.ApplyAccessDescriptorAsync(await scope.ReadAccessDescriptorAsync(token), token);
                throw failure;
            });
        Assert.Equal(CloudProtectedLocalOperationOutcome.CallbackFailed, result.Outcome);
        Assert.Same(failure, result.Error);
        Assert.True(result.NativeIdentityPrepared);
        Assert.True(result.DurableProjectionCommitted);
        Assert.True(result.AccessDescriptorApplied);
        Assert.True(result.AccessDescriptorReadBack);
        Assert.True(result.Drained);
        await File.WriteAllTextAsync(fixture.File.FullPath, "editable");
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Fact]
    public async Task ProtectedLocalAccessReportsNativeAccessDenialWithoutInvokingCallback()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        FileInfo info = new(fixture.File.FullPath);
        FileSecurity original = info.GetAccessControl(AccessControlSections.Access);
        FileSecurity denied = info.GetAccessControl(AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData, AccessControlType.Deny));
        try
        {
            info.SetAccessControl(denied);
            CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(new(binding),
                (_, _) => throw new InvalidOperationException("Native denial must prevent callback admission."));
            Assert.Equal(CloudProtectedLocalOperationOutcome.Failed, result.Outcome);
            Assert.Equal(CloudProtectedLocalOperationStage.Open, result.Stage);
            Assert.Equal(5, Assert.IsType<CloudFilesException>(result.Error).Win32ErrorCode);
            Assert.False(result.CallbackStarted);
            Assert.False(result.AccessDescriptorApplied);
        }
        finally
        {
            original.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            info.SetAccessControl(original);
        }
        Assert.Equal("original", await File.ReadAllTextAsync(fixture.File.FullPath));
        Assert.Equal(0, fixture.Provider.Fetches);
    }
}
