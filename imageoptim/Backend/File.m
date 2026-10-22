//
//  File.m
//  ImageOptim
//
//  Created by Kornel on 11/01/2017.
//
//

#import "File.h"
#import "TempFile.h"
#import "../log.h"
#import <assert.h>

@implementation File

- (nullable instancetype)initWithType:(enum IOFileType)type size:(NSUInteger)size fromPath:(NSURL *)aPath {
    if (!size) {
        return nil;
    }

    if ((self = [super init])) {
        _path = aPath;
        _byteSize = size;
        fileType = type;
    }
    return self;
}

-(instancetype)initWithData:(NSData *)fileData fromPath:(NSURL *)aPath {
    const unsigned char pngheader[] = {0x89,0x50,0x4e,0x47,0x0d,0x0a};
    const unsigned char jpegheader[] = {0xff,0xd8,0xff};
    const unsigned char gifheader[] = {0x47,0x49,0x46,0x38};
    const unsigned char svgheader[] = {'<','s','v','g'};
    const unsigned char bmpheader[] = {'B','M'};
    const unsigned char riffheader[] = {'R','I','F','F'};
    const unsigned char webpheader[] = {'W','E','B','P'};
    // WebP 需要看 RIFF(0-3) 和 WEBP(8-11) 两段，所以头要从 6 字节扩到 12
    enum { headerSize = 12 };
    char fileHeaderBytes[headerSize];

    if (!fileData || fileData.length < 6) {
        return nil;
    }

    memset(fileHeaderBytes, 0, headerSize);
    NSUInteger headerRead = MIN(fileData.length, headerSize);
    [fileData getBytes:fileHeaderBytes length:headerRead];

    enum IOFileType type = 0;

    if (0 == memcmp(fileHeaderBytes, pngheader, sizeof(pngheader))) {
        type = FILETYPE_PNG;
    } else if (0 == memcmp(fileHeaderBytes, jpegheader, sizeof(jpegheader))) {
        type = FILETYPE_JPEG;
    } else if (0 == memcmp(fileHeaderBytes, gifheader, sizeof(gifheader))) {
        type = FILETYPE_GIF;
    } else if (0 == memcmp(fileHeaderBytes, svgheader, sizeof(svgheader)) || [aPath.pathExtension isEqualToString:@"svg"]) {
        type = FILETYPE_SVG;
    } else if (0 == memcmp(fileHeaderBytes, bmpheader, sizeof(bmpheader))) {
        type = FILETYPE_BMP;
    } else if (headerRead >= headerSize &&
               0 == memcmp(fileHeaderBytes, riffheader, sizeof(riffheader)) &&
               0 == memcmp(fileHeaderBytes + 8, webpheader, sizeof(webpheader))) {
        type = FILETYPE_WEBP;
    }

    return [self initWithType:type size:fileData.length fromPath:aPath];
}

- (nullable File *)copyOfPath:(NSURL *)path {
    return [[File alloc] initWithType:fileType size:[File byteSize:path] fromPath:path];
}

- (nullable File *)copyOfPath:(NSURL *)path size:(NSUInteger)s {
    return [[File alloc] initWithType:fileType size:s fromPath:path];
}

- (nullable TempFile *)tempCopyOfPath:(NSURL *)path {
    return [[TempFile alloc] initWithType:fileType size:[File byteSize:path] fromPath:path];
}

- (nullable TempFile *)tempCopyOfPath:(NSURL *)path size:(NSUInteger)s {
    if (!s) {
        return nil;
    }

    NSUInteger actualSize = [File byteSize:path];
    if (s != actualSize) {
        IOWarn(@"Expected size %lu, but file is actually %lu", (unsigned long)s, (unsigned long)actualSize);
        return nil;
    }
    return [[TempFile alloc] initWithType:fileType size:s fromPath:path];
}

- (BOOL)isLarge {
    if (fileType == FILETYPE_PNG) {
        return _byteSize > 250 * 1024;
    }
    return _byteSize > 1 * 1024 * 1024;
}

- (BOOL)isSmall {
    if (fileType == FILETYPE_PNG) {
        return _byteSize < 2048;
    }
    return _byteSize < 10 * 1024;
}

+ (NSInteger)byteSize:(NSURL *)afile {
    NSNumber *value = nil;
    NSError *err = nil;
    if ([afile getResourceValue:&value forKey:NSURLFileSizeKey error:&err] && value) {
        return [value integerValue];
    }
    IOWarn("Could not stat %@: %@", afile.path, err);
    return 0;
}

- (nullable NSString *)mimeType {
    switch (fileType) {
        case FILETYPE_PNG: return @"image/png";
        case FILETYPE_JPEG: return @"image/jpeg";
        case FILETYPE_GIF: return @"image/gif";
        case FILETYPE_SVG: return @"image/svg";
        case FILETYPE_BMP: return @"image/bmp";
        case FILETYPE_WEBP: return @"image/webp";
        default:
            return nil;
    }
}

+ (BOOL)isAnimatedWebPAtPath:(NSString *)path {
    NSFileHandle *handle = [NSFileHandle fileHandleForReadingAtPath:path];
    if (!handle) {
        return NO;
    }

    BOOL animated = NO;
    @try {
        NSData *header = [handle readDataOfLength:12];
        if (header.length < 12) {
            return NO;
        }

        const unsigned char *head = header.bytes;
        if (memcmp(head, "RIFF", 4) != 0 || memcmp(head + 8, "WEBP", 4) != 0) {
            return NO;
        }

        // RIFF chunk 都是 8 字节头 + 载荷，逐个走；只扫文件头部 64KB，避免为判断读整个文件
        NSData *body = [handle readDataOfLength:64 * 1024];
        const unsigned char *p = body.bytes;
        NSUInteger length = body.length;
        NSUInteger offset = 0;

        while (offset + 8 <= length) {
            const unsigned char *chunk = p + offset;

            if (memcmp(chunk, "ANIM", 4) == 0 || memcmp(chunk, "ANMF", 4) == 0) {
                animated = YES;
                break;
            }

            // VP8X 的首字节是标志位，bit1 = animation
            if (memcmp(chunk, "VP8X", 4) == 0 && offset + 8 < length && (chunk[8] & 0x02)) {
                animated = YES;
                break;
            }

            uint32_t chunkSize = (uint32_t)chunk[4] | ((uint32_t)chunk[5] << 8)
                               | ((uint32_t)chunk[6] << 16) | ((uint32_t)chunk[7] << 24);
            NSUInteger advance = 8 + (NSUInteger)chunkSize + (chunkSize & 1u);
            if (advance <= 8) {
                break;
            }
            offset += advance;
        }
    } @finally {
        [handle closeFile];
    }

    return animated;
}

@end
