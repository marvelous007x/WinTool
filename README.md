# WinTool

托盘常驻的 Windows 小工具，目前包含两个功能：

- **全局按键映射**：把一个组合键映射成另一个按键，例如 `Alt+PgUp → VolumeUp`。
- **区域截图 + 标注**：全局热键框选，矩形/椭圆/箭头/画笔/文字标注，复制或保存 PNG。

两个功能默认关闭，在各自页签顶部勾选启用。

## 使用

程序启动后只在托盘显示，双击托盘图标打开主窗口。

### 按键映射

1. 勾选「启用按键映射」。
2. 添加映射，点击「源按键」「目标按键」输入框后直接按下按键即可录入。
3. 保存后立即生效，列表中的勾选框可单独启停条目。

标准键盘上没有实体按键的键（音量、媒体键、F13–F24 等）点输入框右侧 **▾** 选择。

### 截图

- 勾选「启用截图」，默认热键 `Ctrl+Shift+A`（可修改，也可从托盘菜单触发）。
- 拖拽框选后直接标注：矩形、椭圆、箭头、画笔、文字；最左侧色块切换颜色；`Ctrl+Z` 撤销。
- `Enter` / 「确认」复制并关闭，`Ctrl+S` 保存 PNG，`Esc` / 右键取消。
- 选区可拖手柄缩放，方向键微调位置；多显示器、混合 DPI 均支持。

### 软件设置

主窗口右下角「软件设置…」，目前只有「开机自启」一项（在用户启动文件夹创建快捷方式，不写注册表）。

## 配置

配置和日志都放在程序目录下，不写注册表，整个文件夹拷走即可使用：

- `data\config.json` — 配置
- `data\error.log` — 异常日志

## 构建

需要 .NET 8 SDK：

```powershell
dotnet build -c Release
dotnet publish WinTool/WinTool.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

发布产物在 `WinTool/bin/Release/net8.0-windows/win-x64/publish/`，单文件约 600 KB，需要安装 .NET 8 桌面运行时。

## 已知限制

- 普通权限运行，对以管理员身份运行的程序按键映射不生效（Windows UIPI 限制）。
- 系统保留组合键（`Ctrl+Alt+Del`、`Win+L` 等）无法拦截；使用 Raw Input 的游戏仍能收到原始按键。
- 全局键盘钩子 + 模拟按键属敏感行为，首次运行可能触发 SmartScreen 或杀软告警。
