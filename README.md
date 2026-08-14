# CreFetch 高速下载器

CreFetch 是一款基于 C# WinForms 开发的多线程、断点续传下载工具。它支持从 HTTP/HTTPS 链接高速下载文件，通过分块并发，任务状态持久化等特性，显著提升大文件下载效率。

---

## 主要特性

- **多线程分块下载**：将文件切分为多个数据块（Chunk），使用独立线程并发下载，充分利用带宽。
- **断点续传**：下载进度实时保存至 `.state` 文件，支持中断后继续下载（需服务器支持 Range）。
- **任务队列管理**：支持同时执行多个下载任务（可配置并发数），任务状态持久化（`tasks.json`），重启应用自动恢复未完成任务。
- **剪贴板监控**：自动检测剪贴板中的 HTTP/HTTPS 链接，弹出添加任务提示。
- **实时进度反馈**：主界面展示文件名、大小、进度条（含百分比）、状态、下载速度、已用时间。
- **系统托盘集成**：最小化至托盘，后台运行，下载完成/失败时弹出通知。
- **灵活配置**：通过 `config.ini` 调整下载线程数、分块数、缓冲区大小、重试次数、保存路径、并发数等。
- **开机自启**（可选）：设置后可随 Windows 自动启动。

---

## 系统要求

- **操作系统**：Windows 7 SP1 及以上（建议 Windows 10/11）
- **运行时**：[.NET Framework 4.7.2](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net472) 或 [.NET 6/7/8](https://dotnet.microsoft.com/download)（如果运行预编译的程序则无需安装）
- **硬盘空间**：至少 100 MB（用于程序及临时文件）
- **网络**：支持 HTTP/HTTPS 协议，服务器需支持 `Accept-Ranges: bytes`（若不支持则仅能完整下载）

---

## 安装与运行

### 方式一：源码编译
1. 克隆或下载本仓库源码。
2. 使用 Visual Studio 2022 或更高版本打开解决方案（目标框架 `.NET Framework 10.`）。
3. 编译生成 `CreFetch.exe`。
4. 将 `config.ini`（首次运行自动生成）与 `CreFetch.exe` 放在同一目录。
5. 双击 `CreFetch.exe` 启动。

### 方式二：直接使用预编译包
解压后运行 `CreFetch.exe` 即可。

> **首次启动**：程序会自动生成默认 `config.ini`，并最小化至系统托盘。

---

## 配置说明

配置文件 `config.ini` 位于程序根目录，包含以下节（Section）：

```ini
[Download]
max_concurrent_jobs = 2          # 同时进行的下载任务数（1~10）
max_threads = 12                 # 每个任务使用的下载线程数（1~128）
chunk_count = 96                 # 文件分块数量（4~512，越大并发越高）
save_path = C:\Users\Administrator\Downloads  # 下载保存目录
timeout = 30                     # HTTP 连接超时（秒）
buffer_size_kb = 1024            # 读写缓冲区大小（KB，64~16384）
retry_times = 3                  # 单块下载失败重试次数（0~10）

[App]
auto_start = false               # 是否开机自启动（true/false）
show_notification = true         # 是否显示下载完成/失败的系统通知
