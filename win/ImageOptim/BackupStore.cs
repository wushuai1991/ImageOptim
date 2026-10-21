using System.IO;

namespace ImageOptim;

/// <summary>
/// 回退备份仓库。备份集中存放在 %LOCALAPPDATA%\ImageOptim\Backups，
/// 不在用户图片目录里生成 ~xxx.imageoptim.bak（原实现留下的备份永不回收，
/// 每优化一个文件就在用户目录里多一个隐藏文件）。
/// 备份仅用于「回退到优化前」功能，超过保留期由启动时清理。
/// </summary>
public static class BackupStore
{
    private static readonly string BackupDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageOptim", "Backups");

    /// <summary>备份保留期。超过后启动时自动清理。</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>为指定源文件分配一个唯一备份路径，保留原扩展名（回退时按类型写入）。</summary>
    public static string NewBackupPath(string sourceFilePath)
    {
        Directory.CreateDirectory(BackupDir);
        var ext = System.IO.Path.GetExtension(sourceFilePath);
        return System.IO.Path.Combine(BackupDir, $"{Guid.NewGuid():N}{ext}");
    }

    /// <summary>删除单个备份文件（回退成功后调用）。</summary>
    public static void Remove(string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
        catch
        {
            // 删除失败不影响回退结果，等过期清理
        }
    }

    /// <summary>清理超过保留期的历史备份，避免目录无限增长。应用启动时调用一次。</summary>
    public static void CleanupExpired()
    {
        try
        {
            if (!Directory.Exists(BackupDir))
                return;

            var limit = DateTime.UtcNow - Retention;
            foreach (var file in Directory.EnumerateFiles(BackupDir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < limit)
                        File.Delete(file);
                }
                catch
                {
                    // 单个文件被占用时跳过
                }
            }
        }
        catch
        {
            // 目录不可访问时忽略
        }
    }
}
