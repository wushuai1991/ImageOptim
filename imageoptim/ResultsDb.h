
#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN
/// 「已优化过 / 无法再优化」结果缓存。
/// 哈希为 SHA-256（32 字节 = uint32_t[8]），与 Job 侧的 CC_SHA256 保持一致。
@interface ResultsDb : NSObject {
}
- (BOOL)getResultWithHash:(uint32_t[static 8])hash;
- (void)setUnoptimizableFileHash:(uint32_t[static 8])inputFileHash size:(NSUInteger)byteSizeOnDisk;
@end
NS_ASSUME_NONNULL_END
