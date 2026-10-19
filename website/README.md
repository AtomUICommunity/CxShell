# CxShell Portal

这是 CxShell 仓库中的独立静态门户页面，不参与 Avalonia 桌面应用编译，也不需要 Node.js 或构建工具。

## 预览

页面所需的 Logo、产品截图和捐助二维码都已整理到 `assets/` 下，整个 `website/` 目录可以独立复制和部署。部署时请保留 `en/`、`zh/` 两个语言入口目录以及根目录下的脚本文件。语言入口使用 `/en/` 和 `/zh/` 路径；需要通过 HTTP 静态服务器预览，才能测试地址切换和直接访问语言链接。

也可以从仓库根目录启动一个静态文件服务器：

```powershell
python -m http.server 8080 --directory website
```

然后访问 <http://localhost:8080/en/> 或 <http://localhost:8080/zh/>。根路径 <http://localhost:8080/> 默认进入英文版。语言切换会更新地址，刷新后仍保持所选语言。

## 内容

- 产品介绍、核心能力与工作区截图
- Avalonia、AtomUI 和 .NET 技术栈说明
- GitHub 仓库、Release 和 Issue 入口
- Ko-fi、微信支付和支付宝捐助入口
