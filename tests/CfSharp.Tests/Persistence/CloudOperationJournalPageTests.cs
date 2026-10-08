namespace CfSharp.Tests.Persistence;

public sealed class CloudOperationJournalPageTests
{
    [Fact]
    public void PageOwnsCollectionAndRejectsSkippedContinuation()
    {
        CloudOperationJournalEntry first = CreateEntry(1);
        CloudOperationJournalEntry second = CreateEntry(3);
        CloudOperationJournalEntry[] input = [first, second];
        CloudOperationJournalPage page = new(input, 3, true);
        input[0] = second;
        Assert.Same(first, page.Operations[0]);
        Assert.Throws<ArgumentException>(() => new CloudOperationJournalPage([second, first], 1, false));
        Assert.Throws<ArgumentException>(() => new CloudOperationJournalPage([first, first], 1, false));
        Assert.Throws<ArgumentException>(() => new CloudOperationJournalPage([first], 2, true));
        Assert.Throws<ArgumentException>(() => new CloudOperationJournalPage([], 3, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloudOperationJournalPage([], -1, false));
    }

    private static CloudOperationJournalEntry CreateEntry(long sequence) =>
        new(Guid.NewGuid(), CloudStateOperationKind.Create, null, [1], DateTimeOffset.UtcNow, sequence: sequence);
}
