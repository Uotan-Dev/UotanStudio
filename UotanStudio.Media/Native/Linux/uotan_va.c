#include "uotan_va.h"
#include <errno.h>
#include <limits.h>
#include <pthread.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <libavcodec/avcodec.h>
#include <libavutil/error.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_vaapi.h>
#include <libavutil/pixdesc.h>
#include <va/va.h>
#include <va/va_drmcommon.h>

#define UV_MAX_PACKET (32 * 1024 * 1024)
#define UV_MAX_FRAME (256 * 1024 * 1024)

struct uv_decoder {
    AVCodecContext *codec;
    AVBufferRef *device;
    AVFrame *hardware, *download;
    uv_frame_info pending;
    int has_frame;
    pthread_mutex_t lock;
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
    pthread_mutex_lock(&decoder->lock);
    av_frame_free(&decoder->download);
    av_frame_free(&decoder->hardware);
    avcodec_free_context(&decoder->codec);
    av_buffer_unref(&decoder->device);
    pthread_mutex_unlock(&decoder->lock);
    pthread_mutex_destroy(&decoder->lock);
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
    /* Initialize before any uv_close path so the lock is always valid. */
    if (pthread_mutex_init(&decoder->lock, NULL) != 0) {
        av_free(decoder); return fail(error, "initialize lock", AVERROR(ENOMEM));
    }
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
     * Copy applies it to the downloaded software frame; export refuses cropped
     * frames because a PRIME export always covers the full surface. */
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
    pthread_mutex_lock(&decoder->lock);
    status = avcodec_send_packet(decoder->codec, packet);
    pthread_mutex_unlock(&decoder->lock);
    av_packet_free(&packet);
    if (status == AVERROR(EAGAIN)) return 2;
    if (status == AVERROR_EOF) return 3;
    return status < 0 ? fail(error, "submit VA-API access unit", status) : 0;
}

/* Describes the visible area of a held hardware frame without downloading.
 * The download path re-describes the cropped software frame in uv_copy. */
static int describe_held_frame(AVFrame *frame, uv_frame_info *info, char *error)
{
    if (frame->format != AV_PIX_FMT_VAAPI || !frame->hw_frames_ctx)
        return fail(error, "decoder attempted software fallback", AVERROR(ENOSYS));
    if ((frame->flags & AV_FRAME_FLAG_CORRUPT) || frame->decode_error_flags)
        return fail(error, "driver returned a corrupt frame", AVERROR_INVALIDDATA);
    int width = frame->width - (int)frame->crop_left - (int)frame->crop_right;
    int height = frame->height - (int)frame->crop_top - (int)frame->crop_bottom;
    if (!dimensions_valid(width, height))
        return fail(error, "hardware frame dimensions", AVERROR(EINVAL));
    int stride = (width + 1) & ~1;
    *info = (uv_frame_info){
        .width = width, .height = height, .stride = stride,
        .length = stride * (height + (height + 1) / 2),
        .matrix = frame->colorspace == AVCOL_SPC_BT709 ? 2 :
            frame->colorspace == AVCOL_SPC_BT470BG || frame->colorspace == AVCOL_SPC_SMPTE170M ? 1 : 0,
        .range = frame->color_range == AVCOL_RANGE_JPEG ? 2 : frame->color_range == AVCOL_RANGE_MPEG ? 1 : 0,
        .pts = frame->pts
    };
    return 0;
}

/* Produces a tightly packed NV12 view after download and exact cropping.
 * libavutil does the driver-specific synchronization/map/transfer (including
 * tiled surfaces). No DMA-BUF object is interpreted as linear memory here. */
static int describe_downloaded_frame(AVFrame *frame, uv_frame_info *info, char *error)
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

static void copy_planes(const uv_frame_info *info, AVFrame *planes, uint8_t *destination)
{
    memset(destination, 0, info->length);
    for (int plane = 0; plane < 2; ++plane) {
        int rows = plane ? (info->height + 1) / 2 : info->height;
        int bytes = plane ? info->stride : info->width;
        uint8_t *target = destination + (plane ? info->stride * info->height : 0);
        for (int row = 0; row < rows; ++row)
            memcpy(target + (size_t)row * info->stride,
                planes->data[plane] + (size_t)row * planes->linesize[plane], bytes);
    }
}

int uv_receive(uv_decoder *decoder, uv_frame_info *info, char *error)
{
    if (!decoder || !info) return fail(error, "receive state", AVERROR(EINVAL));
    pthread_mutex_lock(&decoder->lock);
    if (decoder->has_frame) {
        pthread_mutex_unlock(&decoder->lock);
        return 2;
    }
    av_frame_unref(decoder->hardware);
    int status = avcodec_receive_frame(decoder->codec, decoder->hardware);
    if (status == AVERROR(EAGAIN) || status == AVERROR_EOF) {
        pthread_mutex_unlock(&decoder->lock);
        return status == AVERROR(EAGAIN) ? 0 : 3;
    }
    if (status < 0) {
        pthread_mutex_unlock(&decoder->lock);
        return fail(error, "receive VA-API frame", status);
    }
    status = describe_held_frame(decoder->hardware, &decoder->pending, error);
    if (status < 0) {
        av_frame_unref(decoder->hardware);
        pthread_mutex_unlock(&decoder->lock);
        return status;
    }
    *info = decoder->pending;
    decoder->has_frame = 1;
    pthread_mutex_unlock(&decoder->lock);
    return 1;
}

int uv_copy(uv_decoder *decoder, uint8_t *destination, int capacity, char *error)
{
    if (!decoder || !destination || capacity <= 0)
        return fail(error, "output buffer bounds", AVERROR(EINVAL));
    pthread_mutex_lock(&decoder->lock);
    if (!decoder->has_frame || capacity < decoder->pending.length) {
        pthread_mutex_unlock(&decoder->lock);
        return fail(error, "output buffer bounds", AVERROR(EINVAL));
    }
    av_frame_unref(decoder->download);
    decoder->download->format = AV_PIX_FMT_NV12;
    int status = av_hwframe_transfer_data(decoder->download, decoder->hardware, 0);
    if (status >= 0) status = av_frame_copy_props(decoder->download, decoder->hardware);
    if (status >= 0) status = describe_downloaded_frame(decoder->download, &decoder->pending, error);
    if (status < 0) {
        av_frame_unref(decoder->download);
        pthread_mutex_unlock(&decoder->lock);
        return status;
    }
    copy_planes(&decoder->pending, decoder->download, destination);
    av_frame_unref(decoder->download);
    av_frame_unref(decoder->hardware);
    decoder->has_frame = 0;
    pthread_mutex_unlock(&decoder->lock);
    return 0;
}

/* Exports the held VAAPI surface for zero-copy presentation. The surface is
 * not downloaded or modified; synchronization happens here once via
 * vaSyncSurface and again implicitly when the consumer imports the buffers. */
int uv_export(uv_decoder *decoder, uv_dmabuf_frame *frame, char *error)
{
    if (!decoder || !frame)
        return fail(error, "export state", AVERROR(EINVAL));
    pthread_mutex_lock(&decoder->lock);
    if (!decoder->has_frame) {
        pthread_mutex_unlock(&decoder->lock);
        return fail(error, "export state", AVERROR(EINVAL));
    }
    AVFrame *hardware = decoder->hardware;
    const AVFrame *crop = hardware;
    if (crop->crop_left || crop->crop_right || crop->crop_top || crop->crop_bottom) {
        pthread_mutex_unlock(&decoder->lock);
        return fail(error, "export requires an uncropped frame; use uv_copy", AVERROR(ENOSYS));
    }
    AVHWDeviceContext *device = (AVHWDeviceContext *)decoder->device->data;
    AVVAAPIDeviceContext *context = device->hwctx;
    VADisplay display = context->display;
    VASurfaceID surface = *(VASurfaceID *)hardware->data[3];
    VAStatus status = vaSyncSurface(display, surface);
    if (status != VA_STATUS_SUCCESS) {
        pthread_mutex_unlock(&decoder->lock);
        return fail(error, "sync VA-API surface", -status);
    }
    VADRMPRIMESurfaceDescriptor descriptor;
    status = vaExportSurfaceHandle(display, surface, VA_SURFACE_ATTRIB_MEM_TYPE_DRM_PRIME_2,
        VA_EXPORT_SURFACE_READ_ONLY | VA_EXPORT_SURFACE_SEPARATE_LAYERS, &descriptor);
    if (status != VA_STATUS_SUCCESS) {
        pthread_mutex_unlock(&decoder->lock);
        return fail(error, "export VA-API surface", -status);
    }
    if (descriptor.width != (uint32_t)hardware->width || descriptor.height != (uint32_t)hardware->height ||
        descriptor.num_objects < 1 || descriptor.num_objects > 4 ||
        descriptor.num_layers < 1 || descriptor.num_layers > 4) {
        for (uint32_t object = 0; object < descriptor.num_objects; ++object) close(descriptor.objects[object].fd);
        pthread_mutex_unlock(&decoder->lock);
        return fail(error, "unexpected DRM PRIME descriptor", AVERROR(EINVAL));
    }
    memset(frame, 0, sizeof(*frame));
    frame->fourcc = descriptor.fourcc;
    frame->width = descriptor.width;
    frame->height = descriptor.height;
    frame->object_count = descriptor.num_objects;
    for (uint32_t object = 0; object < descriptor.num_objects; ++object) {
        frame->objects[object].fd = descriptor.objects[object].fd;
        frame->objects[object].size = descriptor.objects[object].size;
        frame->objects[object].modifier = descriptor.objects[object].drm_format_modifier;
    }
    frame->layer_count = descriptor.num_layers;
    for (uint32_t layer = 0; layer < descriptor.num_layers; ++layer) {
        frame->layers[layer].format = descriptor.layers[layer].drm_format;
        frame->layers[layer].planes = descriptor.layers[layer].num_planes;
        for (int plane = 0; plane < 4; ++plane) {
            frame->layers[layer].object_index[plane] = descriptor.layers[layer].object_index[plane];
            frame->layers[layer].offset[plane] = descriptor.layers[layer].offset[plane];
            frame->layers[layer].pitch[plane] = descriptor.layers[layer].pitch[plane];
        }
    }
    frame->matrix = decoder->pending.matrix;
    frame->range = decoder->pending.range;
    frame->pts = decoder->pending.pts;
    pthread_mutex_unlock(&decoder->lock);
    return 0;
}

/* Safe from presentation threads; the lock serializes it against the decode
 * thread. Unref'ing an AVFrame only drops an atomic reference. */
void uv_release_frame(uv_decoder *decoder)
{
    if (!decoder) return;
    pthread_mutex_lock(&decoder->lock);
    av_frame_unref(decoder->hardware);
    decoder->has_frame = 0;
    pthread_mutex_unlock(&decoder->lock);
}

void uv_reset(uv_decoder *decoder)
{
    if (!decoder) return;
    pthread_mutex_lock(&decoder->lock);
    avcodec_flush_buffers(decoder->codec);
    av_frame_unref(decoder->download);
    av_frame_unref(decoder->hardware);
    decoder->has_frame = 0;
    pthread_mutex_unlock(&decoder->lock);
}
