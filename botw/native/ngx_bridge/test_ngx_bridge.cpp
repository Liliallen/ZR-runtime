// Standalone DLSS Super Resolution test for the BOTW-PC NGX bridge. SPDX-License-Identifier: MIT
//
// Renders an analytic, aliasing-prone pattern (zone plate) on the CPU at 960x540 that scrolls by a
// known amount every frame, with Halton sub-pixel jitter and exact motion vectors, runs DLSS to
// 1920x1080 for N frames and compares the result with a 4x4 supersampled ground truth.
// Passing criteria: NGX initializes, DLSS reports available, evaluation succeeds, and the DLSS output
// beats a bilinear upscale of an unjittered render (PSNR). The optional sign arguments flip the inputs
// to show that the bridge's conventions are the right ones (any flip should score clearly worse).
//
// usage: test_ngx_bridge <snippet-dir> <data-dir> <out-dir> [sample_offset_sign=+1|-1] [mv_sign=+1|-1]
#include "ngx_bridge.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#define CHECK_VK(x)                                                                     \
    do {                                                                                \
        VkResult r_ = (x);                                                              \
        if (r_ != VK_SUCCESS) {                                                         \
            std::fprintf(stderr, "FAIL %s:%d %s -> %d\n", __FILE__, __LINE__, #x, r_);   \
            std::exit(1);                                                               \
        }                                                                               \
    } while (0)

namespace {

constexpr uint32_t kInW = 960, kInH = 540, kOutW = 1920, kOutH = 1080;
constexpr int kFrames = 64;
constexpr double kScrollPerFrame = 3.0;  // output pixels per frame (1.5 render pixels)

VkDevice g_dev;
VkPhysicalDevice g_phys;

double Pattern(double x, double y, double scroll)
{
    // Zone plate around the image centre (in output pixel units) scrolled horizontally: high-frequency
    // rings alias heavily at low resolution, so reconstruction quality is clearly measurable.
    double u = x + scroll - kOutW * 0.5, v = y - kOutH * 0.5;
    return 0.5 + 0.5 * std::cos((u * u + v * v) * 0.0009);
}

double Halton(int i, int b)
{
    double f = 1, r = 0;
    for (; i > 0; i /= b) {
        f /= b;
        r += f * (i % b);
    }
    return r;
}

uint16_t ToHalf(float f)
{
    uint32_t x;
    std::memcpy(&x, &f, 4);
    uint32_t sign = (x >> 16) & 0x8000, exp = ((x >> 23) & 0xff), mant = x & 0x7fffff;
    if (exp == 0) return sign;
    int e = int(exp) - 127 + 15;
    if (e <= 0) return sign;
    if (e >= 31) return sign | 0x7c00;
    return sign | (e << 10) | (mant >> 13);
}

uint32_t FindMemory(uint32_t bits, VkMemoryPropertyFlags flags)
{
    VkPhysicalDeviceMemoryProperties mp;
    vkGetPhysicalDeviceMemoryProperties(g_phys, &mp);
    for (uint32_t i = 0; i < mp.memoryTypeCount; ++i)
        if ((bits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & flags) == flags) return i;
    std::fprintf(stderr, "no memory type\n");
    std::exit(1);
}

struct Image {
    BotwNgxImage ngx{};
    VkDeviceMemory mem{};
};

Image MakeImage(uint32_t w, uint32_t h, VkFormat fmt, VkImageUsageFlags usage)
{
    Image img;
    VkImageCreateInfo ci{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    ci.imageType = VK_IMAGE_TYPE_2D;
    ci.format = fmt;
    ci.extent = {w, h, 1};
    ci.mipLevels = ci.arrayLayers = 1;
    ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = usage;
    CHECK_VK(vkCreateImage(g_dev, &ci, nullptr, &img.ngx.image));
    VkMemoryRequirements req;
    vkGetImageMemoryRequirements(g_dev, img.ngx.image, &req);
    VkMemoryAllocateInfo ai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, nullptr, req.size, FindMemory(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)};
    CHECK_VK(vkAllocateMemory(g_dev, &ai, nullptr, &img.mem));
    CHECK_VK(vkBindImageMemory(g_dev, img.ngx.image, img.mem, 0));
    VkImageViewCreateInfo vi{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
    vi.image = img.ngx.image;
    vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vi.format = fmt;
    vi.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    CHECK_VK(vkCreateImageView(g_dev, &vi, nullptr, &img.ngx.view));
    img.ngx.format = fmt;
    img.ngx.width = w;
    img.ngx.height = h;
    return img;
}

struct Buffer {
    VkBuffer buf{};
    VkDeviceMemory mem{};
    void* map{};
};

Buffer MakeBuffer(VkDeviceSize size, VkBufferUsageFlags usage)
{
    Buffer b;
    VkBufferCreateInfo ci{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, nullptr, 0, size, usage};
    CHECK_VK(vkCreateBuffer(g_dev, &ci, nullptr, &b.buf));
    VkMemoryRequirements req;
    vkGetBufferMemoryRequirements(g_dev, b.buf, &req);
    VkMemoryAllocateInfo ai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, nullptr, req.size,
                            FindMemory(req.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)};
    CHECK_VK(vkAllocateMemory(g_dev, &ai, nullptr, &b.mem));
    CHECK_VK(vkBindBufferMemory(g_dev, b.buf, b.mem, 0));
    CHECK_VK(vkMapMemory(g_dev, b.mem, 0, size, 0, &b.map));
    return b;
}

void Barrier(VkCommandBuffer cmd)
{
    VkMemoryBarrier mb{VK_STRUCTURE_TYPE_MEMORY_BARRIER, nullptr, VK_ACCESS_MEMORY_WRITE_BIT, VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT};
    vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 1, &mb, 0, nullptr, 0, nullptr);
}

void ToGeneral(VkCommandBuffer cmd, VkImage image)
{
    VkImageMemoryBarrier b{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
    b.dstAccessMask = VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT;
    b.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    b.newLayout = VK_IMAGE_LAYOUT_GENERAL;
    b.srcQueueFamilyIndex = b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = image;
    b.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, nullptr, 0, nullptr, 1, &b);
}

void CopyToImage(VkCommandBuffer cmd, VkBuffer src, VkDeviceSize offset, const Image& dst)
{
    VkBufferImageCopy r{};
    r.bufferOffset = offset;
    r.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    r.imageExtent = {dst.ngx.width, dst.ngx.height, 1};
    vkCmdCopyBufferToImage(cmd, src, dst.ngx.image, VK_IMAGE_LAYOUT_GENERAL, 1, &r);
}

void WritePgm(const std::string& path, const std::vector<double>& img, uint32_t w, uint32_t h)
{
    FILE* f = std::fopen(path.c_str(), "wb");
    std::fprintf(f, "P5\n%u %u\n255\n", w, h);
    for (double v : img) std::fputc(int(std::lround(std::fmin(std::fmax(v, 0.0), 1.0) * 255)), f);
    std::fclose(f);
}

double Psnr(const std::vector<double>& a, const std::vector<double>& b)
{
    double mse = 0;
    for (size_t i = 0; i < a.size(); ++i) mse += (a[i] - b[i]) * (a[i] - b[i]);
    mse /= a.size();
    return 10 * std::log10(1.0 / std::fmax(mse, 1e-12));
}

}  // namespace

int main(int argc, char** argv)
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 4) {
        std::fprintf(stderr, "usage: %s <snippet-dir> <data-dir> <out-dir> [jitter_sign]\n", argv[0]);
        return 2;
    }
    const std::string outDir = argv[3];
    const float jitterSign = argc > 4 ? std::strtof(argv[4], nullptr) : 1.0f;
    const float mvSign = argc > 5 ? std::strtof(argv[5], nullptr) : 1.0f;

    botw_ngx_configure(argv[1], argv[2]);

    // Instance with the extensions NGX asks for.
    const char** instExt = nullptr;
    uint32_t instExtCount = 0;
    int r = botw_ngx_instance_extensions(&instExt, &instExtCount);
    std::printf("NGX instance extensions (%s): %u\n", botw_ngx_result_string(r), instExtCount);
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO, nullptr, "botw-ngx-test", 1, "botw-pc", 1, VK_API_VERSION_1_3};
    VkInstanceCreateInfo ici{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, nullptr, 0, &app, 0, nullptr, instExtCount, instExt};
    VkInstance inst;
    CHECK_VK(vkCreateInstance(&ici, nullptr, &inst));

    uint32_t n = 0;
    vkEnumeratePhysicalDevices(inst, &n, nullptr);
    std::vector<VkPhysicalDevice> devs(n);
    vkEnumeratePhysicalDevices(inst, &n, devs.data());
    for (VkPhysicalDevice d : devs) {
        VkPhysicalDeviceProperties p;
        vkGetPhysicalDeviceProperties(d, &p);
        if (p.vendorID == 0x10DE) {
            g_phys = d;
            std::printf("GPU: %s, driver %u.%u\n", p.deviceName, p.driverVersion >> 22, (p.driverVersion >> 14) & 0xff);
            break;
        }
    }
    if (!g_phys) {
        std::fprintf(stderr, "FAIL no NVIDIA GPU\n");
        return 1;
    }

    vkGetPhysicalDeviceQueueFamilyProperties(g_phys, &n, nullptr);
    std::vector<VkQueueFamilyProperties> qf(n);
    vkGetPhysicalDeviceQueueFamilyProperties(g_phys, &n, qf.data());
    uint32_t family = 0;
    while (!(qf[family].queueFlags & VK_QUEUE_GRAPHICS_BIT)) ++family;

    const char** devExt = nullptr;
    uint32_t devExtCount = 0;
    r = botw_ngx_device_extensions(inst, g_phys, &devExt, &devExtCount);
    std::printf("NGX device extensions (%s):", botw_ngx_result_string(r));
    for (uint32_t i = 0; i < devExtCount; ++i) std::printf(" %s", devExt[i]);
    std::printf("\n");

    VkPhysicalDeviceVulkan12Features f12{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES};
    VkPhysicalDeviceFeatures2 f2{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2, &f12};
    vkGetPhysicalDeviceFeatures2(g_phys, &f2);
    VkPhysicalDeviceVulkan12Features want12{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES};
    want12.bufferDeviceAddress = f12.bufferDeviceAddress;
    float prio = 1;
    VkDeviceQueueCreateInfo qci{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO, nullptr, 0, family, 1, &prio};
    VkDeviceCreateInfo dci{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, &want12, 0, 1, &qci, 0, nullptr, devExtCount, devExt, nullptr};
    CHECK_VK(vkCreateDevice(g_phys, &dci, nullptr, &g_dev));
    VkQueue queue;
    vkGetDeviceQueue(g_dev, family, 0, &queue);

    int available = 0;
    r = botw_ngx_init(inst, g_phys, g_dev, vkGetInstanceProcAddr, vkGetDeviceProcAddr, &available);
    std::printf("NGX init: %s, DLSS SR available: %d\n", botw_ngx_result_string(r), available);
    if (!(r & 1) || !available) {
        std::fprintf(stderr, "FAIL NGX init or DLSS unavailable\n");
        return 1;
    }
    for (int q : {BOTW_NGX_QUALITY_PERFORMANCE, BOTW_NGX_QUALITY_BALANCED, BOTW_NGX_QUALITY_QUALITY, BOTW_NGX_QUALITY_DLAA}) {
        uint32_t w = 0, h = 0;
        botw_ngx_optimal_render_size(kOutW, kOutH, q, &w, &h);
        std::printf("  quality %d: render %ux%u for 1920x1080\n", q, w, h);
    }

    const VkImageUsageFlags inUsage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_STORAGE_BIT;
    Image color = MakeImage(kInW, kInH, VK_FORMAT_R8G8B8A8_UNORM, inUsage);
    Image mv = MakeImage(kInW, kInH, VK_FORMAT_R16G16_SFLOAT, inUsage);
    Image depth = MakeImage(kInW, kInH, VK_FORMAT_R32_SFLOAT, inUsage);
    Image out = MakeImage(kOutW, kOutH, VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT);

    const VkDeviceSize colorSize = kInW * kInH * 4, mvSize = kInW * kInH * 4, depthSize = kInW * kInH * 4;
    Buffer upload = MakeBuffer(colorSize + mvSize + depthSize, VK_BUFFER_USAGE_TRANSFER_SRC_BIT);
    Buffer readback = MakeBuffer(kOutW * kOutH * 4, VK_BUFFER_USAGE_TRANSFER_DST_BIT);

    // Constant inputs: motion vectors (current -> previous position, render pixels) and depth.
    uint16_t* mvData = reinterpret_cast<uint16_t*>(static_cast<uint8_t*>(upload.map) + colorSize);
    float* depthData = reinterpret_cast<float*>(static_cast<uint8_t*>(upload.map) + colorSize + mvSize);
    for (uint32_t i = 0; i < kInW * kInH; ++i) {
        mvData[i * 2] = ToHalf(float(mvSign * kScrollPerFrame * kInW / kOutW));
        mvData[i * 2 + 1] = ToHalf(0);
        depthData[i] = 0.5f;
    }

    VkCommandPoolCreateInfo pci{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO, nullptr, VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT, family};
    VkCommandPool pool;
    CHECK_VK(vkCreateCommandPool(g_dev, &pci, nullptr, &pool));
    VkCommandBufferAllocateInfo cai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO, nullptr, pool, VK_COMMAND_BUFFER_LEVEL_PRIMARY, 1};
    VkCommandBuffer cmd;
    CHECK_VK(vkAllocateCommandBuffers(g_dev, &cai, &cmd));
    VkFenceCreateInfo fci{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    VkFence fence;
    CHECK_VK(vkCreateFence(g_dev, &fci, nullptr, &fence));
    auto submit = [&]() {
        CHECK_VK(vkEndCommandBuffer(cmd));
        VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO};
        si.commandBufferCount = 1;
        si.pCommandBuffers = &cmd;
        CHECK_VK(vkQueueSubmit(queue, 1, &si, fence));
        CHECK_VK(vkWaitForFences(g_dev, 1, &fence, VK_TRUE, UINT64_MAX));
        CHECK_VK(vkResetFences(g_dev, 1, &fence));
    };
    VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};

    CHECK_VK(vkBeginCommandBuffer(cmd, &begin));
    for (const Image* img : {&color, &mv, &depth, &out}) ToGeneral(cmd, img->ngx.image);
    r = botw_ngx_create(cmd, kInW, kInH, kOutW, kOutH, BOTW_NGX_QUALITY_PERFORMANCE, BOTW_NGX_FLAG_MV_LOW_RES);
    std::printf("DLSS create (960x540 -> 1920x1080, Performance): %s\n", botw_ngx_result_string(r));
    if (!(r & 1)) return 1;
    submit();

    uint8_t* colorData = static_cast<uint8_t*>(upload.map);
    int evalFailures = 0;
    for (int frame = 0; frame < kFrames; ++frame) {
        const double scroll = frame * kScrollPerFrame;
        const double jx = Halton(frame + 1, 2) - 0.5, jy = Halton(frame + 1, 3) - 0.5;
        for (uint32_t y = 0; y < kInH; ++y) {
            for (uint32_t x = 0; x < kInW; ++x) {
                // One sample per render pixel at the jittered position, in output-pixel units.
                double v = Pattern((x + 0.5 + jx) * 2.0, (y + 0.5 + jy) * 2.0, scroll);
                uint8_t g = uint8_t(std::lround(v * 255));
                uint8_t* p = colorData + (y * kInW + x) * 4;
                p[0] = p[1] = p[2] = g;
                p[3] = 255;
            }
        }
        CHECK_VK(vkBeginCommandBuffer(cmd, &begin));
        CopyToImage(cmd, upload.buf, 0, color);
        CopyToImage(cmd, upload.buf, colorSize, mv);
        CopyToImage(cmd, upload.buf, colorSize + mvSize, depth);
        Barrier(cmd);
        r = botw_ngx_evaluate(cmd, &color.ngx, &depth.ngx, &mv.ngx, &out.ngx, nullptr, float(jitterSign * jx), float(jitterSign * jy), 1.0f, 1.0f, frame == 0, 16.7f);
        if (!(r & 1)) {
            ++evalFailures;
            std::fprintf(stderr, "evaluate frame %d: %s\n", frame, botw_ngx_result_string(r));
        }
        Barrier(cmd);
        if (frame == kFrames - 1) {
            VkBufferImageCopy c{};
            c.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
            c.imageExtent = {kOutW, kOutH, 1};
            vkCmdCopyImageToBuffer(cmd, out.ngx.image, VK_IMAGE_LAYOUT_GENERAL, readback.buf, 1, &c);
        }
        submit();
    }
    std::printf("DLSS evaluate: %d frames, %d failures\n", kFrames, evalFailures);

    // Reference images for the last frame.
    const double scroll = (kFrames - 1) * kScrollPerFrame;
    std::vector<double> truth(kOutW * kOutH), dlss(kOutW * kOutH), bilinear(kOutW * kOutH), lowres(kInW * kInH);
    for (uint32_t y = 0; y < kOutH; ++y)
        for (uint32_t x = 0; x < kOutW; ++x) {
            double s = 0;
            for (int sy = 0; sy < 4; ++sy)
                for (int sx = 0; sx < 4; ++sx) s += Pattern(x + (sx + 0.5) / 4, y + (sy + 0.5) / 4, scroll);
            truth[y * kOutW + x] = s / 16;
            dlss[y * kOutW + x] = static_cast<uint8_t*>(readback.map)[(y * kOutW + x) * 4] / 255.0;
        }
    for (uint32_t y = 0; y < kInH; ++y)
        for (uint32_t x = 0; x < kInW; ++x) lowres[y * kInW + x] = Pattern((x + 0.5) * 2, (y + 0.5) * 2, scroll);
    for (uint32_t y = 0; y < kOutH; ++y)
        for (uint32_t x = 0; x < kOutW; ++x) {
            double fx = std::fmin(std::fmax((x + 0.5) / 2 - 0.5, 0.0), kInW - 1.001), fy = std::fmin(std::fmax((y + 0.5) / 2 - 0.5, 0.0), kInH - 1.001);
            int x0 = int(fx), y0 = int(fy);
            double ax = fx - x0, ay = fy - y0;
            auto L = [&](int xx, int yy) { return lowres[yy * kInW + xx]; };
            bilinear[y * kOutW + x] = (L(x0, y0) * (1 - ax) + L(x0 + 1, y0) * ax) * (1 - ay) + (L(x0, y0 + 1) * (1 - ax) + L(x0 + 1, y0 + 1) * ax) * ay;
        }

    double pd = Psnr(dlss, truth), pb = Psnr(bilinear, truth);
    std::printf("PSNR vs 4x4-supersampled truth: DLSS %.2f dB, bilinear %.2f dB (jitter sign %+.0f, mv sign %+.0f)\n", pd, pb, jitterSign, mvSign);
    WritePgm(outDir + "/truth.pgm", truth, kOutW, kOutH);
    WritePgm(outDir + "/dlss.pgm", dlss, kOutW, kOutH);
    WritePgm(outDir + "/bilinear.pgm", bilinear, kOutW, kOutH);

    vkDeviceWaitIdle(g_dev);
    botw_ngx_shutdown(g_dev);
    std::printf("NGX shutdown ok\n");

    bool pass = evalFailures == 0 && pd > pb;
    std::printf("%s\n", pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}
