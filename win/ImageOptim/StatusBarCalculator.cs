using System.Collections.ObjectModel;

namespace ImageOptim;

/// <summary>
/// 状态栏文本计算器。对应 macOS 原版 <c>ImageOptimController.initStatusbarWithDefaults</c> 中的统计逻辑。
/// 计算总体节省比例、平均节省比例、最大单文件节省比例，并显示有损/Guetzli 警告。
/// </summary>
public sealed class StatusBarCalculator
{
    private bool _overallAvg;

    public string Calculate(ObservableCollection<JobItem> items, Preferences prefs, bool quitWhenDone)
    {
        if (quitWhenDone)
            return "优化完成后 ImageOptim 将退出";

        long bytesTotal = 0, optimizedTotal = 0;
        double optimizedFractionTotal = 0, maxOptimizedFraction = 0;
        int optimizedFileCount = 0;
        bool anyBusy = false;

        foreach (var item in items)
        {
            if (!anyBusy && item.IsBusy)
                anyBusy = true;

            var original = item.Job.Snapshot.ByteSizeOriginal;
            var optimized = item.Job.Snapshot.ByteSizeOptimized;
            if (original.HasValue && optimized.HasValue && original.Value > 0 &&
                (original.Value != optimized.Value || item.IsDone))
            {
                double fraction = 1.0 - (double)optimized.Value / original.Value;
                if (fraction > maxOptimizedFraction)
                    maxOptimizedFraction = fraction;
                optimizedFractionTotal += fraction;
                bytesTotal += original.Value;
                optimizedTotal += optimized.Value;
                optimizedFileCount++;
            }
        }

        if (optimizedFileCount > 1 && bytesTotal > 0)
        {
            double savedTotal = 1.0 - (double)optimizedTotal / bytesTotal;
            double savedAvg = optimizedFractionTotal / optimizedFileCount;
            if (savedTotal > 0.001)
            {
                if (savedTotal * 0.8 > savedAvg)
                    _overallAvg = true;
                else if (savedAvg * 0.8 > savedTotal)
                    _overallAvg = false;

                double avgNum = _overallAvg ? savedTotal : savedAvg;
                long bytesSaved = bytesTotal - optimizedTotal;

                string fmt = _overallAvg
                    ? "已节省 {0}（共 {1}）。总体 {2:0.0}%（单文件最高 {3:0.0}%）"
                    : "已节省 {0}（共 {1}）。平均每文件 {2:0.0}%（最高 {3:0.0}%）";

                return string.Format(fmt,
                    JobItem.BytesToString(bytesSaved),
                    JobItem.BytesToString(bytesTotal),
                    avgNum * 100,
                    maxOptimizedFraction * 100);
            }
        }

        if (prefs.GuetzliEnabled)
            return "警告：已启用 Guetzli 工具，优化可能非常耗时。";

        if (prefs.LossyEnabled)
        {
            var parts = new List<string>();
            if (prefs.JpegOptimMaxQuality < 100 && prefs.JpegOptimMaxQuality > 0)
                parts.Add($"JPEG {prefs.JpegOptimMaxQuality}%");
            if (prefs.PngMinQuality < 100 && prefs.PngMinQuality > 0)
                parts.Add($"PNG {prefs.PngMinQuality}%");
            if (prefs.GifQuality < 100 && prefs.GifQuality > 0)
                parts.Add($"GIF {prefs.GifQuality}%");
            if (parts.Count > 0)
                return $"已启用有损压缩（{string.Join("、", parts)}）";
        }

        if (anyBusy)
            return "";

        return "";
    }
}
