//
//  File.h
//  ImageOptim
//
//  Created by Kornel on 11/01/2017.
//
//

#import <Foundation/Foundation.h>

@class TempFile;

enum IOFileType {
    FILETYPE_PNG = 1,
    FILETYPE_JPEG,
    FILETYPE_GIF,
    FILETYPE_SVG,
    FILETYPE_BMP,
    FILETYPE_WEBP,
};

NS_ASSUME_NONNULL_BEGIN
@interface File : NSObject {
@public
    enum IOFileType fileType;
}

- (nullable instancetype)initWithData:(NSData *)fileData fromPath:(NSURL *)path;

// 格式转换（PNG/BMP → JPEG）需要用指定类型包装 Workers 产出的临时文件
- (nullable instancetype)initWithType:(enum IOFileType)type size:(NSUInteger)size fromPath:(NSURL *)path;

// This is not copying, but only creates an instance poiting to the new place
// If size is given, it aviods disk accesss
- (nullable File *)copyOfPath:(NSURL *)path;
- (nullable File *)copyOfPath:(NSURL *)path size:(NSUInteger)s;
- (nullable TempFile *)tempCopyOfPath:(NSURL *)path;
- (nullable TempFile *)tempCopyOfPath:(NSURL *)path size:(NSUInteger)s;

- (BOOL)isLarge;
- (BOOL)isSmall;

@property (readonly) NSURL *path;
@property (readonly, assign) NSUInteger byteSize;
@property (readonly, nonatomic, nullable) NSString *mimeType;

+ (NSInteger)byteSize:(NSURL *)afile;

/// 判断 WebP 是否为动画（VP8X 的 animation 标志位，或存在 ANIM/ANMF chunk）。
/// 动画 WebP 无法转成静态 JPEG，转换前需要显式拦住。
+ (BOOL)isAnimatedWebPAtPath:(NSString *)path;

@end
NS_ASSUME_NONNULL_END
