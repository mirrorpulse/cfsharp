using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CfSharp.Native.Tests;

public sealed class AbiProbeComparisonTests
{
    private const string ProbePathEnvironmentVariable = "CFSHARP_ABI_PROBE_JSON";

    [Fact]
    [Trait("Category", "AbiProbe")]
    public void ManagedLayoutsAndConstantsMatchNativeProbe()
    {
        string? probePath = Environment.GetEnvironmentVariable(ProbePathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(probePath))
        {
            throw new InvalidOperationException(
                $"{ProbePathEnvironmentVariable} is required for the ABI comparison test. " +
                "Run the native ABI probe job or explicitly exclude Category=AbiProbe.");
        }

        if (!File.Exists(probePath))
        {
            throw new FileNotFoundException(
                $"The native ABI probe JSON was not found at '{probePath}'.",
                probePath);
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(probePath));
        JsonElement probe = document.RootElement;

        AssertProbe(probe, "pointerSize", IntPtr.Size);
        AssertProbe(probe, "readFileUsnDataControlCode", WindowsFileUsn.ReadFileUsnDataControlCode);
        AssertProbe(probe, "overlappedSize", Marshal.SizeOf<OverlappedRead.Overlapped>());
        AssertOffset<OverlappedRead.Overlapped>(probe, "overlappedOffsetOffset", "Offset");
        AssertOffset<OverlappedRead.Overlapped>(probe, "overlappedEventOffset", "Event");
        AssertProbe(probe, "fileIdInfoSize", Marshal.SizeOf<WindowsFileMetadata.FileIdentity>());
        AssertOffset<WindowsFileMetadata.FileIdentity>(probe, "fileIdInfoIdOffset", "FileId");
        AssertProbe(probe, "fileStandardInfoSize", Marshal.SizeOf<WindowsFileMetadata.FileStandardInfo>());
        AssertOffset<WindowsFileMetadata.FileStandardInfo>(probe, "fileStandardInfoLengthOffset", "EndOfFile");
        AssertOffset<WindowsFileMetadata.FileStandardInfo>(probe, "fileStandardInfoLinksOffset", "NumberOfLinks");
        AssertOffset<WindowsFileMetadata.FileStandardInfo>(probe, "fileStandardInfoDeleteOffset", "DeletePending");
        AssertOffset<WindowsFileMetadata.FileStandardInfo>(probe, "fileStandardInfoDirectoryOffset", "Directory");
        AssertProbe(probe, "fileAttributeTagInfoSize", Marshal.SizeOf<WindowsFileMetadata.FileAttributeTagInfo>());
        AssertProbe(probe, "readFileUsnDataSize", Marshal.SizeOf<WindowsFileUsn.ReadFileUsnData>());
        AssertOffset<WindowsFileUsn.ReadFileUsnData>(probe, "readFileUsnDataMaxMajorVersionOffset", "MaxMajorVersion");
        AssertProbe(probe, "usnRecordV2UsnOffset", WindowsFileUsn.Version2UsnOffset);
        AssertProbe(probe, "usnRecordV3UsnOffset", WindowsFileUsn.Version3UsnOffset);
        AssertProbe(probe, "usnRecordV2HeaderLength", WindowsFileUsn.Version2HeaderLength);
        AssertProbe(probe, "usnRecordV3HeaderLength", WindowsFileUsn.Version3HeaderLength);
        AssertProbe(probe, "cfEndOfFile", CfApi.EndOfFile);
        AssertProbe(probe, "cfDefaultRequestKey", CfApi.DefaultRequestKey);
        AssertProbe(probe, "cfMaxFileIdentityLength", CfApi.MaxFileIdentityLength);
        AssertProbe(probe, "cfMaxPriorityHint", CfApi.MaxPriorityHint);
        AssertProbe(probe, "cfMaxProviderNameLength", CfApi.MaxProviderNameLength);
        AssertProbe(probe, "cfMaxProviderVersionLength", CfApi.MaxProviderVersionLength);
        AssertProbe(probe, "cfCallbackRegistrationEndType", (int)CfCallbackRegistration.End.Type);
        AssertProbe(probe, "cfCallbackRegistrationEndCallbackNull", 1);
        AssertProbe(probe, "cfPlatformInfoSize", Marshal.SizeOf<CfPlatformInfo>());
        AssertOffset<CfPlatformInfo>(probe, "cfPlatformInfoBuildNumberOffset", nameof(CfPlatformInfo.BuildNumber));
        AssertOffset<CfPlatformInfo>(probe, "cfPlatformInfoRevisionNumberOffset", nameof(CfPlatformInfo.RevisionNumber));
        AssertOffset<CfPlatformInfo>(probe, "cfPlatformInfoIntegrationNumberOffset", nameof(CfPlatformInfo.IntegrationNumber));
        AssertProbe(probe, "cfHydrationPolicySize", Marshal.SizeOf<CfHydrationPolicy>());
        AssertProbe(probe, "cfPopulationPolicySize", Marshal.SizeOf<CfPopulationPolicy>());
        AssertProbe(probe, "cfSyncPoliciesSize", Marshal.SizeOf<CfSyncPolicies>());
        AssertOffset<CfSyncPolicies>(probe, "cfSyncPoliciesHydrationOffset", nameof(CfSyncPolicies.Hydration));
        AssertOffset<CfSyncPolicies>(probe, "cfSyncPoliciesPopulationOffset", nameof(CfSyncPolicies.Population));
        AssertOffset<CfSyncPolicies>(probe, "cfSyncPoliciesInSyncOffset", nameof(CfSyncPolicies.InSync));
        AssertOffset<CfSyncPolicies>(probe, "cfSyncPoliciesHardLinkOffset", nameof(CfSyncPolicies.HardLink));
        AssertOffset<CfSyncPolicies>(probe, "cfSyncPoliciesPlaceholderManagementOffset", nameof(CfSyncPolicies.PlaceholderManagement));
        AssertProbe(probe, "cfSyncRegistrationSize", Marshal.SizeOf<CfSyncRegistration>());
        AssertOffset<CfSyncRegistration>(probe, "cfSyncRegistrationProviderNameOffset", nameof(CfSyncRegistration.ProviderName));
        AssertOffset<CfSyncRegistration>(probe, "cfSyncRegistrationProviderVersionOffset", nameof(CfSyncRegistration.ProviderVersion));
        AssertOffset<CfSyncRegistration>(probe, "cfSyncRegistrationSyncRootIdentityOffset", nameof(CfSyncRegistration.SyncRootIdentity));
        AssertOffset<CfSyncRegistration>(probe, "cfSyncRegistrationSyncRootIdentityLengthOffset", nameof(CfSyncRegistration.SyncRootIdentityLength));
        AssertOffset<CfSyncRegistration>(probe, "cfSyncRegistrationFileIdentityOffset", nameof(CfSyncRegistration.FileIdentity));
        AssertOffset<CfSyncRegistration>(probe, "cfSyncRegistrationFileIdentityLengthOffset", nameof(CfSyncRegistration.FileIdentityLength));
        AssertOffset<CfSyncRegistration>(probe, "cfSyncRegistrationProviderIdOffset", nameof(CfSyncRegistration.ProviderId));
        AssertProbe(probe, "cfSyncRootBasicInfoSize", Marshal.SizeOf<CfSyncRootBasicInfo>());
        AssertProbe(probe, "cfSyncRootProviderInfoSize", Marshal.SizeOf<CfSyncRootProviderInfo>());
        AssertOffset<CfSyncRootProviderInfo>(probe, "cfSyncRootProviderInfoProviderNameOffset", nameof(CfSyncRootProviderInfo.ProviderName));
        AssertOffset<CfSyncRootProviderInfo>(probe, "cfSyncRootProviderInfoProviderVersionOffset", nameof(CfSyncRootProviderInfo.ProviderVersion));
        AssertProbe(probe, "cfSyncRootStandardInfoSize", Marshal.SizeOf<CfSyncRootStandardInfo>());
        AssertOffset<CfSyncRootStandardInfo>(probe, "cfSyncRootStandardInfoProviderNameOffset", nameof(CfSyncRootStandardInfo.ProviderName));
        AssertOffset<CfSyncRootStandardInfo>(probe, "cfSyncRootStandardInfoProviderVersionOffset", nameof(CfSyncRootStandardInfo.ProviderVersion));
        AssertOffset<CfSyncRootStandardInfo>(probe, "cfSyncRootStandardInfoIdentityLengthOffset", nameof(CfSyncRootStandardInfo.SyncRootIdentityLength));
        AssertOffset<CfSyncRootStandardInfo>(probe, "cfSyncRootStandardInfoIdentityOffset", nameof(CfSyncRootStandardInfo.SyncRootIdentity));
        AssertProbe(probe, "cfRegisterFlagMarkInSyncOnRoot", (uint)CfRegisterFlags.MarkInSyncOnRoot);
        AssertProbe(probe, "cfHydrationModifierAllowFullRestart", (ushort)CfHydrationPolicyModifier.AllowFullRestartHydration);
        AssertProbe(probe, "cfInSyncPolicyTrackAll", (uint)CfInSyncPolicy.TrackAll);
        AssertProbe(probe, "cfSyncRootInfoProvider", (int)CfSyncRootInfoClass.Provider);
        AssertProbe(probe, "cfConnectionKeySize", Marshal.SizeOf<CfConnectionKey>());
        AssertProbe(probe, "cfTransferKeySize", Marshal.SizeOf<CfTransferKey>());
        AssertProbe(probe, "cfRequestKeySize", Marshal.SizeOf<CfRequestKey>());
        AssertProbe(probe, "cfSyncStatusSize", Marshal.SizeOf<CfSyncStatus>());
        AssertOffset<CfSyncStatus>(probe, "cfSyncStatusCodeOffset", nameof(CfSyncStatus.Code));
        AssertOffset<CfSyncStatus>(probe, "cfSyncStatusDescriptionOffsetOffset", nameof(CfSyncStatus.DescriptionOffset));
        AssertOffset<CfSyncStatus>(probe, "cfSyncStatusDeviceIdOffsetOffset", nameof(CfSyncStatus.DeviceIdOffset));
        AssertProbe(probe, "cfProcessInfoSize", Marshal.SizeOf<CfProcessInfo>());
        AssertProbe(probe, "cfCallbackInfoSize", Marshal.SizeOf<CfCallbackInfo>());
        AssertOffset<CfCallbackInfo>(probe, "cfCallbackInfoConnectionKeyOffset", nameof(CfCallbackInfo.ConnectionKey));
        AssertOffset<CfCallbackInfo>(probe, "cfCallbackInfoCallbackContextOffset", nameof(CfCallbackInfo.CallbackContext));
        AssertOffset<CfCallbackInfo>(probe, "cfCallbackInfoNormalizedPathOffset", nameof(CfCallbackInfo.NormalizedPath));
        AssertOffset<CfCallbackInfo>(probe, "cfCallbackInfoTransferKeyOffset", nameof(CfCallbackInfo.TransferKey));
        AssertOffset<CfCallbackInfo>(probe, "cfCallbackInfoPriorityHintOffset", nameof(CfCallbackInfo.PriorityHint));
        AssertOffset<CfCallbackInfo>(probe, "cfCallbackInfoRequestKeyOffset", nameof(CfCallbackInfo.RequestKey));
        AssertProbe(probe, "cfCallbackParametersSize", Marshal.SizeOf<CfCallbackParameters>());
        AssertOffset<CfCallbackParameters>(probe, "cfCallbackParametersUnionOffset", nameof(CfCallbackParameters.FetchData));
        AssertProbe(probe, "cfCallbackFetchDataSize", Marshal.SizeOf<CfCallbackFetchDataParameters>());
        AssertProbe(probe, "cfCallbackRegistrationSize", Marshal.SizeOf<CfCallbackRegistration>());
        AssertOffset<CfCallbackRegistration>(probe, "cfCallbackRegistrationCallbackOffset", nameof(CfCallbackRegistration.Callback));
        AssertProbe(probe, "cfCallbackTypeNone", (int)CfCallbackType.None);
        AssertProbe(probe, "cfConnectFlagBlockSelfImplicitHydration", (uint)CfConnectFlags.BlockSelfImplicitHydration);
        AssertProbe(probe, "cfOperationInfoSize", Marshal.SizeOf<CfOperationInfo>());
        AssertOffset<CfOperationInfo>(probe, "cfOperationInfoTransferKeyOffset", nameof(CfOperationInfo.TransferKey));
        AssertOffset<CfOperationInfo>(probe, "cfOperationInfoRequestKeyOffset", nameof(CfOperationInfo.RequestKey));
        AssertProbe(probe, "cfOperationParametersSize", Marshal.SizeOf<CfOperationParameters>());
        AssertOffset<CfOperationParameters>(probe, "cfOperationParametersUnionOffset", nameof(CfOperationParameters.TransferData));
        AssertProbe(probe, "cfOperationTransferDataSize", Marshal.SizeOf<CfOperationTransferDataParameters>());
        AssertProbe(probe, "cfFsMetadataSize", Marshal.SizeOf<CfFsMetadata>());
        AssertProbe(probe, "cfPlaceholderCreateInfoSize", Marshal.SizeOf<CfPlaceholderCreateInfo>());
        AssertOffset<CfPlaceholderCreateInfo>(probe, "cfPlaceholderCreateInfoMetadataOffset", nameof(CfPlaceholderCreateInfo.FsMetadata));
        AssertOffset<CfPlaceholderCreateInfo>(probe, "cfPlaceholderCreateInfoCreateUsnOffset", nameof(CfPlaceholderCreateInfo.CreateUsn));
        AssertProbe(probe, "cfPlaceholderCreateFlagAlwaysFull", (uint)CfPlaceholderCreateFlags.AlwaysFull);
        AssertProbe(probe, "cfFileRangeSize", Marshal.SizeOf<CfFileRange>());
        AssertOffset<CfFileRange>(probe, "cfFileRangeStartingOffsetOffset", nameof(CfFileRange.StartingOffset));
        AssertOffset<CfFileRange>(probe, "cfFileRangeLengthOffset", nameof(CfFileRange.Length));
        AssertProbe(probe, "cfConvertFlagForceConvertToCloudFile", (uint)CfConvertFlags.ForceConvertToCloudFile);
        AssertProbe(probe, "cfUpdateFlagAllowPartial", (uint)CfUpdateFlags.AllowPartial);
        AssertProbe(probe, "cfDehydrateFlagBackground", (uint)CfDehydrateFlags.Background);
        AssertProbe(probe, "cfPinStateInherit", (int)CfPinState.Inherit);
        AssertProbe(probe, "cfSetPinFlagRecurseStopOnError", (uint)CfSetPinFlags.RecurseStopOnError);
        AssertProbe(probe, "cfInSyncStateInSync", (int)CfInSyncState.InSync);
        AssertProbe(probe, "cfPlaceholderBasicInfoSize", Marshal.SizeOf<CfPlaceholderBasicInfo>());
        AssertOffset<CfPlaceholderBasicInfo>(probe, "cfPlaceholderBasicInfoIdentityOffset", nameof(CfPlaceholderBasicInfo.FileIdentity));
        AssertProbe(probe, "cfPlaceholderStandardInfoSize", Marshal.SizeOf<CfPlaceholderStandardInfo>());
        AssertOffset<CfPlaceholderStandardInfo>(probe, "cfPlaceholderStandardInfoIdentityOffset", nameof(CfPlaceholderStandardInfo.FileIdentity));
        AssertProbe(probe, "cfCorrelationVectorSize", Marshal.SizeOf<CfCorrelationVector>());
        AssertOffset<CfCorrelationVector>(probe, "cfCorrelationVectorValueOffset", nameof(CfCorrelationVector.Vector));
        AssertProbe(probe, "win32FindDataWSize", Marshal.SizeOf<CfWin32FindData>());
        AssertOffset<CfWin32FindData>(probe, "win32FindDataWReparseTagOffset", nameof(CfWin32FindData.Reserved0));
        AssertOffset<CfWin32FindData>(probe, "win32FindDataWFileNameOffset", nameof(CfWin32FindData.FileName));
        AssertProbe(probe, "cfPlaceholderInfoClassStandard", (int)CfPlaceholderInfoClass.Standard);
        AssertProbe(probe, "cfPlaceholderRangeInfoModified", (int)CfPlaceholderRangeInfoClass.Modified);
        AssertProbe(probe, "cfPlaceholderStatePartiallyOnDisk", (uint)CfPlaceholderState.PartiallyOnDisk);
        AssertAllEnumValues(probe.GetProperty("enumValues"));
    }

    private static void AssertAllEnumValues(JsonElement nativeEnums)
    {
        string inventoryPath = Path.Combine(AppContext.BaseDirectory, "cfapi-coverage.json");
        using JsonDocument inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath));
        Assembly nativeAssembly = typeof(CfApi).Assembly;

        foreach (JsonElement entry in inventory.RootElement.GetProperty("symbols").EnumerateArray())
        {
            if (entry.GetProperty("kind").GetString() != "enum")
            {
                continue;
            }

            string nativeName = entry.GetProperty("nativeName").GetString()!;
            string managedName = entry.GetProperty("managedSymbol").GetString()!;
            Type managedType = nativeAssembly.GetType(managedName, throwOnError: true)!;
            FieldInfo[] managedFields = managedType
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .OrderBy(field => field.MetadataToken)
                .ToArray();
            JsonElement.ArrayEnumerator nativeValues = nativeEnums.GetProperty(nativeName).EnumerateArray();
            uint[] expectedValues = nativeValues
                .Select(value => unchecked((uint)value.GetInt64()))
                .ToArray();

            Assert.Equal(managedFields.Length, expectedValues.Length);
            for (int index = 0; index < managedFields.Length; index++)
            {
                uint managedValue = unchecked((uint)Convert.ToInt64(
                    managedFields[index].GetValue(null),
                    CultureInfo.InvariantCulture));
                Assert.True(
                    managedValue == expectedValues[index],
                    $"{nativeName}.{managedFields[index].Name}: " +
                    $"native 0x{expectedValues[index]:X8}, managed 0x{managedValue:X8}");
            }
        }
    }

    private static void AssertOffset<T>(
        JsonElement probe,
        string propertyName,
        string fieldName)
        where T : struct =>
        AssertProbe(probe, propertyName, Marshal.OffsetOf<T>(fieldName).ToInt64());

    private static void AssertProbe(JsonElement probe, string propertyName, long managedValue)
    {
        Assert.True(probe.TryGetProperty(propertyName, out JsonElement nativeValue), propertyName);
        Assert.Equal(managedValue, nativeValue.GetInt64());
    }
}
