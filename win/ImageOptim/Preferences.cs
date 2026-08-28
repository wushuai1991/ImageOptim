using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageOptim;

/// <summary>
/// 应用偏好设置，对应 macOS 原版的 <c>NSUserDefaults</c> + <c>defaults.plist</c>。
/// 使用 JSON 文件持久化到用户配置目录，支持默认值注册与读写。
/// </summary>
public sealed class Preferences
{
    private static readonly string PrefsDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageOptim");
    private static readonly string PrefsPath = System.IO.Path.Combine(PrefsDir, "prefs.json");

    // 有损压缩总开关
    public bool LossyEnabled { get; set; }
    public int JpegOptimMaxQuality { get; set; } = 80;
    public int PngMinQuality { get; set; } = 80;
    public int GifQuality { get; set; } = 80;

    // 各工具开关
    public bool AdvPngEnabled { get; set; } = true;
    public int AdvPngLevel { get; set; } = 4;
    public bool JpegOptimEnabled { get; set; } = true;
    public bool JpegTranStripAll { get; set; } = true;
    public bool JpegTranEnabled { get; set; } = true;
    public bool OptiPngEnabled { get; set; } = true;
    public bool PngCrush2Enabled { get; set; } = false;
    public bool PngOutEnabled { get; set; } = true;
    public bool PngOutRemoveChunks { get; set; } = true;
    public bool ZopfliEnabled { get; set; } = true;
    public bool GifsicleEnabled { get; set; } = true;
    public bool SvgoEnabled { get; set; } = true;
    public bool SvgcleanerEnabled { get; set; } = true;
    public bool GuetzliEnabled { get; set; } = false;

    // 文件保存选项
    public bool PreservePermissions { get; set; } = true;
    public bool PreserveDates { get; set; } = true;
    public bool RemoveOriginal { get; set; } = true;

    // 并发与进程选项
    // 默认并发文件数限制为 4：每个文件内部还会串行运行多个外部进程，
    // 若按 CPU 逻辑核心数（如 32 核）并发，会同时启动几十个外部进程导致系统卡顿。
    public int RunConcurrentFiles { get; set; } = Math.Min(4, Environment.ProcessorCount);
    public int RunConcurrentDirscans { get; set; } = Math.Max(1, (int)Math.Ceiling(Environment.ProcessorCount / 3.9));
    public int RunConcurrentFileops { get; set; } = 2;
    public bool RunLowPriority { get; set; } = false;
    public bool BounceDock { get; set; } = true;

    // 窗口选项
    public bool WindowTopmost { get; set; } = false;

    // 交互提醒
    public int AddFilesThreshold { get; set; } = 2000;

    // 仅用于 Guetzli 联动状态（不持久化也行，但保持一致）
    [JsonIgnore]
    public bool JpegTranStripAllSetByGuetzli { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Preferences Load()
    {
        try
        {
            if (File.Exists(PrefsPath))
            {
                var json = File.ReadAllText(PrefsPath);
                var prefs = JsonSerializer.Deserialize<Preferences>(json, JsonOpts);
                if (prefs != null)
                    return prefs;
            }
        }
        catch
        {
            // 读取失败则回退到默认值
        }
        return new Preferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(PrefsDir);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(PrefsPath, json);
        }
        catch
        {
            // 忽略保存失败
        }
    }

    /// <summary>根据启用工具计算支持的文件扩展名列表。</summary>
    public HashSet<string> EnabledExtensions()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (IsPngEnabled()) { result.Add("png"); }
        if (JpegOptimEnabled || JpegTranEnabled || GuetzliEnabled) { result.Add("jpg"); result.Add("jpeg"); }
        if (GifsicleEnabled) { result.Add("gif"); }
        if (SvgoEnabled || SvgcleanerEnabled) { result.Add("svg"); }
        return result;
    }

    public bool IsPngEnabled() =>
        PngCrush2Enabled || PngOutEnabled || OptiPngEnabled || AdvPngEnabled || ZopfliEnabled;
}
