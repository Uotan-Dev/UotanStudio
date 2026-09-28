/* CPU-only contract tests. These deliberately do not claim VA hardware decode.
 * Include the implementation to test its actual crop/copy and format guard. */
#include "../uotan_va.c"
#include <assert.h>
#include <stddef.h>

_Static_assert(sizeof(uv_frame_info) == 32, "managed frame ABI size");
_Static_assert(offsetof(uv_frame_info, pts) == 24, "managed PTS offset");
_Static_assert(sizeof(uv_dmabuf_object) == 16, "managed DMA-BUF object ABI size");
_Static_assert(sizeof(uv_dmabuf_layer) == 56, "managed DMA-BUF layer ABI size");
_Static_assert(sizeof(uv_dmabuf_frame) == 328, "managed DMA-BUF frame ABI size");
_Static_assert(offsetof(uv_dmabuf_frame, objects) == 16, "managed descriptor prefix");
_Static_assert(offsetof(uv_dmabuf_frame, layer_count) == 80, "managed layer count offset");
_Static_assert(offsetof(uv_dmabuf_frame, layers) == 84, "managed layers offset");
_Static_assert(offsetof(uv_dmabuf_frame, matrix) == 308, "managed color metadata offset");
_Static_assert(offsetof(uv_dmabuf_frame, pts) == 320, "managed PTS offset");

static void test_crop_copy(void)
{
    AVFrame *frame = av_frame_alloc();
    frame->format = AV_PIX_FMT_NV12;
    frame->width = 16;
    frame->height = 12;
    assert(av_frame_get_buffer(frame, 64) == 0);
    for (int y = 0; y < 12; y++)
        for (int x = 0; x < 16; x++) frame->data[0][y * frame->linesize[0] + x] = y * 16 + x;
    for (int y = 0; y < 6; y++)
        for (int x = 0; x < 16; x++) frame->data[1][y * frame->linesize[1] + x] = 128 + y * 16 + x;
    frame->crop_left = 2;
    frame->crop_right = 4;
    frame->crop_top = 2;
    frame->crop_bottom = 4;
    frame->pts = 1234567890123LL;
    frame->colorspace = AVCOL_SPC_BT709;
    frame->color_range = AVCOL_RANGE_JPEG;
    char error[UV_ERROR_BYTES] = {0};
    uv_frame_info pending;
    assert(describe_downloaded_frame(frame, &pending, error) == 0);
    assert(pending.width == 10 && pending.height == 6);
    assert(pending.stride == 10 && pending.length == 90);
    assert(pending.pts == 1234567890123LL);
    assert(pending.matrix == 2 && pending.range == 2);
    uint8_t bytes[92];
    memset(bytes, 0xcc, sizeof(bytes));
    copy_planes(&pending, frame, bytes + 1);
    assert(bytes[0] == 0xcc && bytes[91] == 0xcc);
    for (int y = 0; y < 6; y++)
        for (int x = 0; x < 10; x++) assert(bytes[1 + y * 10 + x] == (y + 2) * 16 + x + 2);
    for (int y = 0; y < 3; y++)
        for (int x = 0; x < 10; x++) assert(bytes[61 + y * 10 + x] == 128 + (y + 1) * 16 + x + 2);
    av_frame_free(&frame);
}

static void test_format_guard(void)
{
    AVCodecContext *context = avcodec_alloc_context3(NULL);
    assert(context);
    context->coded_width = context->coded_height = 64;
    context->sw_pix_fmt = AV_PIX_FMT_YUV420P;
    const enum AVPixelFormat software[] = {AV_PIX_FMT_YUV420P, AV_PIX_FMT_NONE};
    const enum AVPixelFormat hardware[] = {AV_PIX_FMT_YUV420P, AV_PIX_FMT_VAAPI, AV_PIX_FMT_NONE};
    assert(hardware_format(context, software) == AV_PIX_FMT_NONE);
    assert(hardware_format(context, hardware) == AV_PIX_FMT_VAAPI);
    context->sw_pix_fmt = AV_PIX_FMT_YUV420P10LE;
    assert(hardware_format(context, hardware) == AV_PIX_FMT_NONE);
    context->sw_pix_fmt = AV_PIX_FMT_YUV420P;
    context->coded_width = INT_MAX;
    assert(hardware_format(context, hardware) == AV_PIX_FMT_NONE);
    avcodec_free_context(&context);
}

static void test_odd_visible_dimensions(void)
{
    AVFrame *frame = av_frame_alloc();
    frame->format = AV_PIX_FMT_NV12;
    frame->width = 7; frame->height = 5;
    assert(av_frame_get_buffer(frame, 32) == 0);
    for (int y = 0; y < 5; y++) memset(frame->data[0] + y * frame->linesize[0], 16, 7);
    for (int y = 0; y < 3; y++) memset(frame->data[1] + y * frame->linesize[1], 128, 8);
    char error[UV_ERROR_BYTES];
    uv_frame_info pending;
    assert(describe_downloaded_frame(frame, &pending, error) == 0);
    assert(pending.stride == 8 && pending.length == 64);
    uint8_t bytes[64];
    copy_planes(&pending, frame, bytes);
    for (int y = 0; y < 5; y++) assert(bytes[y * 8 + 7] == 0);
    for (int i = 40; i < 64; i++) assert(bytes[i] == 128);
    av_frame_free(&frame);
}

static void test_held_frame_description(void)
{
    char error[UV_ERROR_BYTES];
    AVFrame software = {0};
    software.format = AV_PIX_FMT_NV12;
    assert(describe_held_frame(&software, &(uv_frame_info){0}, error) < 0);
    assert(strstr(error, "software fallback"));

    AVFrame hardware = {0};
    hardware.format = AV_PIX_FMT_VAAPI;
    hardware.hw_frames_ctx = (AVBufferRef *)(uintptr_t)1;
    hardware.width = 20;
    hardware.height = 14;
    hardware.crop_left = 2;
    hardware.crop_right = 4;
    hardware.crop_top = 2;
    hardware.crop_bottom = 4;
    hardware.pts = 42;
    hardware.colorspace = AVCOL_SPC_BT470BG;
    hardware.color_range = AVCOL_RANGE_MPEG;
    uv_frame_info info;
    assert(describe_held_frame(&hardware, &info, error) == 0);
    assert(info.width == 14 && info.height == 8);
    assert(info.stride == 14 && info.length == 14 * (8 + 4));
    assert(info.matrix == 1 && info.range == 1 && info.pts == 42);
    hardware.decode_error_flags = 1;
    assert(describe_held_frame(&hardware, &info, error) < 0);
    assert(strstr(error, "corrupt"));
    hardware.decode_error_flags = 0;
    hardware.width = INT_MAX;
    assert(describe_held_frame(&hardware, &info, error) < 0);
    assert(strstr(error, "dimensions"));
}

static void test_held_state_contracts(void)
{
    char error[UV_ERROR_BYTES];
    const AVCodec *codec = avcodec_find_decoder_by_name("h264");
    assert(codec);
    uv_decoder *decoder = av_mallocz(sizeof(*decoder));
    assert(decoder);
    assert(pthread_mutex_init(&decoder->lock, NULL) == 0);
    decoder->codec = avcodec_alloc_context3(codec);
    decoder->hardware = av_frame_alloc();
    decoder->download = av_frame_alloc();
    assert(decoder->codec && decoder->hardware && decoder->download);
    assert(avcodec_open2(decoder->codec, codec, NULL) == 0);
    uv_frame_info info;
    assert(uv_receive(decoder, &info, error) == 0);
    uint8_t byte;
    assert(uv_copy(decoder, &byte, sizeof(byte), error) < 0);
    uv_dmabuf_frame frame;
    assert(uv_export(decoder, &frame, error) < 0);
    assert(strstr(error, "export state"));
    uv_release_frame(decoder);
    uv_release_frame(NULL);
    uv_reset(decoder);
    uv_close(decoder);
}

static int test_real_software_frame_is_rejected(const char *fixture)
{
    FILE *file = fopen(fixture, "rb");
    if (!file) return 77;
    uint8_t bytes[8192];
    size_t length = fread(bytes, 1, sizeof(bytes), file);
    int truncated = ferror(file);
    fclose(file);
    /* Any real H.264 Annex-B access unit exercises the path; the exact corpus
     * bytes are not part of the contract, only that a software frame is refused. */
    assert(length > 0 && !truncated);
    const AVCodec *codec = avcodec_find_decoder_by_name("h264");
    assert(codec);
    uv_decoder *decoder = av_mallocz(sizeof(*decoder));
    assert(decoder);
    assert(pthread_mutex_init(&decoder->lock, NULL) == 0);
    decoder->codec = avcodec_alloc_context3(codec);
    decoder->hardware = av_frame_alloc();
    decoder->download = av_frame_alloc();
    assert(decoder->codec && decoder->hardware && decoder->download);
    decoder->codec->thread_count = 1;
    /* Test-only construction: production uv_open always installs hardware_format. */
    assert(avcodec_open2(decoder->codec, codec, NULL) == 0);
    char error[UV_ERROR_BYTES] = {0};
    assert(uv_send(decoder, bytes, (int)length, 123456, error) == 0);
    uv_frame_info info;
    assert(uv_receive(decoder, &info, error) < 0);
    assert(strstr(error, "software fallback"));
    uv_reset(decoder);
    uv_close(decoder);
    return 0;
}

int main(int argc, char **argv)
{
    assert(argc == 2);
    assert(uv_abi_version() == 2);
    test_crop_copy();
    test_format_guard();
    test_odd_visible_dimensions();
    test_held_frame_description();
    test_held_state_contracts();
    char error[UV_ERROR_BYTES];
    uv_decoder *decoder = (uv_decoder *)1;
    assert(uv_open(999, "/dev/null", &decoder, error) < 0 && decoder == NULL);
    assert(uv_send(NULL, NULL, 0, 0, error) < 0);
    assert(uv_receive(NULL, NULL, error) < 0);
    uv_release_frame(NULL);
    uv_reset(NULL);
    uv_close(NULL);
    int fixture = test_real_software_frame_is_rejected(argv[1]);
    if (fixture == 77) {
        puts("VA bridge ABI 2 contracts passed; SKIP: the H.264 fixture is not present in this checkout.");
        return 77;
    }
    puts("VA bridge ABI 2, held-frame, hardware-only format, bounded crop/copy and failure contracts passed.");
    return 0;
}
