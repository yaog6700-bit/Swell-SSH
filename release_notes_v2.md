## 🎉 SwellSSH v2.1.1

### ✨ 主要变更

#### 🚀 终端性能优化与主题定制（贡献者：[@david1025](https://github.com/david1025)）

- 使用高效的环形缓冲区（Ring Buffer）替换原有回滚列表，大幅提升渲染性能
- 实现了行版本控制和增量 UI 重绘，减少渲染开销
- 优化 SSH 传输层，使用 Channel 异步队列和专用阻塞读取线程
- 新增持久化的 `TerminalTheme` 主题系统与丰富的内置主题集合
- 侧边栏新增字体和大小控件，支持实时更新
- 修复代理对（Surrogate pairs）和 UTF-16 序列输入问题

#### 🗂️ 导航栏 UI 全面重构（贡献者：[@david1025](https://github.com/david1025)）

- 连接列表从左侧独立栏迁移至 `NavigationView` 导航面板（汉堡菜单展开区），终端区域空间更宽敞
- 新增「连接选择器」弹窗：点击 `+` 新建标签时弹出带搜索过滤的连接选择对话框
- 空状态页面升级：加入快速连接输入框和「新建连接」「展开连接列表」快捷按钮
- 标题栏精细拖拽区域控制：TabView 标签和按钮区域正确穿透标题栏，解决了原有的点击冲突
- 设置页打开方式改为在 TabView 内新开标签页，体验更一致
- 连接编辑对话框布局升级为两列网格排布，字段更整洁

#### 🛠️ 其他修复与改进

- 主题切换按钮移至导航面板底部，与设置入口并列
- 连接对话框字段两列布局，更紧凑美观
- 设置页改为居中布局，宽屏下体验更好

---

### 👥 贡献者

本版本由以下贡献者共同完成：

| 贡献者 | 内容 |
|--------|------|
| [@david1025](https://github.com/david1025) | 终端性能优化、主题系统、字体定制（[PR #5](https://github.com/yaog6700-bit/Swell-SSH/pull/5)）；导航栏 UI 重构、连接选择器、空状态页优化（[PR #3](https://github.com/yaog6700-bit/Swell-SSH/pull/3)） |

---

### ⬇️ 下载

| 文件 | 平台 |
|------|------|
| `SwellSSH-win-x64.zip` | Windows x64 |
| `SwellSSH-win-arm64.zip` | Windows ARM64 |

> 解压后直接运行 `SwellSSH.exe`，无需安装。首次运行 Windows SmartScreen 提示请选择「更多信息」→「仍要运行」。

---

### ⚠️ 版本回退说明

如需回退到上一稳定版本，可在 [Releases](https://github.com/yaog6700-bit/Swell-SSH/releases) 页面下载 **v1.0.1**。
