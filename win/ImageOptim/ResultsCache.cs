using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImageOptim;

/// <summary>
/// 结果缓存：记录「无法进一步优化」的文件哈希，避免重复劳动。
/// 对应 macOS 原版的 <c>ResultsDb</c>（SQLite），此处用 JSON 文件简化实现。
/// </summary>
public sealed class ResultsCache
{
    private static readonly string CacheDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageOptim");
    private static readonly string CachePath = System.IO.Path.Combine(CacheDir, "results.json");

    private readonly object _lock = new();
    private readonly HashSet<string> _unoptimizable = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public ResultsCache()
    {
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(CachePath))
                {
                    var json = File.ReadAllText(CachePath);
                    var hashes = JsonSerializer.Deserialize<List<string>>(json);
                    if (hashes != null)
                        foreach (var h in hashes)
                            _unoptimizable.Add(h);
                }
            }
            catch
            {
                // 忽略加载失败
            }
        }
    }

    /// <summary>计算输入文件哈希（与设置哈希组合，原版用 MD5）。流式读取，避免大文件整体载入内存。</summary>
    public static string ComputeHash(byte[] settingsHash, string filePath)
    {
        using var md5 = MD5.Create();
        md5.TransformBlock(settingsHash, 0, settingsHash.Length, null, 0);

        using (var fs = File.OpenRead(filePath))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                md5.TransformBlock(buffer, 0, read, null, 0);
        }

        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(md5.Hash!);
    }

    public bool HasResult(string hash)
    {
        lock (_lock)
            return _unoptimizable.Contains(hash);
    }

    /// <summary>标记一个哈希为"无法进一步优化"。异步节流写盘，避免同步 IO 阻塞调用方。</summary>
    public void MarkUnoptimizable(string hash)
    {
        bool added;
        lock (_lock)
        {
            added = _unoptimizable.Add(hash);
        }
        if (added)
            ScheduleSave();
    }

    // 节流：不管入队多少次，1 秒内只写盘一次
    private readonly object _saveLock = new();
    private bool _savePending;

    private void ScheduleSave()
    {
        lock (_saveLock)
        {
            if (_savePending) return;
            _savePending = true;
        }
        Task.Run(async () =>
        {
            await Task.Delay(1000);
            lock (_saveLock) { _savePending = false; }
            SaveSnapshot();
        });
    }

    /// <summary>立即将缓存快照写入磁盘（用于程序退出前刷盘）。</summary>
    public void Flush() => SaveSnapshot();

    private void SaveSnapshot()
    {
        List<string> snapshot;
        lock (_lock)
        {
            snapshot = _unoptimizable.ToList();
        }
        try
        {
            Directory.CreateDirectory(CacheDir);
            var json = JsonSerializer.Serialize(snapshot);
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // 忽略保存失败
        }
    }
}
