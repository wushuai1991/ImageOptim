using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageOptim;

/// <summary>
/// 工具链构建规则，来自 <c>shared/toolchain.json</c>（以 Windows 实现为基准的双端契约）。
/// 这里只负责"选哪些工具、按什么顺序、满足什么条件"，具体参数仍由各 Worker 自己从 Preferences 读取。
/// 详见契约文件里的 legend 说明。
/// </summary>
public static class Toolchain
{
    // ---------------------------------------------------------------- 数据模型

    public sealed class Spec
    {
        public int Version { get; set; }
        public string Baseline { get; set; } = "";
        public Dictionary<string, List<ToolSpec>> Types { get; set; } = new();
    }

    public sealed class ToolSpec
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>全部成立才加入（AND）。</summary>
        public List<Condition> When { get; set; } = new();
        /// <summary>这些条件全部成立时跳过（用来表达 else 分支）。</summary>
        public List<Condition> Unless { get; set; } = new();
        /// <summary>加入后置位的状态位，目前只有 lossyConverted。</summary>
        public List<string> SetFlags { get; set; } = new();
    }

    public sealed class Condition
    {
        /// <summary>Preferences 属性名；带 gt/lt/gte/lte 时做数值比较，否则按布尔用。</summary>
        public string? Pref { get; set; }
        public bool? Value { get; set; }
        public double? Gt { get; set; }
        public double? Lt { get; set; }
        public double? Gte { get; set; }
        public double? Lte { get; set; }
        /// <summary>lossyEnabled / noLossyYet</summary>
        public string? Flag { get; set; }
        /// <summary>JpegToolMode 取值。</summary>
        public string? Mode { get; set; }
    }

    // ---------------------------------------------------------------- 加载

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<Spec> CachedSpec = new(LoadSpec);

    private static Spec LoadSpec()
    {
        var asm = typeof(Toolchain).Assembly;
        using Stream? stream = asm.GetManifestResourceStream("toolchain.json")
            ?? throw new InvalidOperationException("内嵌资源 toolchain.json 缺失（检查 csproj 的 EmbeddedResource 配置）");

        var spec = JsonSerializer.Deserialize<Spec>(stream, JsonOpts)
            ?? throw new InvalidOperationException("toolchain.json 解析失败");
        return spec;
    }

    // ---------------------------------------------------------------- 构建

    /// <summary>
    /// 按契约文件构建工具链。
    /// </summary>
    /// <param name="type">文件类型</param>
    /// <param name="job">所属 Job（Worker 需要）</param>
    /// <param name="prefs">当前偏好设置</param>
    /// <param name="lossyConverted">跨类型/跨调用的"已执行过有损"标记，命中 setFlags 时会被置位</param>
    public static List<Worker> Build(FileType type, Job job, Preferences prefs, ref bool lossyConverted)
    {
        var key = TypeKey(type);
        var result = new List<Worker>();
        if (key == null || !CachedSpec.Value.Types.TryGetValue(key, out var tools))
            return result;

        bool lossy = lossyConverted;
        // unless 表达的是"if 分支没命中"这个 else 语义，必须用建链前的状态求值：
        // 有损工具一旦加入就会把 lossy 置为 true，实时求值会让 else 分支也命中。
        bool entryLossy = lossyConverted;

        foreach (var tool in tools)
        {
            if (!Matches(tool.When, prefs, lossy))
                continue;
            if (tool.Unless.Count > 0 && Matches(tool.Unless, prefs, entryLossy))
                continue;

            var worker = Create(tool.Id, job, prefs);
            if (worker == null)
            {
                System.Diagnostics.Debug.WriteLine($"toolchain.json 里的未知工具 id: {tool.Id}");
                continue;
            }
            result.Add(worker);

            if (tool.SetFlags.Contains("lossyConverted"))
                lossy = true;
        }

        lossyConverted = lossy;
        return result;
    }

    private static string? TypeKey(FileType type) => type switch
    {
        FileType.Png => "png",
        FileType.Jpeg => "jpeg",
        FileType.Gif => "gif",
        FileType.Svg => "svg",
        FileType.Bmp => "bmp",
        FileType.Webp => "webp",
        _ => null,
    };

    // ---------------------------------------------------------------- 条件求值

    private static bool Matches(List<Condition> conditions, Preferences prefs, bool lossyConverted)
    {
        foreach (var c in conditions)
        {
            if (!Match(c, prefs, lossyConverted))
                return false;
        }
        return true;
    }

    private static bool Match(Condition c, Preferences prefs, bool lossyConverted)
    {
        if (!string.IsNullOrEmpty(c.Flag))
        {
            return c.Flag switch
            {
                "lossyEnabled" => prefs.LossyEnabled,
                "noLossyYet" => !lossyConverted,
                _ => false,
            };
        }

        if (!string.IsNullOrEmpty(c.Mode))
            return string.Equals(prefs.JpegToolMode.ToString(), c.Mode, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(c.Pref))
            return false;

        PropertyInfo? prop = PrefProperties.GetValueOrDefault(c.Pref);
        if (prop == null)
        {
            System.Diagnostics.Debug.WriteLine($"toolchain.json 里的未知偏好项: {c.Pref}");
            return false;
        }

        object? raw = prop.GetValue(prefs);
        bool numeric = c.Gt.HasValue || c.Lt.HasValue || c.Gte.HasValue || c.Lte.HasValue;

        if (!numeric)
            return raw is bool b && b == (c.Value ?? true);

        double v = Convert.ToDouble(raw);
        if (c.Gt.HasValue && !(v > c.Gt.Value)) return false;
        if (c.Lt.HasValue && !(v < c.Lt.Value)) return false;
        if (c.Gte.HasValue && !(v >= c.Gte.Value)) return false;
        if (c.Lte.HasValue && !(v <= c.Lte.Value)) return false;
        return true;
    }

    private static readonly Dictionary<string, PropertyInfo> PrefProperties =
        typeof(Preferences).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- 构造

    private static Worker? Create(string id, Job job, Preferences prefs)
    {
        int level = prefs.AdvPngLevel;

        return id switch
        {
            "pngquant" => new PngquantWorker(job, level, prefs.PngMinQuality),
            "pngcrush" => new PngCrushWorker(job, level, prefs),
            "oxipng" => new OxiPngWorker(job, level, prefs.PngOutRemoveChunks),
            "pngout" => new PngoutWorker(job, level, prefs),
            "advpng" => new AdvCompWorker(job, level),
            "zopfli" => new ZopfliWorker(job, level, prefs) { AlternativeStrategy = false },

            "jpegli" => new JpegliWorker(job, prefs),
            "jpegtran" => new JpegtranWorker(job, prefs),
            "jpegoptim" => new JpegoptimWorker(job, prefs),
            "guetzli" => new GuetzliWorker(job, prefs),

            "gifsicle" => new GifsicleWorker(job, interlace: false, 100),
            "gifsicle_interlaced" => new GifsicleWorker(job, interlace: true, 100),
            "gifsicle_lossy" => new GifsicleWorker(job, interlace: false, prefs.GifQuality),

            "svgo" => new SvgoWorker(job, prefs.LossyEnabled),
            "svgcleaner" => new SvgcleanerWorker(job, prefs.LossyEnabled),

            "jpegconvert" => new JpegConvertWorker(job, prefs),

            _ => null,
        };
    }
}
