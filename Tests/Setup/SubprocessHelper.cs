namespace Tests.Setup;

internal static class SubprocessHelper
{
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
