//
//  ExtensionController.m
//  Optimize
//
//  Created by Kornel on 18/04/2015.
//
//

#import "ExtensionController.h"
#import "JobQueue.h"
#import "Job.h"
#import "File.h"
#import "SharedPrefs.h"

@interface ExtensionController ()

@end

@implementation ExtensionController {
    NSURL *tempFilePath;
}

- (NSString *)nibName {
    return @"ExtensionController";
}

- (void)loadView {
    @try {
        [super loadView];

        NSExtensionItem *inputItem = self.extensionContext.inputItems.firstObject;
        NSItemProvider *provider = inputItem.attachments.firstObject;
        NSLog(@"Got %d input items with %d attachments", (int)self.extensionContext.inputItems.count, (int)inputItem.attachments.count);

        if (!inputItem || !provider) {
            NSLog(@"No items sent to the extension, nothing to optimize");
            [self cancel:self];
            return;
        }

        if (![provider hasItemConformingToTypeIdentifier:@"public.image"]) {
            NSLog(@"Invoked on non-image");
            [self cancel:self];
            return;
        }

        NSLog(@"ImageOptim extension loading image");
        tempFilePath = [NSURL fileURLWithPath:[NSTemporaryDirectory() stringByAppendingPathComponent:[[NSUUID UUID] UUIDString]]];
        NSURL *tmpFilePathCopy = tempFilePath;
        [provider loadItemForTypeIdentifier:@"public.image"
                                    options:nil
                          completionHandler:^(id<NSSecureCoding> providedImage, NSError *error) {
                              NSData *data = (NSData *)providedImage;
                              if (![data isKindOfClass:[NSData class]]) {
                                  NSLog(@"Received something that's not NSData");
                                  [self cancel:self];
                                  return;
                              }

                              NSLog(@"Writing to %@", tmpFilePathCopy);
                              if (![data writeToURL:tmpFilePathCopy atomically:NO]) {
                                  NSLog(@"Failed writing %@", tmpFilePathCopy);
                                  [self cancel:self];
                                  return;
                              }

                              dispatch_async(dispatch_get_global_queue(0, 0), ^{
                                  Job *f = [[Job alloc] initWithFilePath:tmpFilePathCopy resultsDatabase:nil];
                                  self.currentFile = f;
                                  NSUserDefaults *defaults = IOSharedPrefs();
                                  [defaults registerDefaults:@{
                                      @"AdvPngEnabled" : @(YES),
                                      @"AdvPngLevel" : @(4),
                                      @"ZopfliEnabled" : @(YES),
                                      @"PngOutRemoveChunks" : @(YES),
                                      @"PreservePermissions" : @(NO),
                                      @"PreserveDates" : @(NO),
                                      @"RunLowPriority" : @(NO),
                                      @"JpegTranEnabled" : @(YES),
                                      @"JpegTranStripAll" : @(YES),
                                      @"GifsicleEnabled" : @(YES),
                                      @"PngMinQuality" : @(70),
                                      @"JpegOptimMaxQuality" : @(80),
                                      @"LossyEnabled" : @(YES),
                                  }];

                                  self.jobQueue = [[JobQueue alloc] initWithCPUs:0 dirs:1 files:1 defaults:defaults];
                                  [self.jobQueue addJob:f];
                                  [self.jobQueue wait];

                                  BOOL optimized = [f isOptimized];
                                  NSString *bestToolName = [f bestToolName];
                                  NSString *statusText = optimized
                                      ? [@"Optimized with " stringByAppendingString:(bestToolName ?: @"")]
                                      : @"Already optimized";

                                  // 这里在后台队列：AppKit 控件必须回主线程更新
                                  dispatch_async(dispatch_get_main_queue(), ^() {
                                      self.status.stringValue = statusText;
                                      self.currentFile = nil;
                                  });

                                  if (optimized) {
                                      NSItemProvider *result = [[NSItemProvider alloc] initWithContentsOfURL:tmpFilePathCopy];
                                      inputItem.attachments = @[ result ];

                                      // 让用户看到结果 1 秒后再关闭窗口。
                                      // 原来是 sleep(1) 阻塞后台线程，改成主线程延迟执行。
                                      dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(1.0 * NSEC_PER_SEC)),
                                                     dispatch_get_main_queue(), ^() {
                                          [self.extensionContext completeRequestReturningItems:@[ inputItem ]
                                                                             completionHandler:^(BOOL res) {
                                                                                 NSLog(@"Returned image %d > %d (%d)", [f byteSizeOriginal].intValue, (int)f.savedOutput.byteSize, (int)res);
                                                                                 [[NSFileManager defaultManager] removeItemAtURL:tmpFilePathCopy error:nil];
                                                                                 self->tempFilePath = nil;
                                                                             }];
                                      });
                                  } else {
                                      NSLog(@"Could not optimize, giving up");
                                      dispatch_async(dispatch_get_main_queue(), ^() {
                                          [self cancel:self];
                                      });
                                  }
                              });
                          }];

    } @catch (NSException *e) {
        NSLog(@"Failed %@", e);
        [self cancel:self];
    }
}

- (IBAction)stop:(id)sender {
    if (self.currentFile && [self.currentFile isStoppable] && [self.currentFile isBusy]) {
        NSLog(@"Stopping");
        [self.currentFile stop];
    } else {
        NSLog(@"User cancelled");
        [self cancel:sender];
    }
}

- (IBAction)cancel:(id)sender {
    NSLog(@"Cancelled");
    if (tempFilePath) {
        [[NSFileManager defaultManager] removeItemAtURL:tempFilePath error:nil];
        tempFilePath = nil;
    }
    NSError *cancelError = [NSError errorWithDomain:NSCocoaErrorDomain code:NSUserCancelledError userInfo:nil];
    [self.extensionContext cancelRequestWithError:cancelError];
}

@end
