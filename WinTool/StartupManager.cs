using System;
using System.IO;

namespace WinTool;

/// <summary>开机自启：在“启动”文件夹里放快捷方式，不写注册表。</summary>
public static class StartupManager
{
    static string LinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WinTool.lnk");

    public static bool IsEnabled
    {
        get
        {
            try { return File.Exists(LinkPath); }
            catch { return false; }
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(LinkPath)) File.Delete(LinkPath);
                return true;
            }

            // 单文件发布下 Assembly.Location 为空，回退用程序目录推断
            string exe = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "WinTool.exe");
            if (!File.Exists(exe)) return false;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(LinkPath);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.Description = "WinTool";
            link.Save();
            return true;
        }
        catch (Exception ex)
        {
            ConfigStore.Log(enabled ? "设置开机自启失败" : "取消开机自启失败", ex);
            return false;
        }
    }
}
