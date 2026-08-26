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

    /// <summary>计算输入文件哈希（与设置哈希组合，原版用 MD5）。</summary>
    public static string ComputeHash(byte[] settingsHash, string filePath)
    {
        using var md5 = MD5.Create();
        var fileBytes = File.ReadAllBytes(filePath);
        var buffer = new byte[settingsHash.Length + fileBytes.Length];
        Buffer.BlockCopy(settingsHash, 0, buffer, 0, settingsHash.Length);
        Buffer.BlockCopy(fileBytes, 0, buffer, settingsHash.Length, fileBytes.Length);
        var hash = md5.ComputeHash(buffer);
        return Convert.ToHexString(hash);
    }

    public bool HasResult(string hash)
    {
        lock (_lock)
            return _unoptimizable.Contains(hash);
    }

    public void MarkUnoptimizable(string hash)
    {
        lock (_lock)
        {
            if (_unoptimizable.Add(hash))
                SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var json = JsonSerializer.Serialize(_unoptimizable.ToList());
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // 忽略保存失败
        }
    }
}
