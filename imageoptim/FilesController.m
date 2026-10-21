//
//  FilesController.m
//
//  Created by porneL on 23.wrz.07.
//
#import "Backend/Job.h"
#import "FilesController.h"
#import "log.h"
#import "Backend/DirScanner.h"
#import "RevealButtonCell.h"
#import "ResultsDb.h"
#import "JobQueue.h"
#import "JobProxy.h"

@interface FilesController ()

@property (readonly, copy) NSArray *extensions;
@property (assign) BOOL isStoppable;
- (void)updateBusyState;
@end

NSString *const kJobQueueFinished = @"JobQueueFinished";
static NSString *kIMDraggedRowIndexesPboardType = @"com.imageoptim.rows";

@implementation FilesController {
    JobQueue *jobQueue;
    NSLock *queueWaitingLock;

    NSTableView *tableView;
    BOOL isEnabled, isBusy, isStoppable;
    NSInteger nextInsertRow;

    NSMutableDictionary<NSString *, JobProxy *> *jobsByPath;
    ResultsDb *db;
}

@synthesize isBusy, isStoppable;

- (void)configureWithTableView:(NSTableView *)inTableView {
    tableView = inTableView;
    jobsByPath = [NSMutableDictionary new];
    db = [ResultsDb new];

    queueWaitingLock = [NSLock new];

    NSUserDefaults *defs = [NSUserDefaults standardUserDefaults];

    jobQueue = [[JobQueue alloc] initWithCPUs:[defs integerForKey:@"RunConcurrentFiles"]
                                         dirs:[defs integerForKey:@"RunConcurrentDirscans"]
                                        files:[defs integerForKey:@"RunConcurrentFileops"]
                                     defaults:defs];

    [jobQueue addObserver:self forKeyPath:@"isBusy" options:0 context:NULL];

    [tableView registerForDraggedTypes:@[ NSFilenamesPboardType, kIMDraggedRowIndexesPboardType ]];

    [self setSelectsInsertedObjects:NO];

    isEnabled = YES;
}

- (void)observeValueForKeyPath:(NSString *)keyPath ofObject:(id)object
                        change:(NSDictionary *)change
                       context:(void *)context {
    if ([keyPath isEqualToString:@"isBusy"]) {
        [self updateBusyState];
    }
}

- (void)setRow:(NSInteger)row {
    // nextInsertRow 只在主线程读写（setRow: / 拖放 / 粘贴 / addJobObjects:），
    // 断言把这条约定固化下来，避免以后有人在后台线程调进来。
    NSAssert([NSThread isMainThread], @"setRow: must be called on the main thread");
    nextInsertRow = row;
}

- (void)cleanup {
    isEnabled = NO;

    [jobQueue cleanup];

    NSArray *content = [self content];
    [content makeObjectsPerformSelector:@selector(cleanup)];
}

- (NSDragOperation)tableView:(NSTableView *)atableView
                validateDrop:(id<NSDraggingInfo>)info
                 proposedRow:(NSInteger)row
       proposedDropOperation:(NSTableViewDropOperation)operation {
    if (!isEnabled) {
        return NSDragOperationNone;
    }

    NSDragOperation dragOp = ([info draggingSource] == tableView) ? NSDragOperationMove : NSDragOperationCopy;
    [atableView setDropRow:row dropOperation:NSTableViewDropAbove];

    return dragOp;
}

- (BOOL)tableView:(NSTableView *)aTableView writeRowsWithIndexes:(NSIndexSet *)rowIndexes toPasteboard:(NSPasteboard *)pboard {
    if (!isEnabled) return NO;

    NSArray *fileUrls = [[[self arrangedObjects] objectsAtIndexes:rowIndexes] valueForKey:@"filePath"];

    NSUInteger count = [fileUrls count];
    if (count) {
        NSArray *types = @[ NSFilenamesPboardType, kIMDraggedRowIndexesPboardType ];
        if (count == 1) {
            types = [types arrayByAddingObject:NSURLPboardType];
        }
        [pboard declareTypes:types owner:nil];
        if (count == 1) {
            [[fileUrls firstObject] writeToPasteboard:pboard];
        }
        return [pboard setPropertyList:[fileUrls valueForKey:@"path"] forType:NSFilenamesPboardType] &&
               [pboard setData:[NSKeyedArchiver archivedDataWithRootObject:rowIndexes]
                       forType:kIMDraggedRowIndexesPboardType];
    }
    return NO;
}

- (void)removeObjects:(NSArray *)objects {
    // 索引持有 JobProxy 强引用，移除时必须同步摘掉，否则路径会一直被判定为"已添加"
    @synchronized(self) {
        for (JobProxy *f in objects) {
            NSString *pathString = f.filePath.path;
            if (pathString && jobsByPath[pathString] == f) {
                [jobsByPath removeObjectForKey:pathString];
            }
        }
    }

    [super removeObjects:objects];
    [objects makeObjectsPerformSelector:@selector(cleanup)];
}

- (NSString *)tableView:(NSTableView *)aTableView toolTipForCell:(NSCell *)aCell rect:(NSRectPointer)rect tableColumn:(NSTableColumn *)aTableColumn row:(int)row mouseLocation:(NSPoint)mouseLocation {
    NSArray *objs = [self arrangedObjects];
    if (row < (signed)[objs count]) {
        JobProxy *f = objs[row];
        assert([f isKindOfClass:[JobProxy class]]);

        if ([aCell isKindOfClass:[RevealButtonCell class]]) {
            NSRect infoButtonRect = [((RevealButtonCell *)aCell) infoButtonRectForBounds:*rect];

            BOOL mouseIsInside = NSMouseInRect(mouseLocation, infoButtonRect, [aTableView isFlipped]);
            if (mouseIsInside) {
                return f.filePath.path;
            }
        }

        return [f statusText];
    }
    return nil;
}

// Better in NSArrayController class
- (NSUInteger)rowsAboveRow:(NSUInteger)row inIndexSet:(NSIndexSet *)indexSet {
    NSUInteger currentIndex = [indexSet firstIndex];
    NSUInteger i = 0;
    while (currentIndex != NSNotFound) {
        if (currentIndex < row) {
            i++;
        }
        currentIndex = [indexSet indexGreaterThanIndex:currentIndex];
    }
    return i;
}
- (NSUInteger)numberOfRowsInTableView:(NSTableView *)tableview {
    return [[self arrangedObjects] count];
}

- (void)moveObjectsInArrangedObjectsFromIndexes:(NSIndexSet *)indexSet
                                        toIndex:(NSUInteger)insertIndex {
    NSArray *objects = [self arrangedObjects];
    NSUInteger idx = [indexSet lastIndex];

    NSUInteger aboveInsertIndexCount = 0;
    id object;
    NSUInteger removeIndex;

    while (NSNotFound != idx) {
        if (idx >= insertIndex) {
            removeIndex = idx + aboveInsertIndexCount;
            aboveInsertIndexCount += 1;
        } else {
            removeIndex = idx;
            insertIndex -= 1;
        }
        object = objects[removeIndex];
        [self removeObjectAtArrangedObjectIndex:removeIndex];
        [self insertObject:object atArrangedObjectIndex:insertIndex];

        idx = [indexSet indexLessThanIndex:idx];
    }
}

- (BOOL)tableView:(NSTableView *)aTableView acceptDrop:(id<NSDraggingInfo>)info row:(NSInteger)row dropOperation:(NSTableViewDropOperation)operation {
    NSPasteboard *pboard = [info draggingPasteboard];
    NSData *indexesArchived;

    if ([info draggingSource] == aTableView && (indexesArchived = [pboard dataForType:kIMDraggedRowIndexesPboardType])) {
        NSIndexSet *indexSet = [NSKeyedUnarchiver unarchiveObjectWithData:indexesArchived];

        NSIndexSet *selection = [self selectionIndexes];
        BOOL containsSelection = [selection containsIndexes:indexSet];

        [self moveObjectsInArrangedObjectsFromIndexes:indexSet toIndex:row];

        if (containsSelection) {
            NSUInteger rowsAbove = [self rowsAboveRow:row inIndexSet:indexSet];

            NSRange range = NSMakeRange(row - rowsAbove, [indexSet count]);
            indexSet = [NSIndexSet indexSetWithIndexesInRange:range];
            [self setSelectionIndexes:indexSet];
        } else if (![selection count]) {
            // non-empty selection seems to be preserved, but if there was no selection
            // then tableview selects a row
            [self setSelectedObjects:@[]];
        }

        return YES;
    } else {
        NSArray *paths = [pboard propertyListForType:NSFilenamesPboardType];
        [self setRow:row];
        [self performSelectorInBackground:@selector(addPaths:) withObject:paths];
    }

    [[aTableView window] makeKeyAndOrderFront:aTableView];

    return YES;
}

/** 按路径索引查已添加的文件，O(1)。
    早先用 NSHashTable 做存在性判断再线性扫描 content，批量添加时是 O(n²)。
 */
- (JobProxy *)findFileByPath:(NSURL *)path {
    NSString *pathString = path.path;
    if (!pathString) {
        return nil;
    }

    @synchronized(self) {
        return jobsByPath[pathString];
    }
}

- (void)addJobObjects:(NSArray<JobProxy *> *)files {
    // 空批次直接返回：否则会白注册一条 undo，还会动一次列表
    if (![files count]) {
        return;
    }

    NSAssert([NSThread isMainThread], @"addJobObjects: must be called on the main thread");

    [[tableView undoManager] registerUndoWithTarget:self selector:@selector(removeObjects:) object:files];
    [[tableView undoManager] setActionName:NSLocalizedString(@"Add", @"undo command name")];

    @synchronized(self) {
        NSInteger count = (NSInteger)[[self arrangedObjects] count];

        if (nextInsertRow < 0 || nextInsertRow >= count) {
            [self addObjects:files];
            // 追加后把插入位置同步到队尾，避免留下一个越界的旧值
            nextInsertRow = (NSInteger)[[self arrangedObjects] count];
        } else {
            NSIndexSet *set = [NSIndexSet indexSetWithIndexesInRange:NSMakeRange((NSUInteger)nextInsertRow, [files count])];
            [self insertObjects:files atArrangedObjectIndexes:set];
            nextInsertRow += (NSInteger)[files count];
        }
    }
}

- (void)addURLsBelowSelection:(NSArray<NSURL *> *)paths {
    [self setRow:[self selectionIndex]];
    [self performSelectorInBackground:@selector(addURLs:) withObject:paths];
}

- (BOOL)addPaths:(NSArray<NSString *> *)paths {
    NSMutableArray *urls = [NSMutableArray arrayWithCapacity:[paths count]];
    for (NSString *path in paths) {
        [urls addObject:[NSURL fileURLWithPath:path]];
    }

    return [self addURLs:urls filesOnly:NO];
}

- (BOOL)addURLs:(NSArray<NSURL *> *)paths {
    return [self addURLs:paths filesOnly:NO];
}

/** filesOnly indicates that paths do not contain any directories or symlinks */
- (BOOL)addURLs:(NSArray<NSURL *> *)paths filesOnly:(BOOL)filesOnly {
    if (!isEnabled || !paths) {
        return NO;
    }
    if (![paths isKindOfClass:[NSArray class]]) {
        IOWarn("bad paths obj %@", paths);
        return NO;
    }

    NSMutableArray<JobProxy *> *toAdd = [NSMutableArray arrayWithCapacity:[paths count]];

    BOOL allOK = YES;
    NSFileManager *fm = filesOnly ? nil : [NSFileManager defaultManager];

    for (NSURL *relpath in paths) {
        NSURL *path;
        BOOL isDir = NO;

        if (!fm) {
            path = relpath;
        } else {
            path = [relpath URLByResolvingSymlinksInPath];

            if (![fm fileExistsAtPath:path.path isDirectory:&isDir]) {
                IOWarn("%@ doesn't exist", path.path);
                allOK = NO;
                continue;
            }
        }

        if (!isDir) {
            // 查重与登记必须原子完成，否则并发添加同一路径会建出两个 Job
            @synchronized(self) {
                NSString *pathString = path.path;
                JobProxy *old = pathString ? jobsByPath[pathString] : nil;
                if (old) {
                    if (![old isBusy]) {
                        [jobQueue addJob:old.job];
                    }
                } else {
                    Job *f = [[Job alloc] initWithFilePath:path resultsDatabase:db];
                    JobProxy *jobProxy = [[JobProxy alloc] initWithJob:f];
                    if (pathString) {
                        jobsByPath[pathString] = jobProxy;
                    }
                    [toAdd addObject:jobProxy];
                    [jobQueue addJob:f];
                }
            }
        } else {
            DirScanner *w = [[DirScanner alloc] initWithPath:path filesController:self extensions:[self extensions]];
            [jobQueue addDirScanner:w];
        }
    }

    [self performSelectorOnMainThread:@selector(addJobObjects:) withObject:toAdd waitUntilDone:NO];

    return allOK;
}

- (NSNumber *)queueCount {
    return [jobQueue queueCount];
}

- (void)stopSelected {
    for (JobProxy *f in self.selectedObjects) {
        [f stop];
    }
    [self updateStoppableState];
}

- (void)revert {
    BOOL beep = NO;
    NSArray *array = [self selectedObjects];
    for (JobProxy *f in array) {
        if (![f revert]) {
            beep = YES;
        }
    }
    if (beep) {
        NSBeep();
    }
}

- (BOOL)canRevert {
    NSArray *array = [self selectedObjects];
    for (JobProxy *f in array) {
        if ([f canRevert]) {
            return YES;
        }
    }
    return NO;
}

- (BOOL)canClearComplete {
    for (JobProxy *f in [self arrangedObjects]) {
        if (f.isDone) {
            return YES;
        }
    }
    return NO;
}

- (void)clearComplete {
    @synchronized(self) {
        // 走 removeObjects: 统一出口，保证 jobsByPath 索引同步清理
        NSMutableArray *done = [NSMutableArray new];
        for (JobProxy *f in [self arrangedObjects]) {
            if (f.isDone) {
                [done addObject:f];
            }
        }
        if ([done count]) {
            [self removeObjects:done];
        }
    }
    [self setRow:-1];
}

- (BOOL)canStartAgainOptimized:(BOOL)optimized {
    NSArray *array = [self selectedObjects];
    if (![array count]) {
        array = [self content];
    }

    for (JobProxy *f in array) {
        if (!f.isBusy && (!optimized || f.isOptimized)) {
            return YES;
        }
    }
    return NO;
}

- (void)startAgainOptimized:(BOOL)optimized {
    BOOL anyStarted = NO;
    @synchronized(self) {
        NSArray<JobProxy *> *jobs = [self selectedObjects];
        NSInteger selectionCount = [jobs count];

        // UI doesn't give a way to deselect all, so here's a substitute
        // when selecting "again" on file that doesn't need it, deselect
        if (1 == selectionCount) {
            JobProxy *job = jobs[0];
            if (job.isBusy || !job.isOptimized) {
                jobs = [jobs copy];
                [self setSelectedObjects:@[]];
            }
        } else if (!selectionCount) {
            jobs = [self content];
        }

        for (JobProxy *f in jobs) {
            if (!f.isBusy && (!optimized || f.isOptimized)) {
                [jobQueue addJob:f.job];
                anyStarted = YES;
            }
        }
    }

    if (!anyStarted) NSBeep();
}

- (void)updateBusyState {
    BOOL currentlyBusy = [jobQueue isBusy];

    if (isBusy != currentlyBusy) {
        [self willChangeValueForKey:@"isBusy"];
        isBusy = currentlyBusy;
        [self didChangeValueForKey:@"isBusy"];

        [self updateStoppableState];
    }

    if (!currentlyBusy) {
        [[NSNotificationCenter defaultCenter] postNotificationName:kJobQueueFinished object:self];
    }
}

- (void)updateStoppableState {
    if (isBusy) {
        NSArray *array = [self selectedObjects];
        for (JobProxy *f in array) {
            if ([f isStoppable]) {
                self.isStoppable = YES;
                return;
            }
        }
    }
    self.isStoppable = NO;
}

#define PNG_ENABLED 1
#define JPEG_ENABLED 2
#define GIF_ENABLED 4
#define SVG_ENABLED 8

- (int)typesEnabled {
    int types = 0;
    NSUserDefaults *defs = [NSUserDefaults standardUserDefaults];

    if ([defs boolForKey:@"PngCrush2Enabled"] || [defs boolForKey:@"PngOutEnabled"] ||
        [defs boolForKey:@"OptiPngEnabled"] || [defs boolForKey:@"AdvPngEnabled"] || [defs boolForKey:@"ZopfliEnabled"]) {
        types |= PNG_ENABLED;
    }

    if ([defs boolForKey:@"JpegOptimEnabled"] || [defs boolForKey:@"JpegTranEnabled"]) {
        types |= JPEG_ENABLED;
    }

    if ([defs boolForKey:@"GifsicleEnabled"]) {
        types |= GIF_ENABLED;
    }

    if ([defs boolForKey:@"SvgoEnabled"] || [defs boolForKey:@"SvgcleanerEnabled"]) {
        types |= SVG_ENABLED;
    }

    if (!types) types = PNG_ENABLED; // will show error in the list
    return types;
}

- (NSArray *)extensions {
    int types = [self typesEnabled];
    NSMutableArray *extensions = [NSMutableArray array];

    if (types & PNG_ENABLED) {
        [extensions addObject:@"png"];
        [extensions addObject:@"PNG"];
    }
    if (types & JPEG_ENABLED) {
        [extensions addObjectsFromArray:@[ @"jpg", @"JPG", @"jpeg", @"JPEG" ]];
    }
    if (types & GIF_ENABLED) {
        [extensions addObject:@"gif"];
        [extensions addObject:@"GIF"];
    }
    if (types & SVG_ENABLED) {
        [extensions addObject:@"svg"];
    }

    return extensions;
}

- (NSArray *)fileTypes {
    int types = [self typesEnabled];

    NSMutableArray *fileTypes = [NSMutableArray array];

    if (types & PNG_ENABLED) {
        [fileTypes addObjectsFromArray:@[ @"png", @"PNG", NSFileTypeForHFSTypeCode('PNGf'), @"public.png", @"image/png" ]];
    }
    if (types & JPEG_ENABLED) {
        [fileTypes addObjectsFromArray:@[ @"jpg", @"jpeg", @"JPG", @"JPEG", NSFileTypeForHFSTypeCode('JPEG'), @"public.jpeg", @"image/jpeg" ]];
    }
    if (types & GIF_ENABLED) {
        [fileTypes addObjectsFromArray:@[ @"gif", @"GIF", NSFileTypeForHFSTypeCode('GIFf'), @"public.gif", @"image/gif" ]];
    }
    if (types & SVG_ENABLED) {
        [fileTypes addObjectsFromArray:@[ @"svg", @"public.svg-image", @"image/svg" ]];
    }
    return fileTypes;
}

@end
