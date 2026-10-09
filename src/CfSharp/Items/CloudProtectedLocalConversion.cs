using CfSharp.Native;

namespace CfSharp;

public sealed partial class CloudProtectedLocalOperationRequest
{
    /// <summary>Requests local identity preparation and official projection before invoking the callback.</summary>
    /// <param name="expectedBinding">Original binding recorded before preparation.</param>
    /// <param name="identity">Local identity without an accepted remote revision; must match any known official item identity.</param>
    /// <returns>An exclusive file request that prepares or recovers the exact native identity before callback entry.</returns>
    /// <remarks>
    /// Requires the actual root to disallow placeholder hardlinks. Existing aliases are rejected
    /// before conversion or callback. Bytes and DACL are preserved; no mark, dehydration, ACK or
    /// accepted revision is manufactured. Retry uses this same binding and exact identity after
    /// native success with failed projection; a replacement cannot inherit the preparation.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The identity contains an accepted remote revision.</exception>
    public static CloudProtectedLocalOperationRequest ForLocalConversion(CloudLocalFileBinding expectedBinding,
        CloudPlaceholderIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.RemoteRevision is not null)
        {
            throw new ArgumentException("Local preparation cannot introduce an accepted remote revision.", nameof(identity));
        }
        return new(expectedBinding) { PreparationIdentity = identity };
    }

    /// <summary>Gets local preparation required before the callback, or null for inspection-only admission.</summary>
    public CloudPlaceholderIdentity? PreparationIdentity { get; private init; }
}

public sealed partial class CloudProtectedLocalOperationContext
{
    internal bool NativeIdentityPrepared { get; private set; }
    internal bool NativeConverted { get; private set; }
    internal bool DurableProjectionCommitted { get; private set; }
    internal int? PreparationHResult { get; private set; }
    internal long? PreparationUsn { get; private set; }
    private CloudPlaceholderIdentity? _preparedIdentity;
    private CloudItemState? _lastDurableState;

    /// <summary>Prepares a local placeholder identity using the original protected file object and official store.</summary>
    /// <param name="identity">Local identity without a remotely accepted revision.</param>
    /// <param name="cancellationToken">Checked before native mutation; required post-native projection completes without cancellation.</param>
    /// <returns>A native USN and fresh same-object snapshot after successful projection.</returns>
    /// <remarks>
    /// Uses content-preserving conversion defaults and the actual disallowed-hardlink root policy.
    /// An existing placeholder must already carry the exact identity; this does not replace it.
    /// Known official identity, pending journal rows, acknowledged revisions and provenance are
    /// preserved. No callback-wide SQLite transaction is retained. Native success with failed
    /// projection remains visible in the enclosing receipt; recover by starting a new request
    /// with the original binding and identical preparation. Native success never proves upload
    /// acceptance. Scope methods serialize, reject scope exit, and drain before native release.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The identity is null.</exception>
    /// <exception cref="ArgumentException">An accepted revision was supplied.</exception>
    /// <exception cref="ObjectDisposedException">The scope has exited.</exception>
    /// <exception cref="OperationCanceledException">Cancellation occurred before native preparation.</exception>
    /// <exception cref="IOException">Native facts or durable projection cannot be verified.</exception>
    public ValueTask<CloudPlaceholderMutationResult> ConvertToPlaceholderAsync(CloudPlaceholderIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.RemoteRevision is not null)
        {
            throw new ArgumentException("Local preparation cannot introduce an accepted remote revision.", nameof(identity));
        }
        return ExecuteAsync(token => PrepareIdentityCoreAsync(identity, token), cancellationToken);
    }

    internal ValueTask<CloudPlaceholderMutationResult> ConvertItemAsync(CloudItem item, CloudPlaceholderIdentity identity,
        CloudPlaceholderConversionOptions options, CancellationToken token)
    {
        if (!item.IsOwnedBy(_owner) || item.Kind != _item.Kind ||
            !string.Equals(item.FullPath, _item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Conversion is limited to this scope's original object.");
        }
        if (options.MarkInSync || options.Dehydrate || options.ForceConversion ||
            options.PopulationState is not null || options.ContentMode != CloudPlaceholderConversionOptions.Default.ContentMode)
        {
            throw new NotSupportedException("Protected local conversion supports only content-preserving local preparation.");
        }
        return ConvertToPlaceholderAsync(identity, token);
    }

    private async ValueTask<CloudPlaceholderMutationResult> PrepareIdentityCoreAsync(CloudPlaceholderIdentity identity,
        CancellationToken token)
    {
        SetStage(CloudProtectedLocalOperationStage.NativePreparation);
        if (_item.Kind != CloudItemKind.File || _preparedIdentity is not null && !_preparedIdentity.Equals(identity))
        {
            throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.Unsupported,
                "Prepare one local file identity per scope; directory conversion and identity replacement are unavailable.");
        }
        NativeFileMetadata before = ValidateObject();
        RequireHardlinkPolicy();
        await CloudFileSystem.CheckProtectedLocalIdentityAsync(_store, _item, identity, token).ConfigureAwait(false);
        byte[] encoded = identity.Encode();
        bool placeholder = before.PlaceholderState.HasFlag(CfPlaceholderState.Placeholder);
        if (placeholder && !before.PlaceholderIdentity.AsSpan().SequenceEqual(encoded))
        {
            throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.NotApplicable,
                "An existing placeholder must carry the exact original local identity.");
        }
        token.ThrowIfCancellationRequested();
        if (!placeholder)
        {
            PreparationUsn = CloudPlaceholderMutationPlatform.Convert(_handle.DangerousGetHandle(), _item.FullPath,
                encoded, CloudPlaceholderConversionOptions.Default);
            PreparationHResult = 0;
            NativeConverted = true;
        }
        VerifyPreparedIdentity();
        _preparedIdentity = identity;
        NativeIdentityPrepared = true;
        // Capture actual native success even if the following database transaction fails.
        LastSnapshot = CloudFileSystem.CreateSnapshot(_item,
            CloudItemInspector.Inspect(_handle, _item.FullPath, _item.Kind, _item.SyncRootPath), _lastDurableState);
        SetStage(CloudProtectedLocalOperationStage.Projection);
        await CloudFileSystem.PersistProtectedLocalIdentityAsync(_store, _item, identity,
            LastSnapshot.LocalFileId, VerifyPreparedIdentity, () => DurableProjectionCommitted = true).ConfigureAwait(false);
        VerifyPreparedIdentity();
        CloudItemSnapshot after = await InspectCoreAsync(CancellationToken.None).ConfigureAwait(false);
        SetStage(CloudProtectedLocalOperationStage.Projection);
        return new(_item.FullPath, PreparationUsn, after, durableStateUpdated: true);

        void VerifyPreparedIdentity()
        {
            NativeFileMetadata current = ValidateObject();
            RequireHardlinkPolicy();
            if (!current.PlaceholderState.HasFlag(CfPlaceholderState.Placeholder) ||
                !current.PlaceholderIdentity.AsSpan().SequenceEqual(encoded))
            {
                throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.ProtectionLost,
                    "The protected native preparation no longer matches the original local identity.");
            }
        }
    }

    private void RequireHardlinkPolicy()
    {
        int result = NativeSyncRoot.Query(_handle.DangerousGetHandle(), out NativeSyncRootInfo? actual);
        if (result < 0)
        {
            throw new NativeFileException("CfGetSyncRootInfoByHandle", result);
        }
        if (actual is null || actual.HardLinkPolicy != CfHardLinkPolicy.None)
        {
            throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.Unsupported,
                "Placeholder alias protection requires the actual disallowed-hardlink registration policy.");
        }
    }
}
