using System.Windows;

namespace ImageOptim;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 清理过期的回退备份（超过 7 天），避免备份目录无限增长
        BackupStore.CleanupExpired();
        base.OnStartup(e);
    }
}
