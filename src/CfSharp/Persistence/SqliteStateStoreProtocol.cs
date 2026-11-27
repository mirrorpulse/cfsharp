namespace CfSharp;

// The optional provider implements this in the new assembly. Legacy binaries can
// still satisfy ICloudStateStore, but their on-disk schema permits unsafe downgrade.
internal interface ISqliteStateStoreProtocol
{
    int SchemaVersion { get; }
}

internal static class SqliteStateStoreProtocol
{
    private const int MinimumSchemaVersion = 11;

    internal static void EnsureCompatible(object provider)
    {
        if (provider.GetType().Assembly.GetName().Name == "CfSharp.Storage.Sqlite" &&
            provider is not ISqliteStateStoreProtocol { SchemaVersion: >= MinimumSchemaVersion })
        {
            throw new NotSupportedException(
                $"This CfSharp version requires CfSharp.Storage.Sqlite with schema {MinimumSchemaVersion} or later. Upgrade both packages together.");
        }
    }
}
