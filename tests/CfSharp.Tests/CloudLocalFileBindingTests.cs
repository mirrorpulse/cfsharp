namespace CfSharp.Tests;

public sealed class CloudLocalFileBindingTests
{
    [Fact]
    public void ComparesCompleteFileRootAndVolumeIdentifiers()
    {
        Guid root = Guid.NewGuid();
        Guid file = Guid.NewGuid();
        CloudLocalFileBinding binding = new(1, root, file);
        Assert.Equal(binding, new CloudLocalFileBinding(1, root, file));
        Assert.NotEqual(binding, new CloudLocalFileBinding(2, root, file));
        Assert.NotEqual(binding, new CloudLocalFileBinding(1, Guid.NewGuid(), file));
        byte[] bytes = file.ToByteArray();
        bytes[15] ^= 0xff;
        Assert.NotEqual(binding, new CloudLocalFileBinding(1, root, new Guid(bytes)));
    }

    [Fact]
    public void RejectsIncompleteBindings()
    {
        Assert.Throws<ArgumentException>(() => new CloudLocalFileBinding(0, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new CloudLocalFileBinding(1, Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new CloudLocalFileBinding(1, Guid.NewGuid(), Guid.Empty));
    }
}
