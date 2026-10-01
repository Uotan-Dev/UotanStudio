namespace UotanStudio.Media.GStreamer;

/// <summary>Owns a bounded appsrc pipeline and its error bus.</summary>
internal sealed unsafe class GstPipeline : IDisposable
{
    private nint _pipeline, _source, _sink, _bus;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _disposed;
    internal const int MaximumQueuedBytes = 32 * 1024 * 1024;
    internal GstPipeline(string description, bool hasSink = true)
    {
        GstNative.Initialize();
        _pipeline = GstNative.gst_parse_launch(description, out nint error);
        try
        {
            if (error != 0) throw new NotSupportedException(GstNative.ReadError(error));
            if (_pipeline == 0) throw new NotSupportedException("GStreamer did not create a pipeline.");
            _source = GstNative.gst_bin_get_by_name(_pipeline, "input");
            _sink = hasSink ? GstNative.gst_bin_get_by_name(_pipeline, "output") : 0;
            _bus = GstNative.gst_element_get_bus(_pipeline);
            if (_source == 0 || (hasSink && _sink == 0) || _bus == 0) throw new InvalidOperationException("Missing GStreamer endpoints.");
            if (GstNative.gst_element_set_state(_pipeline, 4) == 0) { CheckError(); throw new NotSupportedException("GStreamer could not enter PLAYING."); }
            CheckError();
        }
        catch { Dispose(); throw; }
    }
    internal void Push(ReadOnlySpan<byte> packet, long ticks)
    {
        CheckThread();
        if (packet.IsEmpty || packet.Length > MaximumQueuedBytes) throw new ArgumentOutOfRangeException(nameof(packet));
        CheckError();
        if (GstNative.gst_app_src_get_current_level_bytes(_source) + (ulong)packet.Length > MaximumQueuedBytes)
            throw new InvalidOperationException("GStreamer input queue exceeded its bounded budget.");
        nint buffer = GstNative.gst_buffer_new_allocate(0, (nuint)packet.Length, 0);
        if (buffer == 0) throw new OutOfMemoryException();
        try
        {
            fixed (byte* data = packet)
                if (GstNative.gst_buffer_fill(buffer, 0, data, (nuint)packet.Length) != (nuint)packet.Length) throw new InvalidDataException("Truncated GStreamer input copy.");
            GstNative.SetTimestamp(buffer, ticks);
        }
        catch { GstNative.gst_mini_object_unref(buffer); throw; }
        int flow = GstNative.gst_app_src_push_buffer(_source, buffer);
        if (flow != 0) { CheckError(); throw new InvalidOperationException($"GStreamer input flow failed: {flow}."); }
    }
    internal nint Pull(ulong timeout = 0)
    {
        CheckThread();
        CheckError();
        nint sample = GstNative.gst_app_sink_try_pull_sample(_sink, timeout);
        if (sample == 0) CheckError();
        return sample;
    }
    internal bool IsEos => GstNative.gst_app_sink_is_eos(_sink) != 0;
    internal ulong QueuedBytes => GstNative.gst_app_src_get_current_level_bytes(_source);
    internal void End()
    {
        CheckThread();
        if (GstNative.gst_app_src_end_of_stream(_source) != 0) throw new InvalidOperationException("GStreamer rejected EOS.");
    }
    internal void CheckError()
    {
        nint message = GstNative.gst_bus_pop_filtered(_bus, 2);
        if (message == 0) return;
        try
        {
            GstNative.gst_message_parse_error(message, out nint error, out nint debug);
            if (debug != 0) GstNative.g_free(debug);
            throw new InvalidOperationException(GstNative.ReadError(error));
        }
        finally { GstNative.gst_mini_object_unref(message); }
    }
    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("GStreamer pipeline belongs to another thread.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        CheckThread();
        _disposed = true;
        if (_pipeline != 0) _ = GstNative.gst_element_set_state(_pipeline, 1);
        if (_sink != 0) GstNative.gst_object_unref(_sink);
        if (_source != 0) GstNative.gst_object_unref(_source);
        if (_bus != 0) GstNative.gst_object_unref(_bus);
        if (_pipeline != 0) GstNative.gst_object_unref(_pipeline);
        _pipeline = _source = _sink = _bus = 0;
    }
}