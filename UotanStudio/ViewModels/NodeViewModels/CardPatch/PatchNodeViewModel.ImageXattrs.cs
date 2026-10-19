using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

public sealed partial class PatchNodeViewModel
{
    /// <summary>
    /// Dispatches the unified xattr node by its Operation input. Read reports the raw value and its presence, while Set and
    /// Delete reuse the staged-edit path with a null value meaning removal and a guarded expected value only when both the
    /// old-value check and the expected presence agree.
    /// <para>按 Operation 输入分派统一扩展属性节点。Read 报告原始值与是否存在；Set 与 Delete 复用暂存编辑路径，
    /// 其中 null 值表示删除，预期值仅在旧值校验与预期存在状态同时成立时才参与守卫。</para>
    /// </summary>
    private async Task ExecuteImageXattrAsync(INodeExecutionContext context)
    {
        var token = context.CancellationToken;
        var operation = Text("Operation").Trim();
        if (string.IsNullOrEmpty(operation))
        {
            operation = "Read";
        }
        var imagePath = Text("Path");
        var format = Text("File System");
        var inImagePath = Text("Image Path");
        var name = Text("Attribute Name");
        switch (operation)
        {
            case "Read":
                var value = await GetRun(context).ReadImageXattrAsync(imagePath, format, inImagePath, name, token).ConfigureAwait(false);
                WriteOutput("Data", value ?? []);
                WriteOutput("Exists", value is not null);
                break;
            case "Set":
                WriteOutput("Changed", await StageImageXattrAsync(context, XattrHex("Hex Value")).ConfigureAwait(false));
                break;
            case "Delete":
                WriteOutput("Changed", await StageImageXattrAsync(context, null).ConfigureAwait(false));
                break;
            default:
                throw new InvalidDataException("Invalid xattr operation.");
        }
    }

    private Task<bool> StageImageXattrAsync(INodeExecutionContext context, byte[]? value)
    {
        var check = Boolean("Check Old Value");
        var edit = new FileSystemXattrEdit(Text("Image Path"), Text("Attribute Name"), value, check,
            check && Boolean("Expected Exists") ? XattrHex("Expected Hex Value") : null);
        return GetRun(context).StageImageXattrAsync(Text("Path"), Text("File System"), edit,
            PositiveImageBytes("Image Capacity"), PositiveImageBytes("Extraction Budget"), context.CancellationToken);
    }

    private async Task ExecuteImageXattrEditAsync(INodeExecutionContext context)
    {
        var check = Boolean("Check Old Value");
        var edit = new FileSystemXattrEdit(Text("Image Path"), Text("Attribute Name"),
            TypeKey == "Image.Xattr.Set" ? XattrHex("Hex Value") : null,
            check, check && Boolean("Expected Exists") ? XattrHex("Expected Hex Value") : null);
        WriteOutput("Changed", await GetRun(context).StageImageXattrAsync(Text("Path"), Text("File System"), edit,
            PositiveImageBytes("Image Capacity"), PositiveImageBytes("Extraction Budget"), context.CancellationToken).ConfigureAwait(false));
    }

    private byte[] XattrHex(string port)
    {
        var text = Text(port);
        if (text.Length > 131072 || (text.Length & 1) != 0 || text.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(Services.LanguageService.Instance.GetString("Node_Patch_InvalidXattrHex"));
        }
        return Convert.FromHexString(text);
    }
}