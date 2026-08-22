#if NETSTANDARD2_0
// LibUsbDotNet 3.0.224 declares UsbContext.HotplugOptions as an init-only property; the
// netstandard2.0 target needs this polyfill so init-only setters can be referenced.
// Kept in its own file because it uses a block-scoped namespace, which cannot coexist
// with the file-scoped namespaces used elsewhere in this project.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
