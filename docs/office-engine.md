# 开发版文档与表格编辑器

开发版使用独立原生进程，将 ONLYOFFICE 文档、表格和演示文稿编辑界面嵌入 FullOffice 标签页。当前接入 DOCX、XLSX 和 PPTX。0.3.2 完整 Windows 开发包已包含此组件；旧 0.2.0 候选仅有 PDF 和文本能力。CSV、ODF、RTF 等其他格式尚未接入，不能用上游支持表代替本项目的实际验收。

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

暂存脚本只复制编辑内核、转换器、原始页面资源、字体、词典及其运行依赖，不读取用户配置或文档。默认从应用目录的 `office-engine` 加载，无须依赖系统 ONLYOFFICE 安装目录。运行时清单记录文件和版本。0.3.2 完整包的 4,568 个编辑组件文件已逐一核对大小和 SHA-256，.NET 运行组件固定为 8.0.31；实际安装、普通升级、卸载和三种编辑器检查分别执行。

原生桥接和固定版本头文件在本库公开。未修改的编辑组件对应上游 [DesktopEditors](https://github.com/ONLYOFFICE/DesktopEditors)、[desktop-sdk 固定版本](https://github.com/ONLYOFFICE/desktop-sdk/tree/6bd2051287a496ac1504214caca69a5729625696) 和 [core 固定版本](https://github.com/ONLYOFFICE/core/tree/b18715ae811aaa5cd9dae2dd9a4b9412d38b8b7b)，构建工具见上游 [build_tools](https://github.com/ONLYOFFICE/build_tools)。包内保留原产品标识、许可证和运行时清单。

开发诊断可以显式设置 `FULLOFFICE_ENGINE_RUNTIME` 和 `FULLOFFICE_ENGINE_ROOT`；正常发行不依赖这些环境变量。云端可以通过 `ENGINE_COMPILE_ONLY=ON` 编译原生接口宿主，但这不等于在云端执行了真实 Office 文档编辑。

## 保存与检查

打开时将文档复制到独立编辑会话。原生编辑器保存成功后，FullOffice 校验新文件、检查原文件是否被外部修改，并原子替换原文件。上一版本放在同目录隐藏的 `.FlowOfficeBackups`。只读、链接文件或外部修改冲突不会覆盖原文件，也不会清除当前修改。编辑进程失败时会话文件保留，并可从恢复界面导出已确认的快照。0.3.2 将恢复快照与编辑中的工作文件分离，兼容原生可写句柄；稳定内容校验失败时保留上一份快照。定时自动恢复保存仍待实现。

私人 Excel 副本的早期往返检查覆盖 18 个工作表、7,159 个公式和原有 42 个错误值，九项比较涵盖公式、输入值、工作表顺序、合并、图片、保护、数据验证和条件规则。文件及内容不进入源码、云端或公开包。这组结果不代替该文件在最终安装包中的真实视觉与输入性能验收。

0.3.2 安装后的云端界面检查使用另一份独立生成的样例，含 18 个工作表、7,159 个公式、2 张图片和 42 个刻意保留的错误值。实际键入、更改依赖值、七种公式计算、Ctrl+S、结构与图像摘要、原文件备份、保存后立即关闭和会话退休均通过；同一轮还完成 DOCX、PPTX、默认候选登记和卸载，共 21 项检查。普通默认路径 0.3.0→0.3.2 升级另有 10 项检查通过。首次窗口出现耗时不当作首屏内容、输入延迟或滚动帧率。

完整包的签名公网升级另有 14 项检查通过：实际旧安装的更新工具从官网获取签名清单，通过 GitHub 镜像完整下载并验证安装器，实际运行中的旧软件阻止升级，正常退出后执行同默认根升级，再确认设置、外部文档、已有 UserChoice、最新版本检测和普通卸载。官网下载在首次跨区域测试中 20 分钟未完成，此失败记录保留；镜像调整没有重建或更换安装包。匿名全文验证中，两包各约 71 秒完成，但不据此承诺其他网络的下载速度。

DOCX 通过实际界面编辑中文、英文和数字，保存后检查内容与上一版备份；模拟外部修改时拒绝覆盖。仍需更多复杂排版、表格、图片和批注文件测试，以及真实输入、首次显示、滚动和内存性能验收。

## 实际混合标签页检查

`tools/probe_office_workspace.py` 在 96 DPI 的 Windows 桌面执行真实输入，先复制输入文件，再检查原生编辑器中的 Ctrl+O、PDF 与 DOCX 的 Ctrl+Tab／Ctrl+Shift+Tab、Ctrl+W 和正常退出。焦点离开测试程序时停止输入。此检查不在云端模拟，也不能代替其他 DPI、多个物理屏和更多输入设备验收。

```powershell
python tools/probe_office_workspace.py artifacts/office-candidate/FullOffice.exe sample.docx sample.pdf artifacts/office-workspace-check
```
