# 开发版文档与表格编辑器

开发版使用独立原生进程，将 ONLYOFFICE 文档与表格编辑界面嵌入 FullOffice 标签页。当前仅接入 DOCX 和 XLSX；其他格式不能据此列为已验收。公开 0.2.0 安装包尚未包含此组件。

## 版本与构建

Windows x64 构建需要 Visual Studio C++ 工具、CMake、Python 3.12 或更新版本和 .NET 8 SDK。对应 ONLYOFFICE Desktop Editors 为 9.4.0.129。构建脚本检查实际核心 DLL 的 SHA-256，版本不符时停止，避免 C++ 接口不兼容。

- SDK 源码：`6bd2051287a496ac1504214caca69a5729625696`。
- core 源码：`b18715ae811aaa5cd9dae2dd9a4b9412d38b8b7b`。
- `third_party/provenance.json` 记录 13 个未修改上游头文件的地址和校验值。
- 原生宿主使用 GNU AGPL 第 3 版；完整许可、原始附加声明及资源声明随源码保留。

在仓库根目录运行：

```powershell
python tools/build_office_host.py 'C:/Program Files/ONLYOFFICE/DesktopEditors'
dotnet publish src/FullOffice.Windows/FullOffice.Windows.csproj -c Release -r win-x64 --self-contained true -o artifacts/office-candidate
python tools/stage_office_runtime.py 'C:/Program Files/ONLYOFFICE/DesktopEditors' artifacts/office-candidate
./artifacts/office-candidate/FullOffice.exe
```

暂存脚本只复制编辑内核、转换器、原始页面资源、字体、词典及其运行依赖，不读取用户配置或文档。默认从应用目录的 `office-engine` 加载，无须依赖系统 ONLYOFFICE 安装目录。运行时清单记录文件和版本；此脚本用于本地完整包验证，公开分发前仍须完成全部对应源码、第三方发布资料和完整安装验收。

开发诊断可以显式设置 `FULLOFFICE_ENGINE_RUNTIME` 和 `FULLOFFICE_ENGINE_ROOT`；正常发行不依赖这些环境变量。云端可以通过 `ENGINE_COMPILE_ONLY=ON` 编译原生接口宿主，但这不等于在云端执行了真实 Office 文档编辑。

## 保存与检查

打开时将文档复制到独立编辑会话。原生编辑器保存成功后，FullOffice 校验新文件、检查原文件是否被外部修改，并原子替换原文件。上一版本放在同目录隐藏的 `.FlowOfficeBackups`。只读、链接文件或外部修改冲突不会覆盖原文件，也不会清除当前修改。编辑进程失败时会话文件保留；自动恢复界面仍在开发。

真实 Excel 副本完成数值编辑、相关公式重算和原文件保存。18 个工作表、7,159 个公式、原有 42 个错误值保留；九项比较涵盖公式、输入值、工作表顺序、合并、图片、保护、数据验证和条件规则。测试文件及其中内容不进入源码或公开包。

DOCX 通过实际界面编辑中文、英文和数字，保存后检查内容与上一版备份；模拟外部修改时拒绝覆盖。仍需更多复杂排版、表格、图片和批注文件测试，以及真实输入、首次显示、滚动和内存性能验收。

## 实际混合标签页检查

`tools/probe_office_workspace.py` 在 96 DPI 的 Windows 桌面执行真实输入，先复制输入文件，再检查原生编辑器中的 Ctrl+O、PDF 与 DOCX 的 Ctrl+Tab／Ctrl+Shift+Tab、Ctrl+W 和正常退出。焦点离开测试程序时停止输入。此检查不在云端模拟，也不能代替其他 DPI、多个物理屏和更多输入设备验收。

```powershell
python tools/probe_office_workspace.py artifacts/office-candidate/FullOffice.exe sample.docx sample.pdf artifacts/office-workspace-check
```
