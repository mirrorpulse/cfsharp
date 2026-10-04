using System.Diagnostics;

namespace CfSharp.Tests;

public sealed class DocumentationSourceLinkTests
{
    [Fact]
    public async Task SourceLinkRewriteCoversRemoteCaseNestedBranchesAndDetachedCommits()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CfSharp.sln")))
        {
            repository = repository.Parent;
        }

        Assert.NotNull(repository);
        ProcessStartInfo start = new("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(repository!.FullName, "eng", "docs", "verify-source-links.ps1"));
        using Process child = Process.Start(start)!;
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(child.ExitCode == 0, await stdout + await stderr);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
    }
}
