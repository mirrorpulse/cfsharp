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

    [Fact]
    public void AShortBudgetPreservesPreparationAndRejectsUnboundedRequests()
    {
        CloudLocalFileBinding binding = new(1, Guid.NewGuid(), Guid.NewGuid());
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("local-budget");
        CloudProtectedLocalOperationRequest original = CloudProtectedLocalOperationRequest.ForLocalConversion(binding, identity);
        CloudProtectedLocalOperationRequest bounded = original.WithBudget(TimeSpan.FromSeconds(1));
        Assert.Same(binding, bounded.ExpectedBinding);
        Assert.Same(identity, bounded.PreparationIdentity);
        Assert.Equal(original.Mode, bounded.Mode);
        Assert.Equal(TimeSpan.FromSeconds(1), bounded.Budget);
        Assert.Equal(TimeSpan.FromSeconds(10), original.Budget);
        Assert.Throws<ArgumentOutOfRangeException>(() => original.WithBudget(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => original.WithBudget(Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() => original.WithBudget(TimeSpan.FromMinutes(2)));
    }
}
