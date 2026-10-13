# FullOffice

FullOffice 是正在开发的桌面办公软件。Windows 0.3.3 完整开发包包含 PDF 阅读器、Markdown 和文本编辑器，以及 DOCX、XLSX、PPTX、ODT、ODS、ODP、RTF、CSV 原生编辑组件，支持实际编辑、公式重算与原文件保存。增加了默认关闭的定时保存选项，并改善连续跳页时的书签侧栏响应。兼容性与性能验收及 macOS 移植仍在继续。

[Windows 安装版](https://github.com/FullPeople/FullOffice/releases/download/v0.3.3/FullOffice-0.3.3-Setup-windows-x64.exe) · [Windows 便携版](https://github.com/FullPeople/FullOffice/releases/download/v0.3.3/FullOffice-0.3.3-windows-x64.zip) · [官网](https://dnd.center/)。重要文件建议先用副本试用；开发版的样例检查不等于所有文档的兼容性。常见格式测试曾出现编辑器空白及退出后的会话清理失败，后续检查成功，偶发原因仍在调查。

## 当前可运行的能力

- PDF 快速预览、适宽与清晰整页适应，按需渲染和相邻页缓存。
- 多标签、最近记录、完整书签层级与编辑、批注和高亮。
- 常驻彩色页签、悬停展开、按下回弹和当前页选中状态；色签不占排版宽度，也不能拖拽。
- 框选复制文字、换行合并、区域和原图复制；本机 OCR 按需使用，缺少系统中文语言包时使用随软件提供的独立识别组件。
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
| DOCX 编辑与保存 | 开发版已接入；中文和英文编辑、原文件保存及外部修改冲突检查通过，更多文档兼容性待验收 |
| 复杂 Excel 完整编辑、公式重算与保存 | 开发版已接入；独立生成的 18 工作表、7,159 个公式和 2 张图片的样例通过实际编辑、七种公式计算、Ctrl+S 和原文件保存检查，更多格式保真和性能继续验收 |
| Markdown 与常见文本编辑、预览和保存 | 已实现基础功能，持续测试 |
| PPTX 演示文稿 | 开发版已接入；基础标题编辑、原文件保存、备份和重开检查通过，复杂演示稿兼容性待验收 |
| 新建 DOCX／XLSX／PPTX／Markdown | 已接入首页入口及实际文件选择窗口，基础新建、独立解析和标签切换通过 |
| ODT、ODS、ODP、RTF、CSV | 0.3.3 完整包的实际编辑保存及 RTF 中文重开检查通过；更多文档及偶发问题仍在验收 |
| Windows 安装、卸载和默认应用候选注册 | 已实现，自包含安装包通过独立安装与卸载检查 |
| 更新检查、下载校验和安装界面 | 0.3.2 签名公网升级 14 项通过；0.3.3 普通升级 10 项及匿名完整包摘要检查通过，新版签名公网升级验收进行中 |
| macOS 包体 | 待移植和运行验证 |

新建、PPTX、中文识别与默认格式的范围和实际验证见 [本轮验证记录](docs/validation/2026-10-13-new-documents-and-ocr.md)。

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

主体代码使用 MIT 许可证，Markdig 1.4.0 使用 BSD-2-Clause。`src/FullOffice.OfficeEngine.Native` 单独使用 GNU AGPL 第 3 版；保留 ONLYOFFICE 原始头文件声明、产品标识和附加条款。原始 ONLYOFFICE 标识使用 CC BY-SA 4.0。完整第三方条款见 `licenses` 与原生组件目录，不能将整个依赖树解释为 MIT。

文档与表格编辑器采用 ONLYOFFICE Desktop Editors 9.4.0.129 的原生接口，运行在独立进程内。组件按需启动；PDF、Markdown 和文本打开时不会启动表格内核。构建、版本固定与独立运行时验证见 [开发版文档与表格编辑器](docs/office-engine.md)。

## Windows 安装与更新组件

`src/FullTools.Distribution` 包含常规安装、卸载、默认应用候选注册、自解压安装界面和三个软件共用的更新组件。安装版包含 .NET 8 Windows Desktop Runtime；默认应用选择由用户在 Windows 设置中完成。

更新清单使用 RSA-PSS/SHA-256 签名，下载只通过 HTTPS，并验证清单中的大小和 SHA-256。安装前再次校验并锁定文件。更新组件先复制到独立暂存目录运行，避免自身占用安装目录；用户须保存并退出主程序后才能安装更新。后台检查每日最多一次，启动界面显示后才触发。

```powershell
cmake -S src/FullTools.Distribution -B .cache/distribution-build
cmake --build .cache/distribution-build --config Release
python tools/verify_distribution.py
python tools/verify_update_signatures.py
python tools/verify_update_versions.py
```

签名测试使用公开测试夹具，不需要发布私钥。`tools/sign_update.py` 仅用于发布机器，私钥保存在仓库之外并使用 Windows DPAPI 保护；不得提交到源码或发布包。签名夹具中的测试版本不是实际发布版本。
