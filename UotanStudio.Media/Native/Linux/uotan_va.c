#include "uotan_va.h"
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <string.h>
#include <libavcodec/avcodec.h>
#include <libavutil/error.h>
#include <libavutil/hwcontext.h>
#include <libavutil/pixdesc.h>

#define UV_MAX_PACKET (32 * 1024 * 1024)
#define UV_MAX_FRAME (256 * 1024 * 1024)

struct uv_decoder {
    AVCodecContext *codec;
    AVBufferRef *device;
    AVFrame *hardware, *download;
    uv_frame_info pending;
    int has_frame;
};

static int fail(char *error, const char *operation, int code)
{
    char detail[AV_ERROR_MAX_STRING_SIZE];
    av_strerror(code, detail, sizeof(detail));
    if (error) snprintf(error, UV_ERROR_BYTES, "%s: %s (%d)", operation, detail, code);
    return code < 0 ? code : AVERROR(EINVAL);
}

static int dimensions_valid(int width, int height)
{
    return width > 0 && height > 0 && width <= 16384 && height <= 16384 &&
        (int64_t)width * height * 4 <= UV_MAX_FRAME;
}

/* Never return a software format. libavcodec owns syntax/DPB/VA parameters;
 * decoded frames must still be verified as VAAPI before downloading. */
static enum AVPixelFormat hardware_format(AVCodecContext *context, const enum AVPixelFormat *formats)
{
    if (!dimensions_valid(context->coded_width, context->coded_height)) return AV_PIX_FMT_NONE;
    const AVPixFmtDescriptor *descriptor = av_pix_fmt_desc_get(context->sw_pix_fmt);
    if (!descriptor || descriptor->comp[0].depth != 8 ||
        descriptor->log2_chroma_w != 1 || descriptor->log2_chroma_h != 1 ||
        descriptor->nb_components != 3) return AV_PIX_FMT_NONE;
    for (const enum AVPixelFormat *p = formats; *p != AV_PIX_FMT_NONE; ++p)
        if (*p == AV_PIX_FMT_VAAPI) return *p;
    return AV_PIX_FMT_NONE;
}

int uv_abi_version(void) { return UV_ABI_VERSION; }

void uv_close(uv_decoder *decoder)
{
    if (!decoder) return;
    av_frame_free(&decoder->download);
    av_frame_free(&decoder->hardware);
    avcodec_free_context(&decoder->codec);
    av_buffer_unref(&decoder->device);
    av_free(decoder);
}

int uv_open(int codec, const char *node, uv_decoder **result, char *error)
{
    if (!result) return fail(error, "output handle", AVERROR(EINVAL));
    *result = NULL;
    if (!node || !node[0]) return fail(error, "DRM render node", AVERROR(EINVAL));
    enum AVCodecID id = codec == 1 ? AV_CODEC_ID_H264 : codec == 2 ? AV_CODEC_ID_HEVC :
        codec == 3 ? AV_CODEC_ID_AV1 : AV_CODEC_ID_NONE;
    /* Named native decoders avoid accidentally selecting an external software-only decoder. */
    const char *name = codec == 1 ? "h264" : codec == 2 ? "hevc" : codec == 3 ? "av1" : "";
    const AVCodec *implementation = avcodec_find_decoder_by_name(name);
    if (id == AV_CODEC_ID_NONE || !implementation || implementation->id != id)
        return fail(error, "native codec unavailable", AVERROR_DECODER_NOT_FOUND);
    int supported = 0;
    for (int i = 0;; ++i) {
        const AVCodecHWConfig *config = avcodec_get_hw_config(implementation, i);
        if (!config) break;
        if (config->device_type == AV_HWDEVICE_TYPE_VAAPI && config->pix_fmt == AV_PIX_FMT_VAAPI &&
            (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX)) supported = 1;
    }
    if (!supported) return fail(error, "codec has no VA-API implementation", AVERROR(ENOSYS));
    uv_decoder *decoder = av_mallocz(sizeof(*decoder));
    if (!decoder) return fail(error, "allocate decoder", AVERROR(ENOMEM));
    int status = av_hwdevice_ctx_create(&decoder->device, AV_HWDEVICE_TYPE_VAAPI, node, NULL, 0);
    if (status < 0) { uv_close(decoder); return fail(error, "create VA-API device", status); }
    decoder->codec = avcodec_alloc_context3(implementation);
    decoder->hardware = av_frame_alloc();
    decoder->download = av_frame_alloc();
    if (!decoder->codec || !decoder->hardware || !decoder->download) {
        uv_close(decoder); return fail(error, "allocate codec state", AVERROR(ENOMEM));
    }
    decoder->codec->hw_device_ctx = av_buffer_ref(decoder->device);
    if (!decoder->codec->hw_device_ctx) { uv_close(decoder); return fail(error, "retain device", AVERROR(ENOMEM)); }
    decoder->codec->get_format = hardware_format;
    decoder->codec->thread_count = 1;
    decoder->codec->pkt_timebase = (AVRational){1, 10000000};
    decoder->codec->max_pixels = UV_MAX_FRAME / 4;
    /* Cropping a hardware frame changes dimensions but not the surface layout.
     * Apply it to the downloaded software frame instead. */
    decoder->codec->apply_cropping = 0;
    decoder->codec->err_recognition = AV_EF_CRCCHECK | AV_EF_BITSTREAM | AV_EF_BUFFER | AV_EF_EXPLODE;
    status = avcodec_open2(decoder->codec, implementation, NULL);
    if (status < 0) { uv_close(decoder); return fail(error, "open VA-API codec", status); }
    *result = decoder;
    return 0;
}

int uv_send(uv_decoder *decoder, const uint8_t *data, int length, int64_t pts, char *error)
{
    if (!decoder || length < 0 || length > UV_MAX_PACKET || (!data && length) || (data && !length))
        return fail(error, "access unit bounds", AVERROR(EINVAL));
    AVPacket *packet = NULL;
    int status;
    if (data) {
        packet = av_packet_alloc();
        if (!packet) return fail(error, "allocate packet", AVERROR(ENOMEM));
        status = av_new_packet(packet, length); /* Includes zeroed AV_INPUT_BUFFER_PADDING_SIZE. */
        if (status < 0) { av_packet_free(&packet); return fail(error, "allocate packet data", status); }
        memcpy(packet->data, data, length);
        packet->pts = pts;
        packet->dts = AV_NOPTS_VALUE;
    }
    status = avcodec_send_packet(decoder->codec, packet);
    av_packet_free(&packet);
    if (status == AVERROR(EAGAIN)) return 2;
    if (status == AVERROR_EOF) return 3;
    return status < 0 ? fail(error, "submit VA-API access unit", status) : 0;
}

/* Produces a tightly packed NV12 view after download and exact cropping.
 * libavutil does the driver-specific synchronization/map/transfer (including
 * tiled surfaces). No DMA-BUF object is interpreted as linear memory here. */
static int describe_frame(AVFrame *frame, uv_frame_info *info, char *error)
{
    if (frame->format != AV_PIX_FMT_NV12 || !dimensions_valid(frame->width, frame->height))
        return fail(error, "only bounded 8-bit NV12 output is supported", AVERROR(ENOSYS));
    if ((frame->crop_left & 1) || (frame->crop_top & 1))
        return fail(error, "unaligned chroma crop", AVERROR(ENOSYS));
    int status = av_frame_apply_cropping(frame, AV_FRAME_CROP_UNALIGNED);
    if (status < 0 || !dimensions_valid(frame->width, frame->height))
        return fail(error, "invalid visible frame crop", status < 0 ? status : AVERROR(EINVAL));
    int stride = (frame->width + 1) & ~1;
    if (!frame->data[0] || !frame->data[1] || frame->linesize[0] < frame->width || frame->linesize[1] < stride)
        return fail(error, "invalid NV12 planes", AVERROR(EINVAL));
    *info = (uv_frame_info){
        .width = frame->width, .height = frame->height, .stride = stride,
        .length = stride * (frame->height + (frame->height + 1) / 2),
        .matrix = frame->colorspace == AVCOL_SPC_BT709 ? 2 :
            frame->colorspace == AVCOL_SPC_BT470BG || frame->colorspace == AVCOL_SPC_SMPTE170M ? 1 : 0,
        .range = frame->color_range == AVCOL_RANGE_JPEG ? 2 : frame->color_range == AVCOL_RANGE_MPEG ? 1 : 0,
        .pts = frame->pts
    };
    return 0;
}

int uv_receive(uv_decoder *decoder, uv_frame_info *info, char *error)
{
    if (!decoder || !info || decoder->has_frame) return fail(error, "receive state", AVERROR(EINVAL));
    av_frame_unref(decoder->hardware);
    av_frame_unref(decoder->download);
    int status = avcodec_receive_frame(decoder->codec, decoder->hardware);
    if (status == AVERROR(EAGAIN)) return 0;
    if (status == AVERROR_EOF) return 3;
    if (status < 0) return fail(error, "receive VA-API frame", status);
    if (decoder->hardware->format != AV_PIX_FMT_VAAPI || !decoder->hardware->hw_frames_ctx)
        return fail(error, "decoder attempted software fallback", AVERROR(ENOSYS));
    if ((decoder->hardware->flags & AV_FRAME_FLAG_CORRUPT) || decoder->hardware->decode_error_flags)
        return fail(error, "driver returned a corrupt frame", AVERROR_INVALIDDATA);
    if (!dimensions_valid(decoder->hardware->width, decoder->hardware->height))
        return fail(error, "hardware frame dimensions", AVERROR(EINVAL));
    decoder->download->format = AV_PIX_FMT_NV12;
    status = av_hwframe_transfer_data(decoder->download, decoder->hardware, 0);
    if (status < 0) return fail(error, "download VA-API frame", status);
    status = av_frame_copy_props(decoder->download, decoder->hardware);
    if (status < 0) return fail(error, "copy frame properties", status);
    status = describe_frame(decoder->download, &decoder->pending, error);
    if (status < 0) return status;
    *info = decoder->pending;
    decoder->has_frame = 1;
    return 1;
}

int uv_copy(uv_decoder *decoder, uint8_t *destination, int capacity, char *error)
{
    if (!decoder || !decoder->has_frame || !destination || capacity < decoder->pending.length)
        return fail(error, "output buffer bounds", AVERROR(EINVAL));
    const uv_frame_info *info = &decoder->pending;
    memset(destination, 0, info->length);
    for (int plane = 0; plane < 2; ++plane) {
        int rows = plane ? (info->height + 1) / 2 : info->height;
        int bytes = plane ? info->stride : info->width;
        uint8_t *target = destination + (plane ? info->stride * info->height : 0);
        for (int row = 0; row < rows; ++row)
            memcpy(target + (size_t)row * info->stride,
                decoder->download->data[plane] + (size_t)row * decoder->download->linesize[plane], bytes);
    }
    decoder->has_frame = 0;
    av_frame_unref(decoder->download);
    av_frame_unref(decoder->hardware);
    return 0;
}

void uv_reset(uv_decoder *decoder)
{
    if (!decoder) return;
    avcodec_flush_buffers(decoder->codec);
    av_frame_unref(decoder->download);
    av_frame_unref(decoder->hardware);
    decoder->has_frame = 0;
}
