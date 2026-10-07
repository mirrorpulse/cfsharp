using System.Runtime.Versioning;

using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed class CloudItemNamespaceOperationTests
{
    [Fact]
    [SupportedOSPlatform("windows10.0.16299")]
    public async Task MovesDeletesRecursiveStateAndLinksRemainCoordinated()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        string testPath = Path.Combine(
            Path.GetTempPath(),
            "CfSharp-namespace-operation-tests",
            Guid.NewGuid().ToString("N"));
        string rootPath = Path.Combine(testPath, "root");
        string outsidePath = Path.Combine(testPath, "outside");
        string databasePath = Path.Combine(testPath, "state", "cfsharp.db");
        byte[] content = new byte[96 * 1024];
        new Random(8192).NextBytes(content);
        Guid providerId = Guid.NewGuid();
        CloudFileSystem? fileSystem = null;
        CloudSyncRoot? root = null;
        bool registered = false;
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(outsidePath);
        string outsideSentinel = Path.Combine(outsidePath, "must-remain.txt");
        await File.WriteAllTextAsync(outsideSentinel, "outside");

        try
        {
            SyncRootRegistrationOptions registration = SyncRootRegistrationOptions
                .CreateBuilder($"CfSharp Namespace {providerId:N}", "1.0.0-test")
                .WithProviderId(providerId)
                .WithSyncRootIdentity(providerId.ToByteArray())
                .WithHydrationPolicy(CloudHydrationPolicy.Progressive)
                .WithPopulationPolicy(CloudPopulationPolicy.Partial)
                .WithRootMarkedInSync()
                .Build();
            fileSystem = CloudFileSystem.CreateBuilder(rootPath)
                .WithStateStore(new SqliteCloudStateStoreFactory(databasePath))
                .WithRegistration(registration)
                .WithContentProvider(new ContentProvider(content))
                .Build();
            await fileSystem.StartAsync();
            root = CloudSyncRoot.Open(rootPath);
            registered = true;

            CloudDirectoryPlaceholderSpec sourceSpec = CompleteDirectory("Source", "remote-source");
            CloudDirectoryPlaceholderSpec destinationSpec = CompleteDirectory(
                "Destination",
                "remote-destination");
            CloudFilePlaceholderSpec renameSpec = FileSpec("rename.bin", "remote-rename", content.Length);
            CloudFilePlaceholderSpec replaceSpec = FileSpec(
                "replace-source.bin",
                "remote-replace",
                content.Length);
            CloudDirectoryPlaceholderSpec emptySpec = CompleteDirectory("Empty", "remote-empty");
            await fileSystem.Root.CreatePlaceholdersAsync(
                [sourceSpec, destinationSpec, renameSpec, replaceSpec, emptySpec]);

            CloudDirectory source = fileSystem.GetDirectory("Source");
            CloudLocalFileBinding sourceBinding = Assert.IsType<CloudLocalFileBinding>(
                (await source.InspectAsync()).LocalBinding);
            CloudDirectory destination = fileSystem.GetDirectory("Destination");
            CloudFilePlaceholderSpec sourceFileSpec = FileSpec(
                "source-file.bin",
                "remote-source-file",
                content.Length);
            CloudDirectoryPlaceholderSpec nestedSpec = CompleteDirectory("Nested", "remote-nested");
            await source.CreatePlaceholdersAsync([sourceFileSpec, nestedSpec]);
            CloudDirectory nested = source.GetDirectory("Nested");
            CloudFilePlaceholderSpec deepFileSpec = FileSpec(
                "deep.bin",
                "remote-deep",
                content.Length);
            await nested.CreatePlaceholderAsync(deepFileSpec);

            CloudFile originalFile = fileSystem.GetFile("rename.bin");
            CloudItemMoveResult renamed = await originalFile.MoveToAsync(
                fileSystem.Root,
                "renamed.bin");
            CloudFile renamedFile = Assert.IsType<CloudFile>(renamed.Item);
            Assert.Equal(renameSpec.Identity.ItemId, renamed.Snapshot.ItemId);
            Assert.Equal(1, renamed.DurableStateEntriesUpdated);
            Assert.False((await originalFile.InspectAsync()).Exists);

            CloudItemMoveResult crossDirectory = await renamedFile.MoveToAsync(
                destination,
                "moved.bin");
            CloudFile movedFile = Assert.IsType<CloudFile>(crossDirectory.Item);
            Assert.Equal(renameSpec.Identity.ItemId, crossDirectory.Snapshot.ItemId);
            Assert.Equal(Path.Combine(destination.FullPath, "moved.bin"), movedFile.FullPath);

            string replaceTargetPath = Path.Combine(rootPath, "replace-target.bin");
            await File.WriteAllTextAsync(replaceTargetPath, "ordinary target");
            CloudItemMoveResult replaced = await fileSystem
                .GetFile("replace-source.bin")
                .MoveToAsync(
                    fileSystem.Root,
                    "replace-target.bin",
                    new CloudMoveOptions(replaceExisting: true));
            Assert.Equal(replaceSpec.Identity.ItemId, replaced.Snapshot.ItemId);
            Assert.Equal(replaceTargetPath, replaced.DestinationPath);

            CloudItemMoveResult movedDirectoryResult = await source.MoveToAsync(
                destination,
                "MovedSource");
            CloudDirectory movedDirectory = Assert.IsType<CloudDirectory>(movedDirectoryResult.Item);
            Assert.Equal(4, movedDirectoryResult.DurableStateEntriesUpdated);
            Assert.Equal(sourceSpec.Identity.ItemId, movedDirectoryResult.Snapshot.ItemId);
            Assert.Equal(sourceBinding, movedDirectoryResult.Snapshot.LocalBinding);
            CloudFile movedDeepFile = movedDirectory.GetFile(Path.Combine("Nested", "deep.bin"));
            Assert.Equal(deepFileSpec.Identity.ItemId, (await movedDeepFile.InspectAsync()).ItemId);
            Assert.False((await source.GetFile(Path.Combine("Nested", "deep.bin")).InspectAsync()).Exists);

            CloudItemDeleteResult fileDeleted = await movedFile.DeleteAsync();
            Assert.True(fileDeleted.DurableStateUpdated);
            Assert.False(fileDeleted.Snapshot.Exists);
            Assert.True(fileDeleted.Snapshot.IsTombstone);
            Assert.Equal(renameSpec.Identity.ItemId, fileDeleted.Snapshot.ItemId);

            CloudDirectory empty = fileSystem.GetDirectory("Empty");
            CloudItemDeleteResult directoryDeleted = await empty.DeleteAsync();
            Assert.True(directoryDeleted.DurableStateUpdated);
            Assert.False(directoryDeleted.Snapshot.Exists);
            Assert.True(directoryDeleted.Snapshot.IsTombstone);
            Assert.Equal(emptySpec.Identity.ItemId, directoryDeleted.Snapshot.ItemId);

            string linkPath = Path.Combine(movedDirectory.FullPath, "outside-link");
            Directory.CreateSymbolicLink(linkPath, outsidePath);
            CloudRecursiveOperationResult pinned = await movedDirectory
                .SetPinStateRecursivelyAsync(CloudPinTarget.Pinned);
            pinned.ThrowIfAnyFailed();
            Assert.Equal(4, pinned.Entries.Count);
            Assert.DoesNotContain(
                pinned.Entries,
                entry => string.Equals(entry.Path, linkPath, StringComparison.OrdinalIgnoreCase));

            CloudRecursiveOperationResult local = await movedDirectory
                .SetAvailabilityRecursivelyAsync(CloudAvailabilityTarget.LocallyAvailable);
            local.ThrowIfAnyFailed();
            Assert.Equal(4, local.Entries.Count);
            Assert.Equal(content, await File.ReadAllBytesAsync(movedDeepFile.FullPath));
            CloudRecursiveOperationResult online = await movedDirectory
                .SetAvailabilityRecursivelyAsync(CloudAvailabilityTarget.OnlineOnly);
            online.ThrowIfAnyFailed();

            CloudRecursiveOperationResult treeDeleted = await movedDirectory.DeleteTreeAsync();
            treeDeleted.ThrowIfAnyFailed();
            Assert.Equal(5, treeDeleted.Entries.Count);
            Assert.Equal(movedDirectory.FullPath, treeDeleted.Entries[^1].Path);
            Assert.Contains(
                treeDeleted.Entries,
                entry => string.Equals(entry.Path, linkPath, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(outsideSentinel));
            CloudItemSnapshot deletedDeep = await movedDeepFile.InspectAsync();
            Assert.False(deletedDeep.Exists);
            Assert.True(deletedDeep.IsTombstone);
            Assert.Equal(deepFileSpec.Identity.ItemId, deletedDeep.ItemId);

            string lockedTreePath = Path.Combine(rootPath, "LockedTree");
            Directory.CreateDirectory(lockedTreePath);
            string lockedPath = Path.Combine(lockedTreePath, "a-locked.bin");
            string removablePath = Path.Combine(lockedTreePath, "b-removable.bin");
            await File.WriteAllTextAsync(lockedPath, "locked");
            await File.WriteAllTextAsync(removablePath, "removable");
            await using (FileStream lockedStream = new(
                lockedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                CloudRecursiveOperationResult continued = await fileSystem
                    .GetDirectory("LockedTree")
                    .DeleteTreeAsync(new CloudRecursiveOperationOptions(
                        includeRoot: true,
                        stopOnFirstFailure: false));
                Assert.Equal(CloudItemOperationStatus.Failed, continued.Entries[0].Status);
                Assert.Equal(CloudItemOperationStatus.Succeeded, continued.Entries[1].Status);
                Assert.Equal(CloudItemOperationStatus.Failed, continued.Entries[2].Status);
                Assert.All(
                    continued.Entries.Where(entry => entry.Error is not null),
                    entry => Assert.Equal("CloudDirectory.DeleteTree", entry.Error!.Operation));
                Assert.False(File.Exists(removablePath));
            }

            Directory.Delete(lockedTreePath, recursive: true);

            string stoppedTreePath = Path.Combine(rootPath, "StoppedTree");
            Directory.CreateDirectory(stoppedTreePath);
            string stoppedLockedPath = Path.Combine(stoppedTreePath, "a-locked.bin");
            string stoppedLaterPath = Path.Combine(stoppedTreePath, "b-later.bin");
            await File.WriteAllTextAsync(stoppedLockedPath, "locked");
            await File.WriteAllTextAsync(stoppedLaterPath, "later");
            await using (FileStream lockedStream = new(
                stoppedLockedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                CloudRecursiveOperationResult stopped = await fileSystem
                    .GetDirectory("StoppedTree")
                    .DeleteTreeAsync(new CloudRecursiveOperationOptions(
                        includeRoot: true,
                        stopOnFirstFailure: true));
                Assert.Equal(CloudItemOperationStatus.Failed, stopped.Entries[0].Status);
                Assert.Equal(CloudItemOperationStatus.NotProcessed, stopped.Entries[1].Status);
                Assert.Equal(CloudItemOperationStatus.NotProcessed, stopped.Entries[2].Status);
                Assert.True(File.Exists(stoppedLaterPath));
            }

            Directory.Delete(stoppedTreePath, recursive: true);

            string canceledTreePath = Path.Combine(rootPath, "CanceledTree");
            Directory.CreateDirectory(canceledTreePath);
            string canceledFilePath = Path.Combine(canceledTreePath, "unchanged.bin");
            await File.WriteAllTextAsync(canceledFilePath, "unchanged");
            using (CancellationTokenSource cancellation = new())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fileSystem
                    .GetDirectory("CanceledTree")
                    .DeleteTreeAsync(cancellationToken: cancellation.Token)
                    .AsTask());
            }

            Assert.True(File.Exists(canceledFilePath));
            Directory.Delete(canceledTreePath, recursive: true);
        }
        finally
        {
            if (fileSystem is not null)
            {
                try
                {
                    await fileSystem.DisposeAsync();
                }
                catch (Exception)
                {
                    // Preserve the original test failure while still cleaning persistent state.
                }
            }

            if (!registered)
            {
                try
                {
                    root = CloudSyncRoot.Open(rootPath);
                    registered = true;
                }
                catch (CloudFilesException)
                {
                    // No registration was created, or it was already removed successfully.
                }
            }

            if (registered && root is not null)
            {
                try
                {
                    root.Unregister();
                }
                catch (CloudFilesException)
                {
                    // Preserve the original test failure while still attempting system cleanup.
                }
            }

            if (Directory.Exists(testPath))
            {
                try
                {
                    Directory.Delete(testPath, recursive: true);
                }
                catch (IOException)
                {
                    // A preceding cleanup failure is more useful than a secondary lock error.
                }
            }
        }
    }

    private static CloudDirectoryPlaceholderSpec CompleteDirectory(string name, string remoteId) =>
        CloudDirectoryPlaceholderSpec.CreateBuilder(name, remoteId)
            .WithPopulationState(CloudDirectoryPopulationState.Complete)
            .Build();

    private static CloudFilePlaceholderSpec FileSpec(string name, string remoteId, long length) =>
        CloudFilePlaceholderSpec.CreateBuilder(name, remoteId, length).Build();

    private sealed class ContentProvider : ICloudFileContentProvider
    {
        private readonly byte[] _content;

        internal ContentProvider(byte[] content)
        {
            _content = content;
        }

        public ValueTask<Stream> OpenReadAsync(
            CloudFileFetchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(_content, writable: false));
        }
    }
}
