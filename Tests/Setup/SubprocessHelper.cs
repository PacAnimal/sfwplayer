using System.Diagnostics;

namespace Tests.Setup;

internal static class SubprocessHelper
{
    // How long a child is given to exit by itself once the test has what it came for.
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Lets a child that has already said its piece shut down on its own, and kills it only if it does not.
    ///
    /// <para>The test modes print DONE and then return straight out of Main, so killing on the line after
    /// the read lands in the window before the runtime's shutdown runs — and a killed .NET process leaves
    /// its <c>dotnet-diagnostic-&lt;pid&gt;-*</c> socket and <c>clr-debug-pipe-&lt;pid&gt;-*</c> pair behind
    /// in the temp dir for good. Kill stays as the backstop for a child that really is wedged.</para>
    /// </summary>
    internal static async Task WaitForCleanExit(Process proc)
    {
        try
        {
            using var grace = new CancellationTokenSource(ExitGrace);
            await proc.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            // wedged — fall through to the kill
        }
        finally
        {
            if (!proc.HasExited) proc.Kill();
        }
    }

    // AppContext.BaseDirectory = {repo}/Tests/bin/{config}/{tfm}/
    internal static string? FindSfwPlayerExe()
    {
        var testDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", ".."));
        var config = Path.GetFileName(Path.GetDirectoryName(testDir)!);
        var tfm = Path.GetFileName(testDir);
        var binDir = Path.Combine(repoRoot, "SfwPlayer", "bin", config, tfm);
        if (!Directory.Exists(binDir)) return null;
        return Directory.GetFiles(binDir, "SfwPlayer", SearchOption.AllDirectories).FirstOrDefault();
    }
}
