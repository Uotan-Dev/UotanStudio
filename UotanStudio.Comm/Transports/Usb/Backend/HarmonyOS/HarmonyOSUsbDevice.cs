using System.Diagnostics;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Text;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS.HarmonyOSUsbDDK;

namespace UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;

internal class HarmonyOSUsbDevice : UsbDevice
{
    private const int PlatformDefaultTimeoutMs = UsbTransferPolicies.DefaultTimeoutMs;

    private ulong _deviceId;
    private ulong _interfaceHandle;
    private byte _interfaceIndex;
    private byte _epIn;
    private byte _epOut;
    private IntPtr _devMmap;
    private IntPtr _devMmapBuffer;
    private int _devMmapSize;
    private bool _disposed;
    private bool _ddkInitialized;

    public override int DefaultTimeoutMs => PlatformDefaultTimeoutMs;

    internal void Initialize(
        ulong deviceId,
        byte interfaceIndex,
        byte epIn,
        byte epOut,
        ushort vendorId,
        ushort productId,
        byte interfaceClass,
        byte interfaceSubClass,
        byte interfaceProtocol,
        string? serialNumber)
    {
        _deviceId = deviceId;
        _interfaceIndex = interfaceIndex;
        _epIn = epIn;
        _epOut = epOut;
        VendorId = vendorId;
        ProductId = productId;
        InterfaceClass = interfaceClass;
        InterfaceSubClass = interfaceSubClass;
        InterfaceProtocol = interfaceProtocol;
        InterfaceMetadataObserved = true;
        UsbDeviceType = UsbDeviceType.HarmonyOS;
        DevicePath = $"harmony-ddk://{deviceId}";
        SerialNumber = serialNumber;
    }

    public override int CreateHandle()
    {
        var ret = OH_Usb_Init();
        if (ret != USB_DDK_NO_ERROR)
        {
            UsbTrace.Log($"HarmonyOSUsbDevice: OH_Usb_Init failed: {GetErrorMessage(ret)}");
            return ret;
        }

        _ddkInitialized = true;

        ret = OH_Usb_ClaimInterface(_deviceId, _interfaceIndex, ref _interfaceHandle);
        if (ret != USB_DDK_NO_ERROR)
        {
            UsbTrace.Log($"HarmonyOSUsbDevice: OH_Usb_ClaimInterface failed: {GetErrorMessage(ret)}");
            OH_Usb_Release();
            _ddkInitialized = false;
            return ret;
        }

        var devMmapPtr = IntPtr.Zero;
        ret = OH_Usb_CreateDeviceMemMap(_deviceId, UsbTransferPolicies.LinuxUsbFsMaxBulkSize, ref devMmapPtr);
        if (ret != USB_DDK_NO_ERROR)
        {
            UsbTrace.Log($"HarmonyOSUsbDevice: OH_Usb_CreateDeviceMemMap failed: {GetErrorMessage(ret)}");
            OH_Usb_ReleaseInterface(_interfaceHandle);
            OH_Usb_Release();
            _ddkInitialized = false;
            return ret;
        }

        _devMmap = devMmapPtr;

        var mmapStruct = Marshal.PtrToStructure<UsbDeviceMemMap>(_devMmap);
        _devMmapBuffer = mmapStruct.Buffer;
        _devMmapSize = (int)mmapStruct.Size;

        GetSerialNumber();
        return USB_DDK_NO_ERROR;
    }

    public override void Reset()
    {
        if (_disposed || !_ddkInitialized)
        {
            return;
        }

        OH_Usb_ReleaseInterface(_interfaceHandle);
        OH_Usb_Release();

        _ddkInitialized = false;

        var ret = OH_Usb_Init();
        if (ret == USB_DDK_NO_ERROR)
        {
            _ddkInitialized = true;
            OH_Usb_ClaimInterface(_deviceId, _interfaceIndex, ref _interfaceHandle);
        }
    }

    public override int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        if (_disposed || !_ddkInitialized)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        if (buffer == null)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(length, 0);
        }
        else
        {
            ValidateBufferRange(buffer, offset, length);
        }

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, PlatformDefaultTimeoutMs);
        var stopwatch = Stopwatch.StartNew();

        var setup = new UsbControlRequestSetup
        {
            BmRequestType = setupPacket.RequestType,
            BRequest = setupPacket.Request,
            WValue = setupPacket.Value,
            WIndex = setupPacket.Index,
            WLength = (ushort)length
        };

        int ret;
        var isInDirection = (setupPacket.RequestType & 0x80) != 0;
        var traceOp = isInDirection ? UsbTransferOperation.Read : UsbTransferOperation.Write;

        if (isInDirection)
        {
            var readBuffer = new byte[length > 0 ? length : 256];
            var dataLen = (uint)readBuffer.Length;

            ret = OH_Usb_SendControlReadRequest(_interfaceHandle, ref setup, (uint)effectiveTimeoutMs, readBuffer, ref dataLen);

            stopwatch.Stop();

            if (ret == USB_DDK_NO_ERROR && buffer != null && dataLen > 0)
            {
                var copyLen = Math.Min((int)dataLen, length);
                Buffer.BlockCopy(readBuffer, 0, buffer, offset, copyLen);
                EmitControlTransferTrace(traceOp, length, copyLen, effectiveTimeoutMs, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.Success);
                return copyLen;
            }
        }
        else
        {
            byte[] writeBuffer;
            if (length > 0 && buffer != null)
            {
                writeBuffer = new byte[length];
                Buffer.BlockCopy(buffer, offset, writeBuffer, 0, length);
            }
            else
            {
                writeBuffer = [];
            }

            ret = OH_Usb_SendControlWriteRequest(_interfaceHandle, ref setup, (uint)effectiveTimeoutMs, writeBuffer, (uint)writeBuffer.Length);

            stopwatch.Stop();

            if (ret == USB_DDK_NO_ERROR)
            {
                EmitControlTransferTrace(traceOp, length, length, effectiveTimeoutMs, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.Success);
                return length;
            }
        }

        var outcome = ret == USB_DDK_TIMEOUT ? UsbTransferOutcome.Timeout : UsbTransferOutcome.FatalError;
        EmitControlTransferTrace(traceOp, length, 0, effectiveTimeoutMs, stopwatch.ElapsedMilliseconds, outcome);

        return ret != USB_DDK_NO_ERROR ? throw new UsbTransferException($"USB control transfer failed: {GetErrorMessage(ret)}", ret) : 0;
    }

    public override byte[] Read(int length) => Read(length, PlatformDefaultTimeoutMs);

    public override byte[] Read(int length, int timeoutMs)
    {
        if (length <= 0)
        {
            return [];
        }

        var buffer = new byte[length];
        var count = ReadInto(buffer, 0, length, timeoutMs);
        if (count == length)
        {
            return buffer;
        }

        if (count == 0)
        {
            return [];
        }

        var result = new byte[count];
        Buffer.BlockCopy(buffer, 0, result, 0, count);
        return result;
    }

    public override int ReadInto(byte[] buffer, int offset, int length) => ReadInto(buffer, offset, length, PlatformDefaultTimeoutMs);

    protected override string BackendName => "harmony-ddk";

    protected override bool IsOpen => !_disposed && _ddkInitialized;

    /// <inheritdoc/>
    internal override bool IsHandleOpen => !_disposed && _ddkInitialized;

    protected override int MaxChunkSize => _devMmapSize > 0 ? _devMmapSize : base.MaxChunkSize;

    protected override UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs)
    {
        var pipe = new UsbRequestPipe
        {
            InterfaceHandle = _interfaceHandle,
            EndpointAddress = _epIn,
            Timeout = (uint)timeoutMs
        };

        var devMmap = new UsbDeviceMemMap
        {
            DeviceId = _deviceId,
            Buffer = _devMmapBuffer,
            Size = (UIntPtr)_devMmapSize,
            Offset = UIntPtr.Zero,
            Length = (UIntPtr)length
        };

        var ret = OH_Usb_SendPipeRequest(ref pipe, ref devMmap);
        if (ret == USB_DDK_NO_ERROR)
        {
            var transferred = (int)devMmap.Length;
            var copyLen = Math.Min(transferred, length);
            if (copyLen > 0)
            {
                unsafe
                {
                    Buffer.MemoryCopy((void*)_devMmapBuffer, (void*)buffer, length, copyLen);
                }
            }

            return UsbChunkResult.Success(copyLen);
        }

        return ret == USB_DDK_TIMEOUT ? UsbChunkResult.Timeout(ret) : UsbChunkResult.Fatal(ret);
    }

    protected override UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs)
    {
        if (_devMmapBuffer == IntPtr.Zero || length > _devMmapSize)
        {
            return UsbChunkResult.Fatal(USB_DDK_INVALID_OPERATION);
        }
        unsafe
        {
            Buffer.MemoryCopy((void*)buffer, (void*)_devMmapBuffer, _devMmapSize, length);
        }

        var pipe = new UsbRequestPipe
        {
            InterfaceHandle = _interfaceHandle,
            EndpointAddress = _epOut,
            Timeout = (uint)timeoutMs
        };

        var devMmap = new UsbDeviceMemMap
        {
            DeviceId = _deviceId,
            Buffer = _devMmapBuffer,
            Size = (UIntPtr)_devMmapSize,
            Offset = UIntPtr.Zero,
            Length = (UIntPtr)length
        };

        var ret = OH_Usb_SendPipeRequest(ref pipe, ref devMmap);
        return ret == USB_DDK_NO_ERROR
            ? UsbChunkResult.Success((int)devMmap.Length)
            : ret == USB_DDK_TIMEOUT ? UsbChunkResult.Timeout(ret) : UsbChunkResult.Fatal(ret);
    }

    protected override Exception CreateReadFatalException(int nativeError)
        => new UsbTransferException($"USB read failed: {GetErrorMessage(nativeError)}", nativeError);

    protected override Exception CreateWriteFatalException(int nativeError)
        => new UsbTransferException($"USB write failed: {GetErrorMessage(nativeError)}", nativeError);

    public override long Write(byte[] data, int length) => Write(data, length, PlatformDefaultTimeoutMs);

    public override void WriteZlp(int timeoutMs)
    {
        if (_devMmapBuffer == IntPtr.Zero || _devMmapSize == 0)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }

        var pipe = new UsbRequestPipe
        {
            InterfaceHandle = _interfaceHandle,
            EndpointAddress = _epOut,
            Timeout = (uint)timeoutMs
        };

        var devMmap = new UsbDeviceMemMap
        {
            DeviceId = _deviceId,
            Buffer = _devMmapBuffer,
            Size = (UIntPtr)_devMmapSize,
            Offset = UIntPtr.Zero,
            Length = UIntPtr.Zero
        };

        var ret = OH_Usb_SendPipeRequest(ref pipe, ref devMmap);
        if (ret != USB_DDK_NO_ERROR)
        {
            if (ret == USB_DDK_TIMEOUT)
            {
                return;
            }
            throw new IOException($"HarmonyOS zero-length write failed with error: {ret}");
        }
    }

    public override int GetSerialNumber()
    {
        if (_disposed || !_ddkInitialized)
        {
            return -1;
        }

        var setup = new UsbControlRequestSetup
        {
            BmRequestType = 0x80,
            BRequest = 0x06,
            WValue = 0x0300,
            WIndex = 0,
            WLength = 255
        };

        var descriptor = new byte[256];
        uint dataLen = 256;

        var ret = OH_Usb_SendControlReadRequest(_interfaceHandle, ref setup, 1000, descriptor, ref dataLen);
        if (ret != USB_DDK_NO_ERROR || dataLen < 4)
        {
            return -1;
        }

        var langCount = (int)(dataLen - 2) / 2;
        if (langCount == 0)
        {
            return -1;
        }

        var langId = (ushort)(descriptor[2] | (descriptor[3] << 8));

        var devDesc = new UsbDeviceDescriptor();
        ret = OH_Usb_GetDeviceDescriptor(_deviceId, ref devDesc);
        if (ret != USB_DDK_NO_ERROR)
        {
            return -1;
        }

        if (devDesc.ISerialNumber == 0)
        {
            return -1;
        }

        setup.WValue = (ushort)((0x03 << 8) | devDesc.ISerialNumber);
        setup.WIndex = langId;
        setup.WLength = 255;
        dataLen = 256;

        ret = OH_Usb_SendControlReadRequest(_interfaceHandle, ref setup, 1000, descriptor, ref dataLen);
        if (ret != USB_DDK_NO_ERROR || dataLen < 2)
        {
            return -1;
        }

        var stringLen = (int)(dataLen - 2);
        if (stringLen > 0)
        {
            SerialNumber = System.Text.Encoding.Unicode.GetStringTrimNul(descriptor, 2, stringLen);
        }

        return 0;
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_devMmap != IntPtr.Zero)
        {
            OH_Usb_DestroyDeviceMemMap(_devMmap);
            _devMmap = IntPtr.Zero;
        }

        if (_interfaceHandle != 0)
        {
            OH_Usb_ReleaseInterface(_interfaceHandle);
            _interfaceHandle = 0;
        }

        if (_ddkInitialized)
        {
            OH_Usb_Release();
            _ddkInitialized = false;
        }
    }

    private void EmitControlTransferTrace(UsbTransferOperation operation, int requested, int transferred, int timeoutMs, long elapsedMs, UsbTransferOutcome outcome)
    {
        if (!UsbTrace.HasTransferSubscribers)
        {
            return;
        }

        UsbTrace.EmitTransfer(new UsbTransferEvent
        {
            Backend = "harmony-ddk",
            DevicePath = DevicePath,
            Operation = operation,
            RequestedBytes = requested,
            TransferredBytes = transferred,
            TimeoutMs = timeoutMs,
            RetryCount = 0,
            NativeErrorCode = null,
            ElapsedMs = elapsedMs,
            Outcome = outcome
        });
    }
}