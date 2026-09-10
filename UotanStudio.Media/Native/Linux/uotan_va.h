#ifndef UOTAN_VA_H
#define UOTAN_VA_H

#include <stdint.h>

/* Stable bridge ABI. No FFmpeg/libva structure crosses the managed boundary.
 * Calls on a handle must be serialized by its owning decode thread.
 * Return values: 0 = accepted/no frame, 1 = frame, 2 = retry after receive,
 * 3 = end of stream, negative = failure (error text is UTF-8).
 */
#define UV_API __attribute__((visibility("default")))
#define UV_ABI_VERSION 1
#define UV_ERROR_BYTES 1024

typedef struct uv_decoder uv_decoder;
typedef struct uv_frame_info {
    int32_t width, height, stride, length;
    int32_t matrix, range; /* Auto=0, BT.601/Limited=1, BT.709/Full=2 */
    int64_t pts; /* 100 ns ticks; unknown = INT64_MIN */
} uv_frame_info;

UV_API int uv_abi_version(void);
/* codec: H.264=1, HEVC=2, AV1=3. Requires an explicit DRM render node. */
UV_API int uv_open(int codec, const char *node, uv_decoder **result, char *error);
UV_API void uv_close(uv_decoder *decoder);
/* Input is one complete access unit; NULL/0 signals EOS. Input is copied. */
UV_API int uv_send(uv_decoder *decoder, const uint8_t *data, int length, int64_t pts, char *error);
/* A returned frame remains owned by the bridge until uv_copy/uv_reset/uv_close.
 * Call uv_copy before calling uv_receive again. */
UV_API int uv_receive(uv_decoder *decoder, uv_frame_info *info, char *error);
UV_API int uv_copy(uv_decoder *decoder, uint8_t *destination, int capacity, char *error);
UV_API void uv_reset(uv_decoder *decoder);

#endif
