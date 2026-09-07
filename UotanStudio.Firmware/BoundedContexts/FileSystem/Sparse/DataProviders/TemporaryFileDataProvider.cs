namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;

internal sealed class TemporaryFileDataProvider(string filePath, long length) : FileDataProvider(filePath, 0, length)
{
    private readonly string _filePath = filePath;
    private bool _disposed;

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            File.Delete(_filePath);
        }
        catch (FileNotFoundException)
        {
        }
    }
}
