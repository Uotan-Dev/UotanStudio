namespace UotanStudio.Services.Firmware;

/// <summary>Restores existing extraction contents on failure.<para>解包失败时恢复既有目录内容。</para></summary>
internal static class ExtractionDirectory
{
    internal static async Task<FirmwareOperationResult> ReplaceAsync(string target, Func<Task<FirmwareOperationResult>> extract)
    {
        var backupRoot = Path.Combine(Path.GetDirectoryName(target)!, ".deleted");
        var backup = Path.Combine(backupRoot, "extract-" + Guid.NewGuid().ToString("N"));
        var hadOriginal = Directory.Exists(target);
        if (hadOriginal)
        {
            Directory.CreateDirectory(backupRoot);
            Directory.Move(target, backup);
        }
        var success = false;
        try
        {
            Directory.CreateDirectory(target);
            var result = await extract();
            success = result.Success;
            return result;
        }
        finally
        {
            await Task.Run(() =>
            {
                if (!success)
                {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    if (hadOriginal) Directory.Move(backup, target);
                }
                else if (hadOriginal) Directory.Delete(backup, true);
            }).ConfigureAwait(false);
        }
    }
}