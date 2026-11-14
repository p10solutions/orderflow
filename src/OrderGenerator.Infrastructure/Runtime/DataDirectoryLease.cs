namespace OrderGenerator.Infrastructure.Runtime;

/// <summary>Process lifetime lease. Never delete the lock file: another process may hold its inode.</summary>
public sealed class DataDirectoryLease : IDisposable
{
    private readonly FileStream lease;
    public string DirectoryPath
    {
        get;
    }

    public DataDirectoryLease(string path)
    {
        DirectoryPath = Path.GetFullPath(path);
        Directory.CreateDirectory(DirectoryPath);
        lease = new FileStream(
            Path.Combine(DirectoryPath, "instance.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    public void Dispose() => lease.Dispose();
}
