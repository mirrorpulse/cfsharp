namespace CfSharp.Tests;

public sealed class ProtectedLocalOperationRequestTests
{
    [Fact]
    public void RequestRequiresAnOriginalBindingAndDefinedMode()
    {
        CloudLocalFileBinding binding = new(1, Guid.NewGuid(), Guid.NewGuid());
        CloudProtectedLocalOperationRequest request = new(binding);
        Assert.Same(binding, request.ExpectedBinding);
        Assert.Equal(CloudProtectedLocalOperationMode.ExclusiveFile, request.Mode);
        Assert.Throws<ArgumentNullException>(() => new CloudProtectedLocalOperationRequest(null!));
        Assert.Throws<ArgumentException>(() => new CloudProtectedLocalOperationRequest(binding, (CloudProtectedLocalOperationMode)999));
    }

    [Fact]
    public void LocalPreparationDoesNotAcceptAnUploadedRevision()
    {
        CloudLocalFileBinding binding = new(1, Guid.NewGuid(), Guid.NewGuid());
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("local-pending");
        CloudProtectedLocalOperationRequest request = CloudProtectedLocalOperationRequest.ForLocalConversion(binding, identity);
        Assert.Same(identity, request.PreparationIdentity);
        Assert.Equal(CloudProtectedLocalOperationMode.ExclusiveFile, request.Mode);
        Assert.Throws<ArgumentNullException>(() => CloudProtectedLocalOperationRequest.ForLocalConversion(binding, null!));
        Assert.Throws<ArgumentException>(() => CloudProtectedLocalOperationRequest.ForLocalConversion(binding,
            CloudPlaceholderIdentity.Create("local-pending", "uploaded-revision")));
    }
}
