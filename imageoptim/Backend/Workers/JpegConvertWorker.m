//
//  JpegConvertWorker.m
//  ImageOptim
//

#import "JpegConvertWorker.h"
#import "Job.h"
#import "File.h"
#import "TempFile.h"
#import "../../log.h"
#import <ImageIO/ImageIO.h>
#import <CoreGraphics/CoreGraphics.h>
#include <unistd.h>

@implementation JpegConvertWorker

- (instancetype)initWithQuality:(NSInteger)quality file:(Job *)aJob {
    if (self = [super initWithFile:aJob]) {
        _quality = MAX(1, MIN(100, quality));
    }
    return self;
}

- (NSInteger)settingsIdentifier {
    return 9000 + _quality;
}

// 同样的输入每次都会得到同样的结果，但「转换」不是原地优化：
// 不能靠 wipInput 体积与上次相同就跳过，否则重跑时会误判为无需处理。
- (BOOL)isIdempotent {
    return NO;
}

- (NSString *)toolName {
    switch (self.job.wipInput->fileType) {
        case FILETYPE_BMP: return @"BMP→JPEG";
        case FILETYPE_WEBP: return @"WebP→JPEG";
        default: return @"PNG→JPEG";
    }
}

- (NSURL *)tempPath {
    static int uid = 0;
    static dispatch_once_t once;
    dispatch_once(&once, ^{ uid = getpid() << 12; });
    NSString *filename = [NSString stringWithFormat:@"ImageOptim.%@.%x.%x.jpg",
                          [self className], (unsigned int)([Job hash] ^ [self hash]), uid++];
    return [NSURL fileURLWithPath:[NSTemporaryDirectory() stringByAppendingPathComponent:filename]];
}

- (void)run {
    File *input = self.job.wipInput;
    if (!input || [self isCancelled]) {
        return;
    }

    // 动画 WebP 转成静态 JPEG 会静默丢掉动效，直接跳过并说明原因
    if (input->fileType == FILETYPE_WEBP && [File isAnimatedWebPAtPath:input.path.path]) {
        [self.job setError:NSLocalizedString(@"Animated WebP is not supported", @"tooltip")];
        return;
    }

    NSURL *tempPath = [self tempPath];
    BOOL converted = NO;
    @try {
        converted = [self convertURL:input.path toURL:tempPath];
    } @catch (NSException *e) {
        IOWarn(@"%@ failed: %@: %@", [self className], [e name], e);
    }

    if (!converted || [self isCancelled]) {
        [[NSFileManager defaultManager] removeItemAtURL:tempPath error:nil];
        return;
    }

    NSUInteger size = [File byteSize:tempPath];
    TempFile *output = [[TempFile alloc] initWithType:FILETYPE_JPEG size:size fromPath:tempPath];
    if (!output) {
        [[NSFileManager defaultManager] removeItemAtURL:tempPath error:nil];
        return;
    }

    // 转换结果一律采纳：WebP/BMP 换成 JPEG 后体积变大也正常（换格式才是目的）
    [self.job setConversionOutput:output toolName:[self toolName]];
}

/// PNG/BMP/WebP → JPEG。系统解码器（ImageIO）打不开时退到 dwebp。
- (BOOL)convertURL:(NSURL *)srcURL toURL:(NSURL *)dstURL {
    CGImageSourceRef source = CGImageSourceCreateWithURL((__bridge CFURLRef)srcURL, NULL);
    if (source) {
        BOOL ok = [self encodeSource:source toURL:dstURL];
        CFRelease(source);
        if (ok) {
            return YES;
        }
    } else {
        IOWarn(@"System decoder can't open %@ (WebP needs macOS 11+, or the file is malformed)", srcURL.path);
    }

    NSURL *decodedPNG = [self decodeWithDwebp:srcURL];
    if (!decodedPNG) {
        return NO;
    }

    BOOL ok = NO;
    CGImageSourceRef pngSource = CGImageSourceCreateWithURL((__bridge CFURLRef)decodedPNG, NULL);
    if (pngSource) {
        ok = [self encodeSource:pngSource toURL:dstURL];
        CFRelease(pngSource);
    } else {
        IOWarn(@"Can't reopen dwebp output %@", decodedPNG.path);
    }

    [[NSFileManager defaultManager] removeItemAtURL:decodedPNG error:nil];
    return ok;
}

/// JPEG 没有透明通道，先把图画到白底上，避免透明区域变成黑色。
- (BOOL)encodeSource:(CGImageSourceRef)source toURL:(NSURL *)dstURL {
    CGImageRef image = CGImageSourceCreateImageAtIndex(source, 0, NULL);
    if (!image) {
        IOWarn(@"Can't decode image data for %@", dstURL.path);
        return NO;
    }

    CFDictionaryRef properties = CGImageSourceCopyPropertiesAtIndex(source, 0, NULL);

    size_t width = CGImageGetWidth(image);
    size_t height = CGImageGetHeight(image);

    CGImageRef flattened = NULL;
    CGColorSpaceRef colorSpace = CGColorSpaceCreateDeviceRGB();
    CGContextRef context = NULL;
    if (width && height && colorSpace) {
        // 两个枚举分属 CGImageAlphaInfo 与 CGBitmapInfo，clang 21 起对混用报 -Wenum-enum-conversion，需显式转换
        context = CGBitmapContextCreate(NULL, width, height, 8, 0, colorSpace,
                                        (CGBitmapInfo)kCGImageAlphaNoneSkipLast | kCGBitmapByteOrderDefault);
    }

    if (context) {
        CGContextSetRGBFillColor(context, 1.0, 1.0, 1.0, 1.0);
        CGContextFillRect(context, CGRectMake(0, 0, width, height));
        CGContextSetInterpolationQuality(context, kCGInterpolationHigh);
        CGContextDrawImage(context, CGRectMake(0, 0, width, height), image);
        flattened = CGBitmapContextCreateImage(context);
        CGContextRelease(context);
    }

    if (colorSpace) {
        CGColorSpaceRelease(colorSpace);
    }

    BOOL ok = NO;
    CGImageDestinationRef destination = NULL;
    if (flattened) {
        destination = CGImageDestinationCreateWithURL((__bridge CFURLRef)dstURL, CFSTR("public.jpeg"), 1, NULL);
    }

    if (destination) {
        CFMutableDictionaryRef options = CFDictionaryCreateMutable(NULL, 0,
                                                                  &kCFTypeDictionaryKeyCallBacks,
                                                                  &kCFTypeDictionaryValueCallBacks);
        double quality = (double)_quality / 100.0;
        CFNumberRef qualityNumber = CFNumberCreate(NULL, kCFNumberDoubleType, &quality);
        CFDictionarySetValue(options, kCGImageDestinationLossyCompressionQuality, qualityNumber);
        CFRelease(qualityNumber);

        // 只保留 DPI：PNG/BMP/WebP 里可能带 eXIf 方向信息，直接透传会与已绘制的像素双重旋转
        if (properties) {
            CFTypeRef dpiWidth = CFDictionaryGetValue(properties, kCGImagePropertyDPIWidth);
            CFTypeRef dpiHeight = CFDictionaryGetValue(properties, kCGImagePropertyDPIHeight);
            if (dpiWidth) {
                CFDictionarySetValue(options, kCGImagePropertyDPIWidth, dpiWidth);
            }
            if (dpiHeight) {
                CFDictionarySetValue(options, kCGImagePropertyDPIHeight, dpiHeight);
            }
        }

        CGImageDestinationAddImage(destination, flattened, options);
        ok = CGImageDestinationFinalize(destination);
        if (!ok) {
            IOWarn(@"Failed to encode JPEG at %@", dstURL.path);
        }
        CFRelease(options);
        CFRelease(destination);
    }

    if (flattened) {
        CGImageRelease(flattened);
    }
    if (properties) {
        CFRelease(properties);
    }
    CGImageRelease(image);

    return ok;
}

/// 系统解码器不可用时的兜底：调 libwebp 的 dwebp 解成 PNG，再交给 ImageIO 编码。
- (nullable NSURL *)decodeWithDwebp:(NSURL *)srcURL {
    NSString *executable = [self dwebpPath];
    if (!executable) {
        IOWarn(@"Can't decode %@: no WebP support in this macOS version and dwebp is not installed", srcURL.path);
        return nil;
    }

    NSString *name = [NSString stringWithFormat:@"ImageOptim.dwebp.%x.%u.png",
                      (unsigned int)[self hash], arc4random_uniform(1000000)];
    NSURL *outURL = [NSURL fileURLWithPath:[NSTemporaryDirectory() stringByAppendingPathComponent:name]];

    NSTask *task = [NSTask new];
    task.launchPath = executable;
    task.arguments = @[ srcURL.path, @"-o", outURL.path ];

    NSPipe *pipe = [NSPipe pipe];
    task.standardOutput = pipe;
    task.standardError = pipe;

    @try {
        [task launch];
        [task waitUntilExit];
    } @catch (NSException *e) {
        IOWarn(@"dwebp failed to start for %@: %@", srcURL.path, e);
        return nil;
    }

    if (task.terminationStatus != 0 || [File byteSize:outURL] == 0) {
        IOWarn(@"dwebp exited with %d for %@", task.terminationStatus, srcURL.path);
        [[NSFileManager defaultManager] removeItemAtURL:outURL error:nil];
        return nil;
    }

    return outURL;
}

- (nullable NSString *)dwebpPath {
    NSFileManager *fm = [NSFileManager defaultManager];

    NSString *bundled = [[NSBundle bundleForClass:[self class]] pathForAuxiliaryExecutable:@"dwebp"];
    if (bundled && [fm isExecutableFileAtPath:bundled]) {
        return bundled;
    }

    for (NSString *candidate in @[ @"/opt/homebrew/bin/dwebp", @"/usr/local/bin/dwebp" ]) {
        if ([fm isExecutableFileAtPath:candidate]) {
            return candidate;
        }
    }

    return nil;
}

@end
