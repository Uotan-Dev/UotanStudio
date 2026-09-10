/* CPU-only contract tests. These deliberately do not claim VA hardware decode.
 * Include the implementation to test its actual crop/copy and format guard. */
#include "../uotan_va.c"
#include <assert.h>
#include <stddef.h>

_Static_assert(sizeof(uv_frame_info) == 32, "managed frame ABI size");
_Static_assert(offsetof(uv_frame_info, pts) == 24, "managed PTS offset");

static void test_crop_copy(void)
{
    uv_decoder decoder = {0};
    decoder.download = av_frame_alloc();
    decoder.hardware = av_frame_alloc();
    AVFrame *frame = decoder.download;
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
    assert(describe_frame(frame, &decoder.pending, error) == 0);
    assert(decoder.pending.width == 10 && decoder.pending.height == 6);
    assert(decoder.pending.stride == 10 && decoder.pending.length == 90);
    assert(decoder.pending.pts == 1234567890123LL);
    assert(decoder.pending.matrix == 2 && decoder.pending.range == 2);
    uint8_t bytes[92];
    memset(bytes, 0xcc, sizeof(bytes));
    decoder.has_frame = 1;
    assert(uv_copy(&decoder, bytes + 1, 89, error) < 0);
    assert(uv_copy(&decoder, bytes + 1, 90, error) == 0);
    assert(bytes[0] == 0xcc && bytes[91] == 0xcc);
    for (int y = 0; y < 6; y++)
        for (int x = 0; x < 10; x++) assert(bytes[1 + y * 10 + x] == (y + 2) * 16 + x + 2);
    for (int y = 0; y < 3; y++)
        for (int x = 0; x < 10; x++) assert(bytes[61 + y * 10 + x] == 128 + (y + 1) * 16 + x + 2);
    assert(!decoder.has_frame);
    assert(uv_copy(&decoder, bytes, 92, error) < 0);
    av_frame_free(&decoder.download);
    av_frame_free(&decoder.hardware);
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
    uv_decoder decoder = {0};
    decoder.download = av_frame_alloc();
    decoder.hardware = av_frame_alloc();
    AVFrame *frame = decoder.download;
    frame->format = AV_PIX_FMT_NV12;
    frame->width = 7; frame->height = 5;
    assert(av_frame_get_buffer(frame, 32) == 0);
    for (int y = 0; y < 5; y++) memset(frame->data[0] + y * frame->linesize[0], 16, 7);
    for (int y = 0; y < 3; y++) memset(frame->data[1] + y * frame->linesize[1], 128, 8);
    char error[UV_ERROR_BYTES];
    assert(describe_frame(frame, &decoder.pending, error) == 0);
    assert(decoder.pending.stride == 8 && decoder.pending.length == 64);
    decoder.has_frame = 1;
    uint8_t bytes[64];
    assert(uv_copy(&decoder, bytes, sizeof(bytes), error) == 0);
    for (int y = 0; y < 5; y++) assert(bytes[y * 8 + 7] == 0);
    for (int i = 40; i < 64; i++) assert(bytes[i] == 128);
    av_frame_free(&decoder.download);
    av_frame_free(&decoder.hardware);
}

static void test_real_software_frame_is_rejected(const char *fixture)
{
    FILE *file = fopen(fixture, "rb");
    assert(file);
    uint8_t bytes[8192];
    size_t length = fread(bytes, 1, sizeof(bytes), file);
    assert(length == 6209 && !ferror(file));
    fclose(file);
    const AVCodec *codec = avcodec_find_decoder_by_name("h264");
    assert(codec);
    uv_decoder *decoder = av_mallocz(sizeof(*decoder));
    assert(decoder);
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
}

int main(int argc, char **argv)
{
    assert(argc == 2);
    assert(uv_abi_version() == 1);
    test_crop_copy();
    test_format_guard();
    test_odd_visible_dimensions();
    test_real_software_frame_is_rejected(argv[1]);
    char error[UV_ERROR_BYTES];
    uv_decoder *decoder = (uv_decoder *)1;
    assert(uv_open(999, "/dev/null", &decoder, error) < 0 && decoder == NULL);
    assert(uv_send(NULL, NULL, 0, 0, error) < 0);
    assert(uv_receive(NULL, NULL, error) < 0);
    uv_reset(NULL);
    uv_close(NULL);
    puts("VA bridge ABI, hardware-only format, bounded crop/copy and failure contracts passed.");
    return 0;
}
