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
}
