# FullOffice

FullOffice 是正在开发的桌面办公软件。当前开源代码包含可运行的 Windows PDF 阅读器与 Markdown、文本编辑器，后续整合文档、表格、安装与更新。尚未整合的能力不会列为已支持。

## 当前可运行的能力

- PDF 快速预览、适宽与清晰整页适应，按需渲染和相邻页缓存。
- 多标签、最近记录、完整书签层级与编辑、批注和高亮。
- 常驻彩色页签、悬停展开、按下回弹和当前页选中状态；色签不占排版宽度，也不能拖拽。
- 框选复制文字、换行合并、区域和原图复制；本机 OCR 按需使用。
- 跨页悬停引用和多个可缩放、拖动的地图小窗。
- 保存按钮、Ctrl+S、关闭 PDF 时的保存直接写回当前文件；写入失败时保留未保存修改。
- 原文件上一版本保存在同目录隐藏文件夹 `.FlowOfficeBackups`，正常情况下每个 PDF 保留一份。另存副本仍可主动使用。
- Markdown 编辑、分栏预览、标题、列表、表格、代码块、相对路径图片与基本文字格式；预览使用本地排版，不执行 HTML 或脚本。
- 文本原文件保存、编码和换行保留、查找、导出副本；只读和外部修改冲突不会清除编辑内容。
- PDF 与文本共用标签页和最近记录，Ctrl+Tab／Ctrl+Shift+Tab 切换，Ctrl+1～8 选择标签页，Ctrl+9 选择最后一页。

## 支持状态

| 能力 | 当前状态 |
| --- | --- |
| Windows PDF 阅读与标记 | 已实现，持续测试 |
| DOCX 编辑与保存 | 编辑底层兼容性评估中 |
| 复杂 Excel 完整编辑、公式重算与保存 | 必须完成真实文件验收，尚未整合 |
| Markdown 与常见文本编辑、预览和保存 | 已实现基础功能，持续测试 |
| 常见格式导入、导出 | 待编辑底层确定 |
| 安装、卸载、默认应用与自动更新 | 待整合 |
| macOS 包体 | 待移植和运行验证 |

## 构建与验证

需要 Windows 10 2004 或更新版本、x64 和 .NET 8 SDK。运行 `src/FullOffice.Windows/build.ps1` 构建；源码随附锁定版本的 Windows PDFium 库及完整第三方声明。

构建后的主程序是 `FullOffice.exe`。在测试输出目录运行以下检查，不会改写输入测试页：

```powershell
./artifacts/app/FullOffice.exe --saving-probe tests/fixtures/feature-fixture.pdf artifacts/saving
./artifacts/app/FullOffice.exe --revision-probe tests/fixtures/feature-fixture.pdf artifacts/revision
./artifacts/app/FullOffice.exe --workspace-probe tests/fixtures/feature-fixture.pdf artifacts/workspace
./artifacts/app/FullOffice.exe --feature-probe tests/fixtures/feature-fixture.pdf artifacts/features
./artifacts/app/FullOffice.exe --text-probe tests/fixtures/feature-fixture.pdf artifacts/text
```

`--inplace-probe` 会先把输入 PDF 复制到检查目录，只编辑和写回检查副本。用户文档、真实曲库、私有设置和联机凭据不进入仓库或发布包。

## 项目边界

完整 Excel 不是表格预览或简单公式演示。公式、图片、样式、工作表、打开和保存兼容性，以及首次显示、滚动和重算性能须分别验证。

当前 PDF 原生操作在独立串行工作线程执行，打开时不进行整书文字或 OCR 扫描。UI 自动化与程序化网络检查不能代替物理输入延迟、真实双机网络、macOS 或手机设备验收。

主体代码使用 MIT 许可证，Markdig 1.4.0 使用 BSD-2-Clause，完整第三方条款见 `licenses`。尚未引入完整 Office 编辑内核；后续引入时同时记录版本、源码和对应许可证。
