using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend.Windows;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Locks in the file-name suffixes the Win32 Rockchip boot ROM backend hands to <c>rockusb.sys</c>: the driver binds
/// exactly one pipe per opened handle and derives it from the number trailing the name, which the official
/// <c>RKDevTool</c> writes as <c>\PIPEnn</c>, so a wrong suffix silently binds the wrong endpoint (or none at all).
/// <para>锁定 Win32 瑞芯微 boot ROM 后端交给 <c>rockusb.sys</c> 的文件名后缀：驱动为每个已打开句柄只绑定一条管道，且按
/// 名字末尾的数字推导（官方 <c>RKDevTool</c> 写作 <c>\PIPEnn</c>），后缀写错就会静默绑定到错误的端点（或绑不上端点）。</para>
/// </summary>
public sealed class RockusbPipeSelectionTests
{
    /// <summary>Builds one endpoint descriptor with the given address and transfer type.
    /// <para>按给定地址与传输类型构造一个端点描述符。</para>
    /// </summary>
    /// <param name="address">The <c>bEndpointAddress</c> value. <para><c>bEndpointAddress</c> 取值。</para></param>
    /// <param name="attributes">The <c>bmAttributes</c> transfer-type bits. <para><c>bmAttributes</c> 的传输类型位。</para></param>
    /// <returns>The endpoint descriptor. <para>端点描述符。</para></returns>
    private static UsbEndpointInfo Endpoint(byte address, byte attributes = 0x02) => new()
    {
        EndpointAddress = address,
        Attributes = attributes,
        MaxPacketSize = 512,
        Interval = 0
    };

    /// <summary>A single bulk pair maps to the driver's first-IN and first-OUT selectors.
    /// <para>唯一的批量端点对映射到驱动的「第一条 IN」与「第一条 OUT」选择器。</para>
    /// </summary>
    [Fact]
    public void SelectPipes_SingleBulkPair_UsesFirstInAndFirstOutSelectors()
    {
        var selection = RockusbDevice.SelectPipes([Endpoint(0x81), Endpoint(0x02)]);

        Assert.NotNull(selection);
        Assert.Equal("PIPE00", selection.InPipeName);
        Assert.Equal("PIPE01", selection.OutPipeName);
        Assert.Equal(0x81, selection.EndpointIn);
        Assert.Equal(0x02, selection.EndpointOut);
    }

    /// <summary>The selectors follow endpoint direction, not descriptor position: an interface that declares its OUT
    /// endpoint first still takes <c>\PIPE00</c> for IN and <c>\PIPE01</c> for OUT.
    /// <para>选择器按端点方向而非描述符顺序工作：先声明 OUT 端点的接口仍然用 <c>\PIPE00</c> 取 IN、用 <c>\PIPE01</c> 取 OUT。</para>
    /// </summary>
    [Fact]
    public void SelectPipes_OutEndpointDeclaredFirst_StillMapsInToZeroAndOutToOne()
    {
        var selection = RockusbDevice.SelectPipes([Endpoint(0x01), Endpoint(0x82)]);

        Assert.NotNull(selection);
        Assert.Equal("PIPE00", selection.InPipeName);
        Assert.Equal("PIPE01", selection.OutPipeName);
        Assert.Equal(0x82, selection.EndpointIn);
        Assert.Equal(0x01, selection.EndpointOut);
    }

    /// <summary>A bulk endpoint that is not the first of its direction is only addressable by its literal pipe index,
    /// which the driver honours from <c>2</c> upward as <c>\PIPE02</c> and above.
    /// <para>不是其方向上第一条的批量端点只能按字面管道序号寻址，驱动从 <c>2</c> 起以 <c>\PIPE02</c> 及以上的形式接受该写法。</para>
    /// </summary>
    [Fact]
    public void SelectPipes_InterruptEndpointPrecedesBulkIn_UsesLiteralPipeIndex()
    {
        var selection = RockusbDevice.SelectPipes([Endpoint(0x83, 0x03), Endpoint(0x02), Endpoint(0x81)]);

        Assert.NotNull(selection);
        Assert.Equal("PIPE02", selection.InPipeName);
        Assert.Equal("PIPE01", selection.OutPipeName);
        Assert.Equal(0x81, selection.EndpointIn);
        Assert.Equal(0x02, selection.EndpointOut);
    }

    /// <summary>An interface whose directed endpoints cannot be named fails instead of binding a neighbouring pipe.
    /// <para>无法按名字精确指向的接口直接失败，而不是绑定到相邻管道。</para>
    /// </summary>
    [Fact]
    public void SelectPipes_UnaddressableBulkIn_ReturnsNull()
    {
        // Two IN endpoints with the bulk one second: index 1 collides with the "first OUT" selector.
        var selection = RockusbDevice.SelectPipes([Endpoint(0x83, 0x03), Endpoint(0x82), Endpoint(0x01)]);

        Assert.Null(selection);
    }

    /// <summary>An interface with no bulk pair reports no selection, so the backend fails the open instead of opening a
    /// control-only port that cannot carry the protocol.
    /// <para>没有批量端点对的接口返回空选择，后端因此让打开失败，而不是打开一个无法承载协议的纯控制端口。</para>
    /// </summary>
    [Fact]
    public void SelectPipes_NoBulkPair_ReturnsNull()
    {
        Assert.Null(RockusbDevice.SelectPipes([Endpoint(0x81, 0x03), Endpoint(0x01, 0x03)]));
        Assert.Null(RockusbDevice.SelectPipes([]));
    }
}