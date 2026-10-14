# KitchenChaos 服务端 · 腾讯云 Ubuntu 部署指南

> **部署对象**：`KitchenChaos-Server`（.NET 8 控制台 TCP 服务器，服务端权威）  
> **目标主机**：腾讯云 Ubuntu · 2 核 2 GB · 系统盘 40 GB · 公网 IP `106.52.92.221`  
> **服务端口**：TCP `7777`  
> 编写日期：2026-10-09

---

## 0. 结论速览

一句话方案：**把服务端交叉编译成 Linux 自包含可执行文件 → 上传到 `/opt/kitchenchaos-server/` → 用 systemd 常驻托管 → 在腾讯云放行 TCP 7777 → 把客户端 `Launch.cs` 的 `serverIp` 改成公网 IP。**

动手前先看的两个硬约束：

1. **`Console.ReadKey()` 会阻塞托管**（见 §5.1）。`Program.cs` 结尾用它来"按任意键退出"，而 systemd 下没有交互终端，该调用会抛 `InvalidOperationException` 让进程直接退出。这是本指南里唯一一处**需要改代码**的地方，不改就只能用 tmux 跑。
2. **腾讯云默认不放行 7777**。安全组（轻量服务器叫"防火墙"）没开端口时，服务端本地监听正常但公网连不上，是本项目部署后最常见的"连不上"根因。

已核对源码的部署参数（非推测）：

| 项目      | 取值                                                                  | 出处                           |
| ------- | ------------------------------------------------------------------- | ---------------------------- |
| 目标框架    | `net8.0`                                                            | `KitchenChaos-Server.csproj` |
| 输出类型    | `Exe`（控制台）                                                          | 同上                           |
| 程序集名    | `KitchenChaos-Server`                                               | 同上                           |
| 监听地址    | `IPAddress.Any`（0.0.0.0）                                            | `GameServer.Start()`         |
| 默认端口    | `7777`，可用命令行首参覆盖                                                    | `Program.cs`                 |
| 外部依赖    | **无** NuGet 包、**无** 运行时数据文件                                         | csproj + 全量静态扫描              |
| 并发上限    | `MAX_PLAYERS = 16`                                                  | `GameServer`                 |
| 心跳超时    | 普通客户端 10 s；调试客户端 600 s（可用环境变量 `KC_DEBUG_CLIENT_TIMEOUT` 覆盖）         | `GameServer`                 |
| 客户端连接入口 | `Assets/Scripts/Applications/Launch.cs` 的 `serverIp` / `serverPort` | 客户端源码                        |

因为服务端**零外部依赖、零数据文件**，部署本质上就是"传一个文件 + 起一个进程"，不涉及数据库、环境变量注入或配置热加载。

---

## 1. 第一步：放行 TCP 7777（先做，别跳过）

这一步不做，后面全白做。按你实际购买的产品类型选一条路径：

### 1.1 轻量应用服务器（Lighthouse）

控制台 → 轻量应用服务器 → 选中实例 → **防火墙** → **添加规则**：

| 字段   | 值                  |
| ---- | ------------------ |
| 应用类型 | 自定义                |
| 协议   | TCP                |
| 端口   | `7777`             |
| 来源   | `0.0.0.0/0`（对全网开放） |
| 策略   | 允许                 |
| 备注   | KitchenChaos 游戏服   |

### 1.2 云服务器 CVM

控制台 → 云服务器 → 实例 → **安全组** → 编辑规则 → **入站规则** → 添加：

| 类型  | 来源          | 协议端口     | 策略 |
| --- | ----------- | -------- | -- |
| 自定义 | `0.0.0.0/0` | TCP:7777 | 允许 |

> **可用性提醒**：对全网开放 7777 意味着任何人都能发起连接。当前服务端没有连接鉴权（`Connect` 只带一个玩家名），在被恶意扫描/灌连接时可能触发大量异常日志。学习/测试阶段可以接受；若要长期暴露，建议后续加"来源 IP 白名单"或服务端握手口令。
>
> 另外 7777 是常见端口（Unreal 系默认、被扫描概率较高）。如果希望降低噪音，可在服务端启动参数里换个高位端口（如 `23115`），客户端同步修改即可——`Program.cs` 已支持。

### 1.3 自查：规则到底生效了没（一分钟判定法）

改完规则后，从**本机 PowerShell** 探测目标端口，并**同时探测两个对照组**——"失败的方式是否相同"直接告诉你问题在哪一层：

```powershell
Test-NetConnection -ComputerName 106.52.92.221 -Port 7777     # 目标
Test-NetConnection -ComputerName 106.52.92.221 -Port 65535    # 对照组（必然关闭）
Test-NetConnection -ComputerName 106.52.92.221 -Port 22       # 对照组（必然开放）
```

| 7777 的结果                       | 与对照组对比                 | 结论                                                           |
| ------------------------------ | ---------------------- | ------------------------------------------------------------ |
| `TcpTestSucceeded : True`      | —                      | 端口已放行；只需再确认服务在跑（§5.4 / §6 第 1 层）                             |
| **超时**、长时间无响应                  | **与 65535 完全一致**       | 报文被**静默丢弃** → **安全组/防火墙这一层没放行**（或被 ufw 拦下），与"服务有没有起"无关       |
| **立即被拒**（`Connection refused`） | **与 22 一致、与 65535 不同** | 报文已到达主机并被内核拒绝 → 放行**已生效**，是**没有程序监听 7777**（服务没起 / 崩了 / 端口不对） |

> ⚠️ **别把这两者搞反**：腾讯云安全组对未放行的端口是 **DROP（静默丢弃）**，不是 REJECT，所以客户端看到的是**超时**；而"已放行但无服务"应由主机内核回 **RST**，客户端看到的是**立即被拒**。常见误判是把"超时"当成"服务没起来"，于是反复重启服务——方向完全错了。
>
> **本次实测记录（2026-10-09 21:14，从本机探测）**：  
> `22 → OPEN 0.01s`、`7777 → TIMEOUT 8.00s`、`65535 → TIMEOUT 8.00s`。7777 与 65535 表现**完全一致** → 判定为**包过滤拦截**，即安全组/防火墙未放行 7777。

---

## 2. 连接服务器

### 2.1 从本地 Windows 直连（推荐）

Windows 10/11 自带 OpenSSH，直接在 Git Bash 或 PowerShell 里执行：

```bash
ssh ubuntu@106.52.92.221
```

- 若购买时绑定了密钥对，指定私钥文件：`ssh -i C:/Users/CHF/.ssh/你的私钥.pem ubuntu@106.52.92.221`
- 若不知道密码：控制台实例右侧 **「登录」** 按钮 → 选择重置密码 / 使用 VNC 登录。
- **腾讯云 Ubuntu 镜像默认登录用户通常是 `ubuntu`**（不是 root）。若你的实例是 `root`，后续所有 `sudo` 都可去掉。

### 2.2 用控制台的 Web 终端

实例右侧 **「登录」** → 网页版 VNC/SSH 终端。适合首次登录、改密码、或在本地网络受限时使用。缺点是粘贴长命令不便，且断开即丢失会话。

### 2.3 首次登录后的环境体检

```bash
# 系统版本（决定 .NET 运行时安装方式，自包含发布则无所谓）
lsb_release -a

# 确认未安装任何 .NET 时可跳过；有输出说明是框架依赖部署
dotnet --list-runtimes 2>/dev/null || echo "未安装 .NET，采用自包含方案即可"

# 系统防火墙状态：腾讯云镜像通常为 inactive，若为 active 需放行
sudo ufw status

# 确认 7777 当前空闲
ss -tlnp | grep -w 7777 || echo "7777 空闲"
```

**若 `ufw status` 显示 `active`**，追加：

```bash
sudo ufw allow 7777/tcp
sudo ufw reload
```

---

## 3. 本地发布（Windows 侧操作）

服务端**没有** NuGet 依赖，且 .NET 支持跨平台发布，因此**可以直接在 Windows 上编译出 Linux 可执行文件**，无需在云主机上装 SDK 或源码。

### 3.1 环境确认

```bash
dotnet --list-sdks
# 本机已实测：10.0.302 / 10.0.401，可正常编译 net8.0 目标
```

若提示找不到 `dotnet`，先装 .NET 8 SDK：<https://dotnet.microsoft.com/download/dotnet/8.0>

### 3.2 发布命令（自包含，推荐）

在仓库根目录 `F:/UnityStudyProject/KitchenChaos-Server` 执行（Git Bash / PowerShell 用反斜杠续行）：

```bash
cd /f/UnityStudyProject/KitchenChaos-Server

dotnet publish KitchenChaos-Server/KitchenChaos-Server.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/linux-x64
```

单行版本（cmd.exe 用 `^` 续行，或用一行写完）：

```
dotnet publish KitchenChaos-Server/KitchenChaos-Server.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/linux-x64
```

**本次已执行完毕**，产物在 `F:/UnityStudyProject/KitchenChaos-Server/publish/linux-x64/`：

| 文件                               | 大小      | 说明                          |
| -------------------------------- | ------- | --------------------------- |
| `KitchenChaos-Server`            | 66.8 MB | 自包含单文件可执行程序（本体）**← 只需上传这个** |
| `KitchenChaos-Server.pdb`        | 36 KB   | 调试符号，服务器上不需要                |
| `KitchenChaos-Server.dll.config` | 189 B   | 遗留配置文件，可不传                  |

> **`--self-contained true` 意味着什么**：.NET 8 运行时被整个打包进可执行文件，云主机上**不需要安装任何 .NET**。代价是文件偏大（66.8 MB，对 40 GB 系统盘不构成压力），收益是彻底消除"服务器没装对应运行时版本"这类问题。
>
> 若你更倾向精简体积（约 200 KB + 需装运行时），把 `--self-contained true` 换成 `false`，并按 §3.3 在服务器安装 .NET 8 运行时。

### 3.3 备选：框架依赖发布

```bash
dotnet publish KitchenChaos-Server/KitchenChaos-Server.csproj -c Release -r linux-x64 --self-contained false -o publish/linux-x64-fd
```

在**服务器上**安装 .NET 8 运行时（Ubuntu 22.04 / 24.04 均适用）：

```bash
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O /tmp/ms-prod.deb
sudo dpkg -i /tmp/ms-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-runtime-8.0
dotnet --list-runtimes | grep 8.0    # 应输出 Microsoft.NETCore.App 8.0.x
```

> **取舍**：自包含 = 简单、不易出错、占空间；框架依赖 = 体积小、但多一步环境依赖，且未来升级运行时版本有可能引入行为差异。**本项目推荐自包含。**

### 3.4 顺手把产物排除出版本控制

`publish/` 目录不应进 git。在 `.gitignore` 末尾追加一行：

```gitignore
publish/
```

---

## 4. 上传到服务器

### 4.1 用 scp（命令行，推荐）

```bash
# 在 Windows Git Bash 中执行；把 ~/.ssh/id_rsa 换成你的密钥路径（用密码登录则省略 -i）
scp -i ~/.ssh/id_rsa \
  "F:/UnityStudyProject/KitchenChaos-Server/publish/linux-x64/KitchenChaos-Server" \
  ubuntu@106.52.92.221:/tmp/KitchenChaos-Server
```

66.8 MB，视上行带宽通常十几秒到一两分钟。

### 4.2 用图形化工具

- **WinSCP** / **FileZilla** / **Xshell 的 XFTP**：SFTP 协议，主机 `106.52.92.221`，端口 22，用户名 `ubuntu`，密钥或密码认证。把文件拖到 `/tmp/` 即可。
- 上传目标先放 `/tmp/`，再由 §5 的脚本以 root 权限搬到 `/opt/`，避免因目录权限导致上传失败。

### 4.3 通过 Git 中转（可选）

服务端仓库 `origin` 为 <https://github.com/chen-hao-lang/KitchenChaos-Study.git>。若嫌 scp 麻烦，可提交源码后在服务器上装 .NET 8 **SDK** 直接 `dotnet publish`。缺点是多装一个几百 MB 的 SDK，2C2G 上编译也偏慢。**不推荐**，仅作备选。

---

## 5. 部署并托管服务

### 5.1 【关键】修正 `Program.cs` 的退出阻塞

**问题**：`Program.cs` 第 31–32 行：

```csharp
Console.WriteLine("服务器已启动，按任意键关闭...");
Console.ReadKey();
```

在 systemd 托管下进程没有交互终端，`Console.ReadKey()` 会抛：

```
System.InvalidOperationException: Cannot read keys when either application does not have a console
or when console input has been redirected from a file. Try Console.Read.
```

该异常**未被捕获**（`try/catch` 只包住了 `server.Start()`），会导致 `Main` 直接抛出、进程退出，`server.Stop()` 也不会执行。表现就是：`systemctl start` 显示成功，几秒后进程消失，`journalctl` 里能看到上述堆栈。

**修正方案**：把"按任意键"换成"等待退出信号"，同时获得优雅停机能力。

```csharp
// 文件头补上 using
using System;
using System.Threading;          // ← 新增

// ... 中间不变 ...

            Console.WriteLine("服务器已启动，按 Ctrl+C 或发送 SIGTERM 停止。");

            // systemd 无交互终端，Console.ReadKey() 会抛异常；改为等待退出信号。
            // Ctrl+C（前台调试）与 SIGTERM（systemd 停止）都能触发，随后走优雅停机。
            using (var exitSignal = new ManualResetEventSlim(false))
            {
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;          // 阻止默认的强制终止
                    exitSignal.Set();
                };
                AppDomain.CurrentDomain.ProcessExit += (_, _) => exitSignal.Set();
                System.Runtime.Loader.AssemblyLoadContext.Default.Unloading += _ => exitSignal.Set();

                exitSignal.Wait();
            }

            server.Stop();
            Console.WriteLine("服务器已关闭。");
```

改完后**必须重新执行 §3.2 发布命令**，再用新产物覆盖服务器上的旧文件。

> **不改代码的替代路径**：用 `tmux` / `screen` 启动，它们会分配伪终端（pty），`Console.ReadKey()` 正常工作。详见 §5.6。适合"先跑起来看看"，但不具备开机自启和崩溃自愈，不建议长期使用。

### 5.2 在服务器上安装与授权

```bash
# 1) 建立部署目录
sudo mkdir -p /opt/kitchenchaos-server

# 2) 搬运可执行文件
sudo mv /tmp/KitchenChaos-Server /opt/kitchenchaos-server/KitchenChaos-Server

# 3) 赋予执行权限（scp 上传后默认没有 x 位，这一步漏了会报 Permission denied）
sudo chmod +x /opt/kitchenchaos-server/KitchenChaos-Server

# 4) 单文件自包含包的原生库解压目录（与 unit 文件中的环境变量对应）
sudo mkdir -p /opt/kitchenchaos-server/.bundle

# 5) 若用 ubuntu 用户托管（非 root），把目录归属交给它
sudo chown -R ubuntu:ubuntu /opt/kitchenchaos-server

# 6) 校验
ls -l /opt/kitchenchaos-server/
file /opt/kitchenchaos-server/KitchenChaos-Server   # 应显示 ELF 64-bit LSB executable, x86-64
```

### 5.3 前台试跑一次（强烈建议）

先手动跑，确认程序本身没问题，再交给 systemd：

```bash
sudo -u ubuntu /opt/kitchenchaos-server/KitchenChaos-Server 7777
```

期望输出：

```
=== KitchenChaos 服务器 ===
正在启动，监听端口: 7777
服务器已启动，按 Ctrl+C 或发送 SIGTERM 停止。
```

另开一个终端确认端口已监听：

```bash
ss -tlnp | grep -w 7777
# 期望：LISTEN 0 512 0.0.0.0:7777 ... users:(("KitchenChaos-Ser",pid=...,fd=...))
```

确认无误后 `Ctrl+C` 退出。

### 5.4 注册为 systemd 服务

已将配置好的 unit 文件放在 `F:/UnityStudyProject/KitchenChaos-Server/deploy/kitchenchaos-server.service`。上传并启用：


```bash
# 上传（在本地 Windows 执行）
scp -i ~/.ssh/id_rsa \
  "F:/UnityStudyProject/KitchenChaos-Server/deploy/kitchenchaos-server.service" \
  ubuntu@106.52.92.221:/tmp/

# 以下在服务器执行
sudo mv /tmp/kitchenchaos-server.service /etc/systemd/system/
sudo chmod 644 /etc/systemd/system/kitchenchaos-server.service

sudo systemctl daemon-reload
sudo systemctl enable kitchenchaos-server     # 开机自启
sudo systemctl start  kitchenchaos-server     # 立即启动
sudo systemctl status kitchenchaos-server --no-pager
```

`status` 应显示 `Active: active (running)`。

**若服务器上用户名不是 `ubuntu`**（例如用 root），编辑 `/etc/systemd/system/kitchenchaos-server.service`，把这两行删掉或改成实际用户：

```ini
User=ubuntu
Group=ubuntu
```

**改完记得 `sudo systemctl daemon-reload && sudo systemctl restart kitchenchaos-server`。**

### 5.5 查看日志

```bash
# 实时跟随
journalctl -u kitchenchaos-server -f

# 最近 100 行
journalctl -u kitchenchaos-server -n 100 --no-pager

# 只看本次启动以来的错误
journalctl -u kitchenchaos-server -p err -b --no-pager

# 重启 / 停止
sudo systemctl restart kitchenchaos-server
sudo systemctl stop    kitchenchaos-server
```

因为 unit 里配了 `Restart=always` / `RestartSec=3`，进程意外退出会被自动拉起。**注意**：如果是因为端口被占用而启动失败，会陷入"3 秒一次的重启循环"，`journalctl` 里会反复出现 `无法监听端口 7777...`。这时应先 `systemctl stop` 再排查。

### 5.6 备选：tmux 方式（不改代码）

```bash
sudo apt-get update && sudo apt-get install -y tmux

# 新建名为 kc 的会话并启动服务
tmux new -s kc
# 会话内执行：
cd /opt/kitchenchaos-server && ./KitchenChaos-Server 7777
# 按 Ctrl+B 然后按 D 脱离（程序继续运行）

tmux ls                 # 查看会话
tmux attach -t kc       # 重新接入
```

缺点：不开机自启、崩溃不自动恢复、机器重启后需手动重来。**仅用于临时验证。**

---

## 6. 验证部署（从外到内四层）

按顺序逐层验证，能快速定位问题出在哪一层：

### 第 1 层：服务端进程与端口

```bash
sudo systemctl status kitchenchaos-server --no-pager | head -5
ss -tlnp | grep -w 7777
```

### 第 2 层：服务器本机可达

```bash
# 本机自连（验证程序能接受连接，与防火墙/安全组无关）
nc -zv 127.0.0.1 7777
# 或
telnet 127.0.0.1 7777
```

### 第 3 层：公网可达（在**你的 Windows** 上执行）

```bash
nc -zv 106.52.92.221 7777
# 或（Windows 若无 nc）
Test-NetConnection -ComputerName 106.52.92.221 -Port 7777    # PowerShell
```

- **不通** → 99% 是 §1 的安全组/防火墙没放行，或 ufw 未放行。回 §1。
- **通** → 安全组已正确。

> 也可用第三方端口探测站点（如 `tool.chinaz.com/port`）从公网视角验证 7777 是否开放，排除本地网络策略干扰。

### 第 4 层：客户端连通

修改客户端连接地址 —— `Assets/Scripts/Applications/Launch.cs`：

```csharp
public string serverIp = "106.52.92.221";   // 原值 127.0.0.1
public int    serverPort = 7777;
```

然后在 Unity 编辑器（或打包后的客户端）里启动 MainMenuScene，观察：

- Unity Console 出现 `[NetworkManager] 已连接到106.52.92.221:7777`；
- 服务端 `journalctl -u kitchenchaos-server -f` 出现新客户端接入日志。

> **离线模式注意**：`Launch.cs` 还有 `ePlayMode` 字段，默认是 `OfflinePlayMode`（离线模式，走本地资源）。要连远程服务器需切到 `HostPlayMode`。若同时使用 `HostPlayMode` 拉取热更资源，还需要处理 §7 的 CDN 地址问题。

---

## 7. 配套项：YooAsset 热更 CDN 地址

这条**不是游戏服本身的部署**，但如果你的客户端跑 `HostPlayMode`，资源是从一个 HTTP 服务器拉取的，与服务端是两套东西，需要独立处理。

当前地址硬编码在 `Assets/Scripts/FrameWork/PatchLogic/FSMState/FSMInitializePackage.cs`：

```csharp
string hostServerIP = "https://127.0.0.1";   // GetHostServerURL()
```

`127.0.0.1` 在玩家机器上指向玩家本机，必然失败。需要改成一个**公网可访问的 HTTPS 地址**：

- **快速方案**：在同一台云服务器上用 Nginx 托管 `Assets/Bundles`（或你 YooAsset 的输出目录），把 `hostServerIP` 改成 `https://106.52.92.221`（**需备案域名 + 证书才能用 HTTPS 域名**；学习阶段可用 `http://106.52.92.221` 但要注意 YooAsset 对 HTTP 的兼容与 Unity WebGL 的混合内容限制）。
- **正式方案**：接对象存储 + CDN（腾讯云 COS + CDN），把 `hostServerIP` 指向 CDN 域名。

> ⚠️ **HTTPS 提示**：`hostServerIP` 现在是 `https://` 前缀。用公网 IP 直连 `https://` 会因证书不匹配失败。要么改成 `http://`，要么配好域名证书。若只是验证联机逻辑，建议先用离线模式或把资源包指向已配置好的既有域名。

---

## 8. 日常运维

### 8.1 更新版本（改完代码后的标准流程）

```bash
# 本地：重新发布
cd /f/UnityStudyProject/KitchenChaos-Server
dotnet publish KitchenChaos-Server/KitchenChaos-Server.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/linux-x64

# 本地：上传覆盖
scp -i ~/.ssh/id_rsa publish/linux-x64/KitchenChaos-Server ubuntu@106.52.92.221:/tmp/

# 服务器：停服 → 替换 → 起服
sudo systemctl stop kitchenchaos-server
sudo mv /tmp/KitchenChaos-Server /opt/kitchenchaos-server/KitchenChaos-Server
sudo chmod +x /opt/kitchenchaos-server/KitchenChaos-Server
sudo chown ubuntu:ubuntu /opt/kitchenchaos-server/KitchenChaos-Server
sudo systemctl start kitchenchaos-server
sudo systemctl status kitchenchaos-server --no-pager | head -5
```

> **注意**：`sudo mv` 覆盖正在运行的程序文件会得到 `Text file busy` 错误，所以必须**先 stop**。
>
> **注意**：替换文件会中断所有在线玩家（服务端进程重启，房间状态全丢）。本项目房间状态在内存中，没有持久化，重启后所有房间清空。建议在无人对局时更新。

### 8.2 端口调整

改端口只需两处同步：

```bash
# 服务器：启动参数与 unit 文件同时改
sudo vim /etc/systemd/system/kitchenchaos-server.service   # ExecStart 末尾的 7777
sudo systemctl daemon-reload && sudo systemctl restart kitchenchaos-server
```

同步在腾讯云防火墙/安全组放行新端口，并修改客户端 `Launch.cs` 的 `serverPort`。

### 8.3 资源与容量

- 实例规格：2 核 2 GB。服务端为单进程、异步 TCP + 心跳线程，**无外部依赖、无文件 I/O**，`MAX_PLAYERS = 16`，内存占用很低。2C2G **足够**，无需扩容。
- 磁盘：部署目录约 67 MB，40 GB 系统盘余量充足。日志走 journald，可在 `/etc/systemd/journald.conf` 里用 `SystemMaxUse` 限制总量，避免长期运行占满磁盘。
- 连接数：需提高文件描述符上限时，unit 文件里已有 `LimitNOFILE=65535`。

### 8.4 时区与时间

```bash
timedatectl                    # 查看当前时区
sudo timedatectl set-timezone Asia/Shanghai
```

让日志时间戳与本地一致，便于对照对局时间排障。

### 8.5 安全建议

- 已启用 `NoNewPrivileges` / `ProtectSystem=full` / `ProtectHome` / `PrivateDevices`，进程权限被限制在最小集合。
- 服务以非 root 的 `ubuntu` 用户运行，避免服务端漏洞直接升级为系统级权限。
- 7777 对全网开放且服务端无鉴权，若观察到大量异常连接日志，优先考虑加来源白名单。
- SSH 建议只用密钥登录，关闭密码登录（`/etc/ssh/sshd_config` 中 `PasswordAuthentication no`）。
- 该实例 **2026-11-03 14:02:55 到期**，请提前续费，否则服务会随实例释放一起消失。

### 8.6 卸载还原

```bash
sudo systemctl stop kitchenchaos-server
sudo systemctl disable kitchenchaos-server
sudo rm /etc/systemd/system/kitchenchaos-server.service
sudo systemctl daemon-reload
sudo rm -rf /opt/kitchenchaos-server
```

---

## 9. 故障速查表

| 现象                                                        | 根因                                                         | 处置                                                           |
| --------------------------------------------------------- | ---------------------------------------------------------- | ------------------------------------------------------------ |
| `systemctl status` 显示 active，几秒后变 failed / 进程消失           | `Console.ReadKey()` 在无 TTY 下抛异常                            | 按 §5.1 改代码后重新发布，或改用 tmux（§5.6）                               |
| `journalctl` 反复出现 `无法监听端口 7777`                           | 端口被占用，或两个实例同时在跑                                            | `ss -tlnp \| grep 7777` 找到占用进程；`systemctl stop` 后排查          |
| 服务器本机 `nc 127.0.0.1 7777` 通，公网 `nc 106.52.92.221 7777` 不通 | 腾讯云安全组/防火墙未放行，或 ufw 未放行                                    | §1 添加 TCP 7777 入站规则；`sudo ufw allow 7777/tcp`                |
| `Permission denied` 无法执行                                  | 缺执行权限                                                      | `sudo chmod +x /opt/kitchenchaos-server/KitchenChaos-Server` |
| `Text file busy` 替换文件失败                                   | 程序正在运行                                                     | 先 `systemctl stop`，再替换，再 `start`                             |
| 客户端报连接失败/超时                                               | `Launch.cs` 仍是 `127.0.0.1`；或 `ePlayMode` 仍是离线模式；或安全组未放行    | 改 `serverIp` 为公网 IP；切到 `HostPlayMode`；回 §1                   |
| 能连上但一进对局就报错                                               | 服务端版本与客户端协议不一致（本项目两侧各自维护 `MessageType` / `NetworkMessage`） | 重新发布两边，确保源码同步                                                |
| 玩家频繁掉线                                                    | 客户端心跳未按时发送 / 网络抖动                                          | 服务端普通客户端超时 10 s；调试期可放宽 `KC_DEBUG_CLIENT_TIMEOUT`             |
| 运行 `dotnet publish` 报 `NETSDK1045` 或找不到目标框架               | 本机未装 .NET 8 SDK（只有更高版本且缺 net8.0 参考包）                       | 装 .NET 8 SDK，或改用已装版本并调整 `TargetFramework`（**不推荐**）           |

---

## 10. 执行清单（可直接照做）

- [x] **1.** 腾讯云控制台放行 **TCP 7777** 入站（轻量=防火墙 / CVM=安全组）
- [x] **2.** 本地确认 `dotnet --list-sdks` 有可用 SDK
- [ ] **3.** 按 §5.1 修正 `Program.cs` 的 `Console.ReadKey()`（若打算用 systemd）
- [ ] **4.** `dotnet publish ... -r linux-x64 --self-contained true -p:PublishSingleFile=true`
- [ ] **5.** `scp` 把 `publish/linux-x64/KitchenChaos-Server` 传到 `/tmp/`
- [ ] **6.** 服务器：`mkdir -p /opt/kitchenchaos-server/.bundle` → 搬运 → `chmod +x` → `chown ubuntu:ubuntu`
- [ ] **7.** 前台试跑一次，确认输出正常且 `ss -tlnp` 能看到 0.0.0.0:7777
- [ ] **8.** 安装 `deploy/kitchenchaos-server.service` 到 `/etc/systemd/system/`，`daemon-reload` + `enable` + `start`
- [ ] **9.** 本地 `nc -zv 106.52.92.221 7777` 验证公网可达
- [ ] **10.** 改客户端 `Launch.cs` 的 `serverIp`，进游戏确认连接成功

---

## 附：本指南涉及的文件

| 路径                                                                                        | 作用                                      |
| ----------------------------------------------------------------------------------------- | --------------------------------------- |
| `KitchenChaos-Server/publish/linux-x64/KitchenChaos-Server`                               | 已编译好的 Linux 自包含可执行文件（66.8 MB）           |
| `KitchenChaos-Server/deploy/kitchenchaos-server.service`                                  | systemd 托管配置，上传到 `/etc/systemd/system/` |
| `KitchenChaos-Server/DEPLOY_Ubuntu.md`                                                    | 本指南                                     |
| `KitchenChaos-Server/KitchenChaos-Server/Program.cs`                                      | 需按 §5.1 修改退出阻塞                          |
| `KitchenChaos-Study/Assets/Scripts/Applications/Launch.cs`                                | 需改 `serverIp` / `serverPort`            |
| `KitchenChaos-Study/Assets/Scripts/FrameWork/PatchLogic/FSMState/FSMInitializePackage.cs` | 热更 CDN 地址（§7）                           |
