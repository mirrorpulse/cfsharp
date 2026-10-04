namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationAndRestartReplayPreserveJournalEchoAndRescanObligations(bool failProjection)
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        DateTimeOffset created = DateTimeOffset.UtcNow;
        CloudOperationJournalEntry operation;
        CloudEchoSuppressionState echo = new(Guid.NewGuid(), proof.AcceptedIdentity.ItemId,
            CloudStateOperationKind.ContentUpdate, fixture.File.RelativePath,
            System.Text.Encoding.UTF8.GetBytes("local-change-feed/v1"),
            created.AddDays(1), remainingObservations: 3);
        CloudStateCheckpoint checkpoint = new(CloudLocalChangeFeed.CheckpointName,
            new LocalChangeCheckpoint(41, RequiresFullRescan: true).Encode(), created);
        await using (ICloudStateTransaction transaction = await factory!.Store.BeginTransactionAsync())
        {
            await transaction.Items.UpsertAsync(new(proof.AcceptedIdentity.ItemId, proof.AcceptedIdentity.RemoteId,
                fixture.File.RelativePath, CloudItemKind.File, "revision-1", localFileId: null,
                isTombstone: false, created));
            operation = await transaction.Operations.EnqueueAsync(new(Guid.NewGuid(),
                CloudStateOperationKind.ContentUpdate, proof.AcceptedIdentity.ItemId,
                new LocalChangePayload(fixture.File.RelativePath, null, false, created).Encode(),
                created, attemptCount: 2, retryAfter: created.AddHours(1)));
            await transaction.EchoSuppressions.UpsertAsync(echo);
            await transaction.Checkpoints.UpsertAsync(checkpoint);
            await transaction.CommitAsync();
        }

        await AssertObligationsAsync();
        if (failProjection)
        {
            factory!.NextCommitFault = new ProjectionFault();
        }

        CloudContentConfirmationResult initial = await fixture.File.ConfirmUploadedContentAsync(proof);
        Assert.Equal(failProjection ? CloudContentConfirmationOutcome.NativeAppliedProjectionPending :
            CloudContentConfirmationOutcome.Confirmed, initial.Outcome);
        Assert.True(initial.NativeApplied);
        Assert.Equal(!failProjection, initial.DurableProjectionCommitted);
        await AssertObligationsAsync();
        await fixture.RestartAsync();
        await AssertObligationsAsync();
        CloudContentConfirmationResult replay = await fixture.File.ConfirmUploadedContentAsync(proof);
        Assert.Equal(CloudContentConfirmationOutcome.AlreadyConfirmed, replay.Outcome);
        Assert.True(replay.DurableProjectionCommitted);
        await AssertObligationsAsync();
        Assert.Equal(0, fixture.Provider.Fetches);

        async Task AssertObligationsAsync()
        {
            await using ICloudStateTransaction transaction = await factory!.Store.BeginTransactionAsync();
            CloudOperationJournalEntry pending = Assert.Single(await transaction.Operations.ListAsync(10));
            Assert.Equal(operation.OperationId, pending.OperationId);
            Assert.Equal(operation.ItemId, pending.ItemId);
            Assert.Equal(operation.Kind, pending.Kind);
            Assert.Equal(operation.Sequence, pending.Sequence);
            Assert.Equal(operation.Payload.ToArray(), pending.Payload.ToArray());
            Assert.Equal(operation.CreatedAt, pending.CreatedAt);
            Assert.Equal(operation.AttemptCount, pending.AttemptCount);
            Assert.Equal(operation.RetryAfter, pending.RetryAfter);
            CloudEchoSuppressionState retained = Assert.Single(await transaction.EchoSuppressions.ListActiveAsync(created));
            Assert.Equal(echo.SuppressionId, retained.SuppressionId);
            Assert.Equal(echo.ItemId, retained.ItemId);
            Assert.Equal(echo.Kind, retained.Kind);
            Assert.Equal(echo.RelativePath, retained.RelativePath);
            Assert.Equal(echo.Payload.ToArray(), retained.Payload.ToArray());
            Assert.Equal(echo.RemainingObservations, retained.RemainingObservations);
            Assert.Equal(echo.ExpiresAt, retained.ExpiresAt);
            CloudStateCheckpoint rescan = Assert.IsType<CloudStateCheckpoint>(
                await transaction.Checkpoints.GetAsync(checkpoint.Name));
            Assert.Equal(checkpoint.Value.ToArray(), rescan.Value.ToArray());
            Assert.Equal(checkpoint.UpdatedAt, rescan.UpdatedAt);
            Assert.True(LocalChangeCheckpoint.Decode(rescan.Value).RequiresFullRescan);
        }
    }
}
