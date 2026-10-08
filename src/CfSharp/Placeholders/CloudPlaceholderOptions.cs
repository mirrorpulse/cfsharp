namespace CfSharp;

/// <summary>Describes the requested local availability of a placeholder file.</summary>
public enum CloudAvailabilityTarget
{
    /// <summary>Keep metadata locally and allow all file content to be absent.</summary>
    OnlineOnly = 0,

    /// <summary>Keep the complete content locally without preventing later storage reclamation.</summary>
    LocallyAvailable = 1,

    /// <summary>Keep the complete content locally and request that Windows retain it.</summary>
    AlwaysAvailable = 2,
}

/// <summary>Describes a pin-state value that may be assigned to a placeholder.</summary>
public enum CloudPinTarget
{
    /// <summary>Clear explicit pin intent.</summary>
    Unspecified = 0,

    /// <summary>Request that content remain available locally.</summary>
    Pinned = 1,

    /// <summary>Allow Windows to reclaim local content.</summary>
    Unpinned = 2,

    /// <summary>Exclude the item from synchronization.</summary>
    Excluded = 3,

    /// <summary>Inherit pin intent from the parent directory.</summary>
    Inherit = 4,
}

/// <summary>Describes whether a placeholder directory is partial or fully represented locally.</summary>
public enum CloudDirectoryPopulationState
{
    /// <summary>Future access may request remote children through a provider callback.</summary>
    Partial = 0,

    /// <summary>All children are represented locally and no population callback is required.</summary>
    Complete = 1,
}

/// <summary>Describes whether a placeholder file may become partial after it is hydrated.</summary>
public enum CloudFileContentMode
{
    /// <summary>Allow local content to be dehydrated when policy and state permit.</summary>
    AllowPartial = 0,

    /// <summary>Require complete content after hydration and reject later dehydration.</summary>
    AlwaysFull = 1,
}

/// <summary>Selects a category of placeholder byte ranges.</summary>
public enum CloudPlaceholderRangeKind
{
    /// <summary>Return all content physically present on disk.</summary>
    OnDisk = 0,

    /// <summary>Return present content validated against provider state.</summary>
    Validated = 1,

    /// <summary>Return present content modified locally since provider validation.</summary>
    Modified = 2,
}

/// <summary>Controls collision handling for one placeholder creation entry.</summary>
public enum CloudPlaceholderCollisionBehavior
{
    /// <summary>Fail when an unrelated local item already uses the requested name.</summary>
    Fail = 0,

    /// <summary>Explicitly request that Windows supersede the existing item.</summary>
    Supersede = 1,
}

/// <summary>Controls how zero-valued native metadata fields are interpreted during an update.</summary>
public enum CloudMetadataWriteMode
{
    /// <summary>Preserve fields represented by native zero values.</summary>
    PreserveUnspecified = 0,

    /// <summary>Pass every zero value through to the file system.</summary>
    PassThrough = 1,
}

/// <summary>Controls batch-level placeholder creation behavior.</summary>
public sealed class CloudPlaceholderBatchOptions
{
    /// <summary>Gets options that attempt every entry after an individual failure.</summary>
    public static CloudPlaceholderBatchOptions Default { get; } = new(stopOnFirstFailure: false);

    /// <summary>Initializes immutable batch options.</summary>
    /// <param name="stopOnFirstFailure">Whether Windows should stop after the first failed entry.</param>
    public CloudPlaceholderBatchOptions(bool stopOnFirstFailure)
    {
        StopOnFirstFailure = stopOnFirstFailure;
    }

    /// <summary>Gets whether processing stops after the first failed entry.</summary>
    public bool StopOnFirstFailure { get; }
}

/// <summary>Controls conversion of an ordinary item into a placeholder.</summary>
public sealed class CloudPlaceholderConversionOptions
{
    private CloudPlaceholderConversionOptions(
        bool markInSync,
        bool dehydrate,
        CloudDirectoryPopulationState? populationState,
        CloudFileContentMode contentMode,
        bool forceConversion)
    {
        MarkInSync = markInSync;
        Dehydrate = dehydrate;
        PopulationState = populationState;
        ContentMode = contentMode;
        ForceConversion = forceConversion;
    }

    /// <summary>Gets default conversion options that preserve content and do not mark in sync.</summary>
    public static CloudPlaceholderConversionOptions Default { get; } = CreateBuilder().Build();

    /// <summary>Gets whether successful conversion marks the item in sync.</summary>
    public bool MarkInSync { get; }

    /// <summary>Gets whether a converted file is immediately dehydrated.</summary>
    public bool Dehydrate { get; }

    /// <summary>Gets an optional directory population state.</summary>
    public CloudDirectoryPopulationState? PopulationState { get; }

    /// <summary>Gets the requested file content mode.</summary>
    public CloudFileContentMode ContentMode { get; }

    /// <summary>Gets whether conversion from another placeholder implementation is allowed.</summary>
    public bool ForceConversion { get; }

    /// <summary>Creates a mutable conversion-options builder.</summary>
    public static Builder CreateBuilder() => new();

    /// <summary>Builds immutable conversion options.</summary>
    public sealed class Builder
    {
        private bool _markInSync;
        private bool _dehydrate;
        private CloudDirectoryPopulationState? _populationState;
        private CloudFileContentMode _contentMode;
        private bool _forceConversion;

        /// <summary>Marks the converted item in sync.</summary>
        public Builder WithInSyncState()
        {
            _markInSync = true;
            return this;
        }

        /// <summary>Requests immediate file dehydration after conversion.</summary>
        public Builder WithDehydration()
        {
            _dehydrate = true;
            return this;
        }

        /// <summary>Sets directory population state.</summary>
        public Builder WithPopulationState(CloudDirectoryPopulationState state)
        {
            _populationState = RequireDefined(state, nameof(state));
            return this;
        }

        /// <summary>Sets the converted file content mode.</summary>
        public Builder WithContentMode(CloudFileContentMode mode)
        {
            _contentMode = RequireDefined(mode, nameof(mode));
            return this;
        }

        /// <summary>Allows force-conversion from another placeholder implementation.</summary>
        public Builder WithForceConversion()
        {
            _forceConversion = true;
            return this;
        }

        /// <summary>Creates immutable conversion options.</summary>
        /// <exception cref="InvalidOperationException">
        /// Always-full content was combined with immediate dehydration.
        /// </exception>
        public CloudPlaceholderConversionOptions Build()
        {
            if (_dehydrate && _contentMode is CloudFileContentMode.AlwaysFull)
            {
                throw new InvalidOperationException(
                    "An always-full file cannot be dehydrated during conversion.");
            }

            return new CloudPlaceholderConversionOptions(
                _markInSync,
                _dehydrate,
                _populationState,
                _contentMode,
                _forceConversion);
        }
    }

    internal static T RequireDefined<T>(T value, string parameterName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value is not defined.");
        }

        return value;
    }
}

/// <summary>Controls explicit placeholder dehydration.</summary>
public sealed class CloudDehydrationOptions
{
    /// <summary>Gets foreground dehydration options.</summary>
    public static CloudDehydrationOptions Foreground { get; } = new(background: false);

    /// <summary>Gets background dehydration options.</summary>
    public static CloudDehydrationOptions Background { get; } = new(background: true);

    private CloudDehydrationOptions(bool background)
    {
        IsBackground = background;
    }

    /// <summary>Gets whether Windows should classify the operation as background work.</summary>
    public bool IsBackground { get; }
}

/// <summary>Controls a conditional in-sync state transition.</summary>
public sealed class CloudInSyncChangeOptions
{
    /// <summary>Initializes options with an optional positive expected USN.</summary>
    /// <param name="expectedUsn">Expected current USN, or null for no condition.</param>
    public CloudInSyncChangeOptions(long? expectedUsn = null)
    {
        if (expectedUsn <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedUsn),
                expectedUsn,
                "An expected USN must be positive when supplied.");
        }

        ExpectedUsn = expectedUsn;
    }

    /// <summary>Gets the expected current USN, or null for no condition.</summary>
    public long? ExpectedUsn { get; }
}

/// <summary>Controls a same-root move or rename.</summary>
public sealed class CloudMoveOptions
{
    /// <summary>Gets non-destructive move options.</summary>
    public static CloudMoveOptions Default { get; } = new(replaceExisting: false);

    /// <summary>Initializes immutable move options.</summary>
    public CloudMoveOptions(bool replaceExisting)
    {
        ReplaceExisting = replaceExisting;
    }

    /// <summary>Initializes a directory move or recovery with its original durable pre-move proof.</summary>
    /// <param name="directoryMoveProof">Owned evidence for this exact source and intended destination.</param>
    /// <exception cref="ArgumentNullException">The proof is null.</exception>
    /// <remarks>
    /// Does not permit replacement. The immutable value owns no native protection; the move
    /// authenticates its original store record and verifies the actual object. Use only for
    /// directories; files retain the boolean constructor and existing replacement behavior.
    /// </remarks>
    public CloudMoveOptions(CloudDirectoryMoveProof directoryMoveProof)
    {
        ArgumentNullException.ThrowIfNull(directoryMoveProof);
        DirectoryMoveProof = directoryMoveProof;
    }

    /// <summary>Gets whether an existing destination file may be replaced.</summary>
    public bool ReplaceExisting { get; }

    /// <summary>Gets optional durable directory move evidence, or null for normal automatic preparation.</summary>
    public CloudDirectoryMoveProof? DirectoryMoveProof { get; }
}

/// <summary>Controls explicit recursive execution over materialized local entries.</summary>
public sealed class CloudRecursiveOperationOptions
{
    /// <summary>Gets options that include the root and continue after entry failures.</summary>
    public static CloudRecursiveOperationOptions Default { get; } =
        new(includeRoot: true, stopOnFirstFailure: false);

    /// <summary>Initializes immutable recursive options.</summary>
    public CloudRecursiveOperationOptions(bool includeRoot, bool stopOnFirstFailure)
    {
        IncludeRoot = includeRoot;
        StopOnFirstFailure = stopOnFirstFailure;
    }

    /// <summary>Gets whether the receiving directory itself is included.</summary>
    public bool IncludeRoot { get; }

    /// <summary>Gets whether execution stops after the first failed entry.</summary>
    public bool StopOnFirstFailure { get; }
}
