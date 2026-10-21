//
//  log.h
//  ImageOptim

extern int hideLogs;

// 用 do { } while (0) 包裹，避免裸 if 造成悬垂 else：
// 原来的 IODebug 展开成 `if (!hideLogs) NSLog(...);`，
// 一旦写成 `if (x) IODebug(...); else ...`，else 会绑到宏内部的 if 上。
#define IODebug(...) do { if (!hideLogs) NSLog(@"" __VA_ARGS__); } while (0)
#define IOWarn(...) do { NSLog(@"" __VA_ARGS__); } while (0)
