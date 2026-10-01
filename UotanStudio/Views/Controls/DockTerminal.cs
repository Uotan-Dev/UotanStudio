using Avalonia.LogicalTree;

namespace UotanStudio.Views.Controls;

internal sealed class DockTerminal : Iciclecreek.Terminal.TerminalView
{
    private bool _disposed;

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {

        BeginReparent();
        base.OnDetachedFromLogicalTree(e);
        if (!_disposed && Terminal is { } terminal)
        {

            terminal.DataReceived -= ForwardBackgroundReply;
            terminal.DataReceived += ForwardBackgroundReply;
        }
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        if (Terminal is { } terminal) terminal.DataReceived -= ForwardBackgroundReply;
        base.OnAttachedToLogicalTree(e);
        EndReparent();
    }

    private async void ForwardBackgroundReply(object? sender, XTerm.Events.TerminalEvents.DataEventArgs e) =>
        await SendInputAsync(e.Data);

    public new void Dispose()
    {
        _disposed = true;
        if (Terminal is { } terminal) terminal.DataReceived -= ForwardBackgroundReply;
        base.Dispose();
    }
}