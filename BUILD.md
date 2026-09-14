# BiliShare 打包发布指南

本文档说明如何从源码一键构建发布版，产出**安装包**与**便携压缩包**。

## 一、前置环境

| 工具 | 用途 | 默认路径 |
|---|---|---|
| Visual Studio（含 MSBuild） | 编译（必需，`dotnet build` 无法生成 PRI 资源） | `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe` |
| Inno Setup 6 | 生成安装包 | `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` |
| 7-Zip | 生成便携压缩包 | `7z.exe`（需在 PATH 中） |

## 二、应用图标（一次性）

首次或更换图标时，将 PNG 转成多尺寸 ICO：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\convert-icon.ps1 -Png "源图标.png" -Ico ".\src\BiliShare\Assets\BiliShare.ico"
```

产物 `src\BiliShare\Assets\BiliShare.ico` 已被 csproj 的 `ApplicationIcon` 及安装脚本引用，无需其它改动。

## 三、一键发布

在项目根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

脚本按 4 步执行：

1. MSBuild Release 编译（x64）
2. 精简产物目录（仅保留 zh-CN / zh-TW / en 语言资源，剔除多余语言包与 `.pdb` 调试符号）
3. Inno Setup 编译安装脚本
4. 7-Zip 生成便携压缩包

## 四、产物

| 产物 | 路径 | 说明 |
|---|---|---|
| 应用目录 | `dist\BiliShare\` | 完整运行目录，可直接拷贝使用 |
| 安装包 | `dist\BiliShare_Setup_1.0.0.exe` | 默认安装到「安装包所在目录\BiliShare」，无需管理员权限 |
| 便携版 | `dist\BiliShare_1.0.0_portable.zip` | 解压即用 |

## 五、版本号与发布者

版本号 `1.0.0` 与发布者 `BaaaiZe` 在以下三处需保持一致，改版本号时同步修改：

- `src\BiliShare\BiliShare.csproj`（`Version` / `Company`）
- `scripts\BiliShare.iss`（`MyAppVersion` / `MyAppPublisher` / `OutputBaseFilename`）
- `scripts\build-release.ps1`（`$Version`）

## 六、注意事项

- 构建前请关闭正在运行的 BiliShare 及 `msedgewebview2` 进程，否则 exe 被占用会导致编译失败。
- 必须使用 Visual Studio 的 MSBuild，`dotnet build` 无法生成 PRI 资源文件。
- Release 产物会在发布前剔除运行期数据目录 `data/`，确保交付目录干净。
- Inno Setup 中文语言文件位于 `scripts\Languages\ChineseSimplified.isl`，缺失会导致安装脚本报错。