using UotanStudio.Services.Devices;

namespace UotanStudio.ViewModels.Compare;

public sealed partial class CompareColumnViewModel
{
    /// <summary>
    /// Deletes one entry in this column's remote device directory.
    /// <para>删除本列远程设备目录中的一个条目。</para>
    /// </summary>
    /// <param name="device">Target device. <para>目标设备。</para></param>
    /// <param name="remotePath">Remote file or directory path. <para>远程文件或目录路径。</para></param>
    /// <param name="isDirectory">Whether the entry is a directory. <para>该条目是否为目录。</para></param>
    public Task DeleteDeviceEntryAsync(DetectedDevice device, string remotePath, bool isDirectory) =>
        DeviceFileSystemService.Instance.DeleteAsync(device, remotePath, isDirectory);

    /// <summary>
    /// Renames one entry in this column's remote device directory, keeping it in the same parent directory.
    /// <para>重命名本列远程设备目录中的一个条目，保持在原父目录下。</para>
    /// </summary>
    /// <param name="device">Target device. <para>目标设备。</para></param>
    /// <param name="remotePath">Current remote path. <para>当前远程路径。</para></param>
    /// <param name="newName">New entry name within the same parent. <para>同一父目录下的新条目名。</para></param>
    public Task RenameDeviceEntryAsync(DetectedDevice device, string remotePath, string newName) =>
        DeviceFileSystemService.Instance.RenameAsync(device, remotePath, RemoteChildPath(remotePath, newName));

    /// <summary>
    /// Creates a directory next to or inside this column's remote entry.
    /// <para>在本列远程条目之内或同级创建一个目录。</para>
    /// </summary>
    /// <param name="device">Target device. <para>目标设备。</para></param>
    /// <param name="remotePath">Remote entry the new folder is created for. <para>新建目录所对应的远程条目。</para></param>
    /// <param name="isDirectory">Whether <paramref name="remotePath"/> is a directory. <para><paramref name="remotePath"/> 是否为目录。</para></param>
    /// <param name="folderName">New folder name. <para>新目录名。</para></param>
    public Task CreateDeviceDirectoryAsync(DetectedDevice device, string remotePath, bool isDirectory, string folderName) =>
        DeviceFileSystemService.Instance.CreateDirectoryAsync(device,
            DeviceFileSystemService.CombinePath(isDirectory ? remotePath : DeviceFileSystemService.GetParentPath(remotePath), folderName));

    /// <summary>
    /// Copies one local path into this column's remote device directory.
    /// <para>把一个本地路径复制到本列的远程设备目录。</para>
    /// </summary>
    /// <param name="device">Target device. <para>目标设备。</para></param>
    /// <param name="localPath">Local file or directory to push. <para>待推送的本地文件或目录。</para></param>
    /// <param name="remoteDestination">Remote directory or file path; a trailing slash pushes inside it.
    /// <para>远程目录或文件路径；以斜杠结尾时推入该目录内。</para></param>
    public Task PushToDeviceAsync(DetectedDevice device, string localPath, string remoteDestination) =>
        DeviceFileSystemService.Instance.PushAsync(device, localPath, remoteDestination);

    /// <summary>
    /// Copies one remote entry of this column into a local directory.
    /// <para>把本列的一个远程条目复制到本地目录。</para>
    /// </summary>
    /// <param name="device">Target device. <para>目标设备。</para></param>
    /// <param name="remotePath">Remote file or directory to pull. <para>待拉取的远程文件或目录。</para></param>
    /// <param name="localDestination">Local directory or file path; an existing directory receives the entry name.
    /// <para>本地目录或文件路径；已存在的目录按条目名接收。</para></param>
    public Task PullFromDeviceAsync(DetectedDevice device, string remotePath, string localDestination) =>
        DeviceFileSystemService.Instance.PullAsync(device, remotePath, localDestination);

    /// <summary>
    /// Copies one remote file of this column into a local temporary file and returns its path.
    /// <para>把本列的一个远程文件复制到本地临时文件并返回其路径。</para>
    /// </summary>
    /// <param name="device">Target device. <para>目标设备。</para></param>
    /// <param name="remotePath">Remote file path. <para>远程文件路径。</para></param>
    /// <returns>The local temporary file path. <para>本地临时文件路径。</para></returns>
    public Task<string> PullDeviceEntryToTempAsync(DetectedDevice device, string remotePath) =>
        DeviceFileSystemService.Instance.PullToTempAsync(device, remotePath);

    private static string RemoteChildPath(string remotePath, string name) =>
        DeviceFileSystemService.CombinePath(DeviceFileSystemService.GetParentPath(remotePath), name);
}