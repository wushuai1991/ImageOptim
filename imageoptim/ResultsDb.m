
#import "ResultsDb.h"
#import "log.h"
#include <sqlite3.h>

// 缓存的保留上限。这张表以前只增不删，长期使用会无限膨胀；
// 修剪只会让缓存没命中（多做一次优化），不影响正确性。
static const int kMaxCachedResults = 20000;

// 哈希槽位数：SHA-256 = 32 字节 = 8 × uint32_t
enum { kHashWords = 8 };

// 把哈希拼成 SQLite 的 blob 字面量 x'...'
static NSString *hashHexLiteral(const uint32_t *hash) {
    NSMutableString *hex = [NSMutableString stringWithString:@"x'"];
    for (int i = 0; i < kHashWords; i++) {
        [hex appendFormat:@"%08x", (unsigned int)hash[i]];
    }
    [hex appendString:@"'"];
    return hex;
}

// 注意：这里必须写成内联表达式而不是 helper 函数——
// Release 下 NDEBUG 会把 assert 整个编译掉，helper 会变成未使用函数触发 -Wunused-function。
#define IOAssertHashNonZero(hash) assert((hash)[0] | (hash)[1] | (hash)[2] | (hash)[3] | (hash)[4] | (hash)[5] | (hash)[6] | (hash)[7])

@implementation ResultsDb {
    sqlite3 *db;
    dispatch_queue_t sqlitequeue;
}

typedef BOOL (^rowcallback)(int numColumns, char **values, char **columnNames);

- (instancetype)init {
    if ((self = [super init])) {
        NSURL *cachesPath = [[NSFileManager defaultManager] URLForDirectory:NSCachesDirectory inDomain:NSUserDomainMask appropriateForURL:nil create:YES error:nil];
        NSURL *cachesWithBundlePath = [cachesPath URLByAppendingPathComponent:[[NSBundle mainBundle] bundleIdentifier] isDirectory:YES];
        [[NSFileManager defaultManager] createDirectoryAtURL:cachesWithBundlePath withIntermediateDirectories:YES attributes:nil error:nil];
        if ([[NSFileManager defaultManager] fileExistsAtPath:[cachesPath URLByAppendingPathComponent:@"ImageOptimResults.db"].path]) {
            [[NSFileManager defaultManager] moveItemAtURL:[cachesPath URLByAppendingPathComponent:@"ImageOptimResults.db"] toURL:[cachesWithBundlePath URLByAppendingPathComponent:@"Results.db"] error:nil];
        }
        cachesPath = [cachesWithBundlePath URLByAppendingPathComponent:@"Results.db"];
        IODebug(@"Results cache is in %@", cachesPath.path);
        if (SQLITE_OK != sqlite3_open([cachesPath.path fileSystemRepresentation], &db)) {
            IOWarn(@"Failed to open db: %s", sqlite3_errmsg(db));
            sqlite3_close(db);
            db = NULL;
            return nil;
        }
        sqlitequeue = dispatch_queue_create("imageoptim sqlite", DISPATCH_QUEUE_SERIAL);
        dispatch_async(sqlitequeue, ^() {
            char *err = NULL;

            // pragma 单独一条 exec：万一文件系统不支持 WAL，也不能连带把下面的建表语句一起废掉
            if (SQLITE_OK != sqlite3_exec(self->db, "PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;", NULL, NULL, &err)) {
                IOWarn(@"Failed to set pragmas: %s", err);
                sqlite3_free(err);
                err = NULL;
            }

            if (SQLITE_OK != sqlite3_exec(self->db, "CREATE TABLE IF NOT EXISTS results(inputs_hashsh BLOB(32) NOT NULL PRIMARY KEY, size INT NOT NULL, status INT NOT NULL DEFAULT 0);",
                                          NULL, NULL, &err)) {
                IOWarn(@"Failed to create tables: %s", err);
                sqlite3_free(err);
                err = NULL;
            }

            NSString *prune = [NSString stringWithFormat:
                               @"DELETE FROM results WHERE rowid NOT IN (SELECT rowid FROM results ORDER BY rowid DESC LIMIT %d)", kMaxCachedResults];
            if (SQLITE_OK != sqlite3_exec(self->db, prune.UTF8String, NULL, NULL, &err)) {
                IOWarn(@"Failed to prune results: %s", err);
                sqlite3_free(err);
                err = NULL;
            }
        });
    }
    return self;
}

- (void)dealloc {
    if (db) {
        sqlite3 *dbtmp = db;
        dispatch_sync(sqlitequeue, ^() {
            sqlite3_close(dbtmp);
        });
    }
    sqlitequeue = nil;
}

- (void)setUnoptimizableFileHash:(uint32_t[static 8])hash size:(NSUInteger)byteSizeOnDisk {
    IOAssertHashNonZero(hash);

    NSString *sql = [NSString stringWithFormat:@"INSERT OR REPLACE INTO results(inputs_hashsh, size, status) VALUES(%@, %lu, 1)", hashHexLiteral(hash), (unsigned long)byteSizeOnDisk];
    [self runQuery:sql withBlock:nil];
}

- (BOOL)getResultWithHash:(uint32_t[static 8])hash {
    IOAssertHashNonZero(hash);

    BOOL __block found = NO;
    NSString *sql = [NSString stringWithFormat:@"SELECT 1 FROM results WHERE inputs_hashsh = %@ LIMIT 1", hashHexLiteral(hash)];
    [self runQuery:sql
         withBlock:^BOOL(int c, char **a, char **b) {
             found = YES;
             return YES;
         }];
    return found;
}

static int invokeBlockCallback(void *blockp, int numColumns, char **values, char **columnNames) {
    rowcallback b = (__bridge rowcallback)blockp;
    return !b(numColumns, values, columnNames);
}

- (BOOL)runQuery:(NSString *)query withBlock:(rowcallback)block {
    BOOL __block res = YES;
    dispatch_sync(sqlitequeue, ^() {
        char *err = NULL;
        if (SQLITE_OK != sqlite3_exec(self->db, [query UTF8String], block ? invokeBlockCallback : NULL, (__bridge void *)block, &err)) {
            IOWarn(@"Query failed: %s in %@", err, query);
            sqlite3_free(err);
            res = NO;
        }
    });
    return res;
}

@end
