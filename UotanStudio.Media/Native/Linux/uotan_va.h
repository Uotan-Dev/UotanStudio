#ifndef UOTAN_VA_H
#define UOTAN_VA_H

#include <stdint.h>

/* Stable bridge ABI. No FFmpeg structure crosses the managed boundary; the
 * DMA-BUF frame mirrors VADRMPRIMESurfaceDescriptor exactly.
 * Calls on a handle must be serialized; the bridge serializes them internally,
 * so uv_release_frame may also run on a presentation thread.
 * Return values: 0 = accepted/no frame, 1 = frame, 2 = retry (a frame is held
 * or the decoder wants the caller to drain), 3 = end of stream,
 * negative = failure (error text is UTF-8).
 */
#define UV_API __attribute__((visibility("default")))
#define UV_ABI_VERSION 2
#define UV_ERROR_BYTES 1024

typedef struct uv_decoder uv_decoder;
typedef struct uv_frame_info {
    int32_t width, height, stride, length;
    int32_t matrix, range; /* Auto=0, BT.601/Limited=1, BT.709/Full=2 */
    int64_t pts; /* 100 ns ticks; unknown = INT64_MIN */
} uv_frame_info;

/* Byte-identical prefix of VADRMPRIMESurfaceDescriptor plus color metadata. */
typedef struct uv_dmabuf_object {
    int32_t fd;
    uint32_t size;
    uint64_t modifier;
} uv_dmabuf_object;
typedef struct uv_dmabuf_layer {
    uint32_t format, planes;
    uint32_t object_index[4], offset[4], pitch[4];
} uv_dmabuf_layer;
typedef struct uv_dmabuf_frame {
    uint32_t fourcc, width, height;
    uint32_t object_count;
    uv_dmabuf_object objects[4];
    uint32_t layer_count;
    uv_dmabuf_layer layers[4];
    int32_t matrix, range; /* Same encoding as uv_frame_info. */
    int64_t pts;
} uv_dmabuf_frame;

UV_API int uv_abi_version(void);
/* codec: H.264=1, HEVC=2, AV1=3. Requires an explicit DRM render node. */
UV_API int uv_open(int codec, const char *node, uv_decoder **result, char *error);
UV_API void uv_close(uv_decoder *decoder);
/* Input is one complete access unit; NULL/0 signals EOS. Input is copied. */
UV_API int uv_send(uv_decoder *decoder, const uint8_t *data, int length, int64_t pts, char *error);
/* A returned frame (the VA surface) remains owned by the bridge until
 * uv_copy/uv_release_frame/uv_reset/uv_close. While a frame is held,
 * uv_receive returns 2 without pulling from the decoder queue. */
UV_API int uv_receive(uv_decoder *decoder, uv_frame_info *info, char *error);
/* Downloads the held frame into caller memory (packed NV12) and releases it. */
UV_API int uv_copy(uv_decoder *decoder, uint8_t *destination, int capacity, char *error);
/* Exports the held VA surface as read-only separate DRM PRIME layers. The
 * exported file descriptors belong to the caller (close them via the managed
 * snapshot); the frame stays held until uv_release_frame/uv_copy/uv_reset.
 * Requires an uncropped frame; cropped streams must use uv_copy. */
UV_API int uv_export(uv_decoder *decoder, uv_dmabuf_frame *frame, char *error);
/* Releases the held frame back to the decoder pool without downloading. */
UV_API void uv_release_frame(uv_decoder *decoder);
UV_API void uv_reset(uv_decoder *decoder);

#endif
