//
//  JpegConvertWorker.h
//  ImageOptim
//
//  把 PNG / BMP 这类无损位图转成 JPEG。
//

#import "Worker.h"

@class Job;

/// 用 ImageIO 在进程内把 PNG / BMP 编码成 JPEG，不依赖任何外部工具。
///
/// 产物类型是 JPEG，因此 Job 保存时会因为「产物类型 ≠ 原图类型」改走
/// 「另存 .jpg」分支，而不是原地覆盖原文件。
@interface JpegConvertWorker : Worker

- (instancetype)initWithQuality:(NSInteger)quality file:(Job *)aJob;

@property (readonly) NSInteger quality;

/// 展示在「最佳工具」列的名字，如 PNG→JPEG / BMP→JPEG
@property (readonly) NSString *toolName;

@end
