# Jellyfin Thunder Subtitle

为 Jellyfin 影视库自动补全中文外挂字幕，**由 AI 大模型在候选字幕之间择优**。

当迅雷字幕库对同一部影片返回多份中文字幕时，插件会下载全部候选，逐条解码、校验格式、
统计台词量，再交由大模型横向比较，选出最接近人工翻译的一份，写成
`<片名>.chi.srt` / `<片名>.chi.ass` 并与视频文件放在同一目录 —— Jellyfin 会将其识别为**外部字幕**，
播放时可直接选择。

面向 Jellyfin **12.2.0**（`targetAbi = 12.2.0.0`，`net10.0`）。

---

## 特色

### AI 大模型择优

同一部影片在迅雷上往往存在十余份中文字幕：人工翻译、AI 机翻，以及同一份字幕的不同转码版本。
**台词条数多并不代表翻译质量高** —— 机翻通常更长（语气词也被逐字直译），但语义与原片偏差较大。

因此插件并非简单地选择体积最大的候选，而是：

1. 先按**台词条数分档**，筛选出一批候选；
2. 将这些候选的**台词抽样**放入同一个请求，由大模型横向比较；
3. 模型逐份评分，标记出机翻，并给出简短理由；
4. 按模型的判断重新排序，取首位写入文件。

> 在一组 3 份候选中，模型能够识别出「第 2 份虽台词最多，但属于逐字直译」这类信息 ——
> 这是任何基于体积、条数或文件名的规则都无法做到的。

AI 仅负责**在候选之间排序**，不做绝对阈值判断。连接失败、超时或返回格式异常时，
自动回退到按字符数排序，**不会影响字幕下载**。

### 两种检索方式，每个影视库各选一种

| 方式 | 适用范围 | 查询词 |
|---|---|---|
| **片名搜索** | 普通影视（电影 / 电视剧 / 动漫 / 综艺） | 电影：`片名 + 年份`；剧集：`剧名 + SxxExx` |
| **番号搜索** | 按番号命名的影片（如 `ABF-363`） | 从文件名提取的番号，并丢弃番号不符的候选 |

同一 Jellyfin 实例中可同时存在这两类内容：每个影视库各选一种，互不影响。

### 每个影视库独立控制自动化

并非每个影视库都需要自动下载 —— 大部分影片本身已自带字幕。因此每个影视库提供三个独立选项：

- **检索方式**：番号搜索 / 片名搜索
- **自动补全**：是否由定时任务与实时入库自动为该库下载字幕
- **自动择优**：是否由「自动择优」任务重新比较该库已有的插件字幕

当所有影视库都未开启对应选项时，任务会直接跳过并记录一行日志，不会对全库执行无意义的扫描。

### 先预览，再决定

Jellyfin 的「搜索字幕」中，每条结果后附带两个按钮：

- **预览**（眼睛图标）：在弹窗中查看完整字幕，含时间轴、可切换自动换行，**不会写入影片目录**；
  确认合适后，点击弹窗中的「使用这份字幕」即可保存。
- **下载**（↓ 图标）：直接保存至影片目录。

预览的字幕全文仅保存在**服务器内存**中（30 分钟过期、最多 24 份、重启即清空），
任何情况下都不会写入磁盘。

### 不覆盖用户已有的字幕

- 网盘同步产生的 `<片名>.srt` / `<片名>.zh.srt`：**既不覆盖，也不删除**
- 视频内嵌字幕：视为「已有字幕」，跳过
- 插件仅管理自身生成的 `<片名>.chi.<ext>`

---

## 两个计划任务

| 任务 | Key | 默认触发 | 作用 |
|---|---|---|---|
| 迅雷字幕 · 自动补全 | `ThunderSubtitleFill` | 每 6 小时 | 对开启了「自动补全」的影视库中**完全没有字幕**的条目，检索并下载最合适的字幕 |
| 迅雷字幕 · 自动择优 | `ThunderSubtitleRepick` | 每周日 04:00 | 对开启了「自动择优」的影视库中**已有插件字幕**的条目重新比较，仅在更优时替换 |

两个任务共用同一套检索 / 下载 / 过滤 / 排序逻辑，与手动「搜索字幕」完全一致 ——
因此自动任务选中的候选，与弹窗中排在第 1 条的结果始终相同。

### 实时补字幕

新条目入库并完成刮削后，插件会在 30 秒内为其补全一次字幕，无需等待定时任务：

- 每 30 秒查询一次「最近 6 小时入库且已完成刮削」的条目
- 仅处理这些新增条目，不扫描全库
- 仅处理开启了「自动补全」的影视库
- 定时任务保留作为补充机制（覆盖遗漏条目，或实时流程被全量任务占用时）

---

## 安装

### 方式 A：通过插件仓库（推荐，可自动更新）

控制台 → 插件 → 仓库 → 添加：

```
https://raw.githubusercontent.com/Louis-15/Jellyfin-Thunder-Subtitle/main/manifest.json
```

然后在「目录」中搜索 **Thunder Subtitle** 并安装，安装完成后重启 Jellyfin。
之后插件页面出现更新提示时点击「更新」即可升级，无需再手工传输文件。

### 方式 B：手工安装（服务器无法访问 GitHub 时）

请先备份插件目录；重启 Jellyfin 会造成数十秒不可用。

从 Release 页面下载 `thunder-subtitle_<version>.zip`，解压至 Jellyfin 配置目录下的
`plugins/ThunderSubtitle_<version>/`：

```bash
# 以 Docker 部署为例：将 /path/to/jellyfin/config 替换为映射给容器的配置目录
CONFIG=/path/to/jellyfin/config
VERSION=1.0.0.0
mkdir -p "$CONFIG/plugins/ThunderSubtitle_$VERSION"
unzip -o thunder-subtitle_$VERSION.zip -d "$CONFIG/plugins/ThunderSubtitle_$VERSION"
docker restart jellyfin   # 容器名请按实际情况填写

# 确认加载
docker logs --tail 200 jellyfin 2>&1 | grep -i "Thunder Subtitle"
```

- **Docker**：上述 `/path/to/jellyfin/config` 为**宿主机**上映射至容器 `/config` 的目录；
- **NAS（群晖 / 威联通 / UGREEN 等）**：无需 SSH，在 NAS 的文件管理器中将 zip 解压至映射出的
  `config/plugins/ThunderSubtitle_<version>/`，随后在容器管理界面重启 Jellyfin 即可。

插件目录内必须直接包含 `Jellyfin.Plugin.ThunderSubtitle.dll`、`meta.json` 与 `logo.png`，
不可多套一层文件夹，也不要放入 Jellyfin 自身的 dll（项目已设置 `CopyLocalLockFileAssemblies=false`）。

---

## 配置

控制台 → 插件 → Thunder Subtitle。

### 目标影视库

勾选需要本插件处理的影视库；每个库后有三个下拉框（未勾选的库同样显示当前设置）：

| 下拉框 | 取值 | 默认 | 说明 |
|---|---|---|---|
| 检索方式 | 番号搜索 / 片名搜索 | 片名搜索 | 番号库按 `ABF-363` 检索；普通影视库按片名 + 年份 / `S01E02` 检索 |
| 自动补全 | 是 / 否 | 否 | 定时任务与实时入库是否自动为该库下载字幕 |
| 自动择优 | 是 / 否 | 否 | 「自动择优」任务是否重新比较该库已有的 `.chi.*`；自动补全为「否」时该项隐藏 |

### 下载与筛选

| 配置项 | 默认 | 说明 |
|---|---|---|
| 启用插件 | 开 | 关闭后两个任务均不再下载 |
| 单次上限 | 5 | 每次运行最多处理的条目数（仅统计实际发起检索的） |
| 最多候选数 | 25 | 每个条目最多下载多少份候选参与比较 |
| 请求间隔 | 500 ms | 两次**检索**之间的等待时间 |
| 下载并发数 | 4 | 同时下载的候选数量（1~8） |
| 单部影片下载预算 | 90 秒 | 到时后使用已下载的候选排序，不再等待响应缓慢的 CDN |
| 优先语言 | 简体中文 | 简体与繁体均接受，优先选择设定的语种 |
| 日志级别 | 常规 | 错误 / 警告 / 常规 / 调试（逐条候选） |

---

## AI 评审

### 送审范围的确定方式

仅按「台词最多」取前几名并不充分：条数最多的那批往往只是同一份机翻的不同转码，
真正的人工翻译会被排除在评审范围之外。因此改为**按条数分档**：

1. 候选按「条数」从多到少分档，相差 **2%** 以内视为同一档（500 与 504 同档，500 与 480 不同档）
2. 前 **3** 档，每档选取 **2 份**（档内按质量排序取靠前的若干份）
3. **不足则补**：档位过少时（例如条数分布分散，每档仅 1 份），从第 4 档起按档位顺序依次补充，
   直至达到 6 份（= 每档份数 × 3 档），上限 12 份

由此，即使条数最多的那批全部是同一份机翻的副本，排在后面的人工翻译仍可进入评审范围。

### 提交给模型的内容

- 仅提交**台词文本**，不含时间轴（节省 token，也避免模型以时间码作为判断依据）
- 每份候选抽取 **24 条**对白、最多 **3000 字**，覆盖整部影片而非只取开头
- 模型返回 `{"candidates":[{"id":1,"score":85,"machine":false,"reason":"…"}]}`，
  插件仅用于重新排序

成本参考：3 份候选约 6k token / 部。

### 配置项

| 配置项 | 默认 | 说明 |
|---|---|---|
| 启用 AI 评审 | 关 | 每个条目发起一次请求，取样候选置于同一请求内 |
| 供应商 | **Open Code（内置）** | 见下文；也可切换为「自定义」 |
| API Base URL | — | 仅「自定义」需要填写 |
| 模型名 | `deepseek-v4.1-flash` | Open Code 可使用 `glm-5.3-flash`、`kimi-k3` 等 |
| API Key | 空 | 以 `Authorization: Bearer` 发送，不写入日志 |
| 每档评审份数 | 2 | 1~3；档数固定为 3 |
| 超时 | 60 秒 | 超时后回退至按字符数排序 |

### 供应商

**Open Code（内置）**

Base URL 固定为 `https://opencode.ai/inference/go/openai/v1`，无需填写。

按 [OpenCode Go 文档](https://opencode.ai/docs/go/) 的要求，每个请求都需携带稳定的会话 ID：

> Send a stable session ID in `x-opencode-session` for each conversation so we can
> optimize routing and prompt caching.

插件会自动附加该请求头，取值形如 `<本次运行 GUID>-<条目 ID>`，同一部影片在同一次运行内保持不变。
客户端使用插件自身的 User-Agent（`Jellyfin.Plugin.ThunderSubtitle/x.y.z`），文档同样要求不使用通用 SDK 名称。

**自定义（任意 OpenAI 兼容端点）**

填写自己的 Base URL 即可，此时不会发送 `x-opencode-session`。本地模型示例：

```
Base URL: http://127.0.0.1:11434/v1
模型名:   qwen2.5:14b
API Key:  （留空）
```

**隐私说明**：开启 AI 评审后，插件会将字幕抽样文本（部分对白）发送至所配置的服务端。
如对此有顾虑，可使用本地 Ollama / LM Studio，数据不会离开本机。

---

## 处理流程

1. **逐库枚举**：番号库检索 `Movie`，片名库检索 `Movie + Episode`
   （`CollapseBoxSetItems = false`，避免已加入合集的影片被隐藏）。
2. **生成查询词**：按该库配置的方式 —— 番号，或 `条目名 + 年份` / `剧名 + SxxExx`。
3. **检索**：请求迅雷字幕库。番号模式会以另一种前导零写法补充检索一次
   （索引将 `NIMA-086` 与 `NIMA-86` 视为两次不同的检索）；片名模式在无任何证据匹配时，
   退化为仅使用片名再次检索。
4. **下载并逐条校验**：
   - 解码：`utf-8-sig → utf-16(BOM) → utf-8 → gb18030 → big5 → shift_jis → windows-1252`，
     按 `汉字×3 - Latin1×2 - 异常字符×8` 打分选取最优编码；
   - 内容校验：必须为有效字幕文件（SRT 时间轴 / ASS `[Script Info]`+`Dialogue:` / `WEBVTT`），
     CDN 返回的错误页与 XML 将被丢弃；
   - 格式白名单 `srt / ass / ssa / vtt`，输出扩展名与内容一致（`.ass` 不会保存为 `.srt`）。
5. **语言过滤**：仅保留简体与繁体中文。该步骤必须位于排序之前 —— 英文每 KB 的字母数远高于中文，
   直接比较字符数会天然偏向英文。
6. **去重**：`md5(CRLF→LF 后的文本)`，同一份字幕的换行差异版本仅保留一份。
7. **排序**：
   - 番号模式先排除番号不符的候选（检索 `NIMA-86` 会返回 `NIMA-014`，其体积更大但属于其他影片）；
   - 片名模式先按**片名 / 季集 / 年份**证据分档，仅保留最强的一档，集数不符的候选直接丢弃；
   - 再按「机翻标记数升序」→「优先语言」→「**台词字符数**降序」排列。
   - 比较对象为剥离样式头、时间轴与 `{\pos}` 标签之后的**台词字符数**，而非文件字节数
     （同一份字幕保存为 `.ass` 会增大约 20%）。
8. **AI 评审（可选）**：见上一节。
9. **写入**：`<片名>.chi.<ext>`，先写临时文件再原子替换，并删除其它扩展名的旧 `.chi.*`。
   已有 `.chi.*` 时仅在确有改进时才替换：语言不符无条件替换；机翻标记减少直接替换；
   优先语言替换另一种中文形式；否则要求台词字符数多出 `max(30 字, 5%)`。
10. **刷新条目**：`ValidationOnly` + `ImageRefreshMode.None` —— 重新探测本地文件并挂载外挂字幕，
    但不会触发库设置中的 `SubtitleDownloadLanguages`，因此不会产生 `.chi.0.srt`。

---

## 安全边界

- 仅处理 `.chi.*`；网盘同步产生的 `<片名>.srt` / `<片名>.zh.srt` **永远不会被覆盖或删除**。
- 仅删除本插件命名的 `<片名>.chi.<ext>`（不含 Jellyfin 生成的 `<片名>.chi.0.srt` 冲突副本）。
- 下载仅允许 `*.xunlei.com` / `*.sandai.net` / `*.geilijiasu.com`，单文件上限 8 MB。
- 同一时间仅允许一个任务执行（信号量），自动补全与自动择优不会相互冲突。
- 幂等：内容相同直接跳过；**自动补全永远不会修改已有 `.chi.*`**，升级仅由「自动择优」完成。

---

## 日志

插件日志以北京时间开头，便于排查：

```
[2026-11-02 20:32:01 CST] 自动补全开始：影视库 1 个，条目 28 条，待处理 2 条，单次上限 20 条。
[2026-11-02 20:32:02 CST] MIUM-1457.strm [MIUM-1457]：候选 12 条，可下载 12 条，下载耗时 3.1 秒（并发 4）。
[2026-11-02 20:32:05 CST] MIUM-1457.strm [MIUM-1457]：最优「MIUM-1457.海南鸡字幕.37.9KB.srt」（简体，4241 字 / 928 条，.srt，AI标记 0）。
[2026-11-02 20:32:05 CST] MIUM-1457.strm [MIUM-1457]：写入「MIUM-1457.chi.srt」37896 B（utf-8 → utf-8）。
[2026-11-02 20:32:06 CST] MIUM-1457.strm：刷新后识别到 1 条字幕流，「MIUM-1457.chi.srt」已生效。
[2026-11-02 20:32:06 CST] 自动补全结束：处理 2 条，写入 2 条，保留 0 条，无可用字幕 0 条，失败 0 条，耗时 4.3 秒。
```

开启 AI 评审后，每份送审候选会额外输出一行 `AI #2「xxx.srt」88 分 口语自然`。

```bash
docker logs --tail 500 jellyfin 2>&1 | grep -i "CST\]"
```

将日志级别调至「调试」会输出每份候选的编码、字数、条数与机翻标记，便于排查某份候选未被选中的原因。

---

## 测试与自检

```powershell
# 单元测试
dotnet test tests\Jellyfin.Plugin.ThunderSubtitle.Tests -c Release

# 联网自检：直接请求真实 API，输出每份候选的编码 / 格式 / 字数 / 条数 / 机翻标记
dotnet run --project tools\SubtitleProbe -c Release -- ABF-363 MIUM-1457 FSDSS-272
```

`SubtitleProbe` 不依赖 Jellyfin，可在安装插件之前确认上游行为是否发生变化。

---

## 从源码构建

需要 .NET 10 SDK。

```powershell
# 编译并打包
.\build.ps1

# 未找到 dotnet 时指定路径
.\build.ps1 -Dotnet "D:\dotnet10\dotnet.exe"
```

产物：

```
artifacts/Jellyfin.Plugin.ThunderSubtitle_<version>/   # dll + meta.json + logo.png
artifacts/thunder-subtitle_<version>.zip               # 目录内容打平后的 zip
```

---

## 发布新版本（维护者）

```powershell
.\release.ps1 -Version 1.0.1.0 -Changelog "本次变更说明"
```

脚本会同步 csproj / meta.json 的版本号、提交并推送 `v1.0.1.0` 标签；
GitHub Actions 随后编译、执行测试、打包，并将**本次产物的 MD5** 写回 `manifest.json` 后提交，
最后发布 Release。由此保证 Jellyfin 下载的 zip 与 manifest 中的 checksum 始终一致。

---

## 目录结构

```
Jellyfin.Plugin.ThunderSubtitle/
├── Plugin.cs                     # 插件入口：名称、简介、配置页注册
├── PluginConfiguration.cs        # 配置模型（含每个影视库的设置）
├── PluginServiceRegistrator.cs   # DI 注册
├── Core/
│   ├── ReleaseCode.cs            # 番号提取（含 FC2 / HEYZO 等特殊形态）
│   ├── SearchTarget.cs           # 检索目标：番号 / 片名 + 年份 / 季集
│   ├── TitleMatch.cs             # 片名模式：候选与目标的证据分档
│   ├── LibraryContext.cs         # 条目或路径 → 所属影视库 → 检索方式 → 检索目标
│   ├── ThunderClient.cs          # 迅雷 API 与 CDN 下载（域名白名单、体积上限）
│   ├── SubtitleText.cs           # 编码探测、格式嗅探、去重哈希
│   ├── SubtitleContent.cs        # 解析 SRT/VTT/ASS，统计台词字符数
│   ├── SubtitleRanker.cs         # 证据分档、机翻标记与台词字符数排序、升级判定
│   ├── SubtitleFinder.cs         # 共享管线：检索 → 下载 → 过滤 → 排序 → AI 评审
│   ├── AiSubtitleJudge.cs        # AI 评审：抽样、prompt 构造、JSON 解析
│   ├── Sidecar.cs                # 命名规则、原子写入、旧扩展名清理
│   ├── ProvenanceStore.cs        # 字幕来源记录（供自动择优判断机翻标记）
│   ├── ItemWatchService.cs       # 实时入库监听
│   ├── PreviewTextCache.cs       # 预览全文的内存缓存
│   ├── ClientScriptInjectionFilter.cs  # 将预览按钮注入 Jellyfin Web
│   ├── PluginLog.cs              # 日志级别过滤与北京时间戳
│   └── SubtitleJob.cs            # 任务主体（按影视库分别执行补全 / 择优）
├── Api/PreviewController.cs      # 预览接口（仅读取内存，不写入磁盘）
├── Providers/ThunderSubtitleProvider.cs  # 注册为 Jellyfin 字幕提供者
├── Tasks/
│   ├── ThunderFillTask.cs
│   └── ThunderRepickTask.cs
└── Web/configPage.html           # 内置配置页
```

---

## 已知限制

- 番号搜索的影视库仅处理 `Movie`，且番号必须能从文件名识别；片名搜索的影视库同时处理 `Movie` 与 `Episode`。
- 片名搜索不具备番号那样的强制标识：中文片名检索出的候选常常以英文命名，此时无法核对片名，
  插件会按质量择优并在日志中告警，建议人工确认一次。
- 排序假设「台词字符数越多，字幕越完整」，该假设仅在同语言、同翻译流派内成立；跨流派依赖 AI 评审。
- AI 评审仅查看抽样的 24 条对白，无法识别时间轴错位的字幕。
- AI 评审结果不做缓存：同一候选在「自动择优」中会重新评审。
- 番号不符的候选一律丢弃：宁可跳过，也不将其他影片的字幕写入当前影片。
- 刷新使用 `ValidationOnly`，不会重新拉取远程元数据；`.strm` 指向的远程视频本身不会被探测。
- 上游无候选时不会回退到其他字幕站点。

---

## 许可

MIT
