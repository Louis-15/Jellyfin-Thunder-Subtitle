# Jellyfin Thunder Subtitle

给 Jellyfin 影视库自动补中文外挂字幕，**用 AI 大模型在候选之间择优**。

从迅雷字幕库搜到同一部片子的一堆中文字幕之后，插件会把它们全部下载下来，逐条解码、校验格式、
统计台词量，再让大模型横向读一遍，挑出最像人工翻译的那一份，写成 `<片名>.chi.srt` / `<片名>.chi.ass`
放在视频旁边 —— Jellyfin 会把它识别成**外部字幕**，播放时直接可选。

面向 Jellyfin **12.2.0**（`targetAbi = 12.2.0.0`，`net10.0`）。

---

## 特色

### AI 大模型择优

同一个片子，迅雷上常常有十几份中文字幕：有人工翻译、有 AI 机翻、有同一份的多个转码版本。
**台词条数多不代表翻译好** —— 机翻往往更长（每个语气词都被逐字翻出来），但读起来驴唇不对马嘴。

所以插件不只是"挑最大的那份"，而是：

1. 先按**台词条数分档**挑出一小批候选；
2. 把这些候选的**台词抽样**放进同一个请求，让大模型横向比较；
3. 模型逐份打分，标出哪份是机翻，并给出简短理由；
4. 按模型的判断重排，取第一份写盘。

> 在一组 3 份候选里，模型能读出「第 2 份虽然台词最多，但明显是逐字直译」这种信息 ——
> 这是任何基于体积、条数、文件名的规则都做不到的。

AI 只负责**在候选之间排序**，不做绝对阈值判断。连接失败、超时、返回格式不对时，
自动回退到字符数排序，**绝不影响字幕下载**。

### 两种检索方式，每个影视库各选一种

| 方式 | 适合 | 查询词 |
|---|---|---|
| **片名搜索** | 普通影视（电影 / 电视剧 / 动漫 / 综艺） | 电影：`片名 + 年份`；剧集：`剧名 + SxxExx` |
| **番号搜索** | 按番号命名的影片（如 `ABF-363`） | 从文件名提取的番号，并丢弃番号不符的候选 |

一个 Jellyfin 里同时有这两类内容也没问题：每个库各选一种，互不影响。

### 每个库独立控制自动化

不是每个库都需要自动下载 —— 大部分影片本来就自带字幕。所以每个影视库有三个独立开关：

- **检索方式**：番号搜索 / 片名搜索
- **自动补全**：是否让定时任务和实时入库自动为该库下载字幕
- **自动择优**：是否让「自动择优」任务重新比较该库已有的插件字幕

一个库都没开启对应开关时，任务直接跳过并写一行日志，不会空跑全库。

### 先预览，再决定

Jellyfin 的「搜索字幕」里，每条结果后面有两个按钮：

- **预览**（眼睛图标）：弹窗里读完整份字幕，带时间轴、可切换自动换行，**不会写进影片文件夹**；
  看完觉得好，点弹窗里的「使用这份字幕」即可。
- **下载**（↓ 图标）：直接保存。

预览的字幕全文只放在**服务器内存**里（30 分钟过期、最多 24 份、重启即清空），
任何情况下都不会落盘。

### 永远不碰你自己的字幕

- 网盘同步下来的 `<片名>.srt` / `<片名>.zh.srt`：**不覆盖、不删除**
- 视频内嵌字幕：视为"已有字幕"，跳过
- 插件只管理自己写的 `<片名>.chi.<ext>`

---

## 两个计划任务

| 任务 | Key | 默认触发 | 作用 |
|---|---|---|---|
| 迅雷字幕 · 自动补全 | `ThunderSubtitleFill` | 每 6 小时 | 对开启了「自动补全」的库里**完全没有字幕**的条目，搜索并下载最合适的字幕 |
| 迅雷字幕 · 自动择优 | `ThunderSubtitleRepick` | 每周日 04:00 | 对开启了「自动择优」的库里**已有插件字幕**的条目重新比较，更好才替换 |

两个任务共用同一套搜索 / 下载 / 过滤 / 排序逻辑，也和手动「搜索字幕」完全一致 ——
所以自动任务选中的那份，就是你在弹窗里看到的第 1 条。

### 实时补字幕

新片入库并刮削完成后，插件会在 30 秒内自动为它补一次字幕，不用等定时任务：

- 每 30 秒查一次「最近 6 小时入库 + 已刮削完成」的条目
- 只处理这些新片，不扫全库
- 只处理开启了「自动补全」的库
- 定时任务保留作为兜底（漏网条目、或实时流程被全量任务挡住时）

---

## 安装

### 方式 A：通过插件仓库（推荐，可自动更新）

控制台 → 插件 → 仓库 → 添加：

```
https://raw.githubusercontent.com/Louis-15/Jellyfin-Thunder-Subtitle/main/manifest.json
```

然后在「目录」里搜索 **Thunder Subtitle** 安装，装完重启 Jellyfin。
之后插件页出现更新提示时点「更新」即可升级，不需要再手工传文件。

### 方式 B：手工安装（服务器访问不了 GitHub 时）

先备份插件目录，重启 Jellyfin 会造成几十秒不可用。

下载 Release 里的 `thunder-subtitle_<version>.zip`，解压到 Jellyfin 配置目录下的
`plugins/ThunderSubtitle_<version>/`：

```bash
# 以 Docker 部署为例：/path/to/jellyfin/config 换成你映射给容器的配置目录
CONFIG=/path/to/jellyfin/config
VERSION=1.0.0.0
mkdir -p "$CONFIG/plugins/ThunderSubtitle_$VERSION"
unzip -o thunder-subtitle_$VERSION.zip -d "$CONFIG/plugins/ThunderSubtitle_$VERSION"
docker restart jellyfin   # 容器名按实际填写

# 确认加载
docker logs --tail 200 jellyfin 2>&1 | grep -i "Thunder Subtitle"
```

- **Docker**：上面的 `/path/to/jellyfin/config` 是**宿主机**上映射给容器 `/config` 的目录；
- **NAS（群晖 / 威联通 / UGREEN 等）**：不需要 SSH，在 NAS 的文件管理器里把 zip 解压到映射出来的
  `config/plugins/ThunderSubtitle_<version>/`，再在容器管理界面重启 Jellyfin 即可。

插件目录里必须是 `Jellyfin.Plugin.ThunderSubtitle.dll` + `meta.json` + `logo.png`，
不要多套一层文件夹，也不要再放 Jellyfin 自身的 dll（项目已设置 `CopyLocalLockFileAssemblies=false`）。

---

## 配置

控制台 → 插件 → Thunder Subtitle。

### 目标影视库

勾选需要本插件处理的库，每个库后面有三个下拉框（未勾选的库也会显示当前设置）：

| 下拉框 | 取值 | 默认 | 说明 |
|---|---|---|---|
| 检索方式 | 番号搜索 / 片名搜索 | 片名搜索 | 番号库按 `ABF-363` 查；普通影视库按片名 + 年份 / `S01E02` 查 |
| 自动补全 | 是 / 否 | 否 | 定时任务与实时入库是否自动为该库下载字幕 |
| 自动择优 | 是 / 否 | 否 | 「自动择优」任务是否重新比较该库已有的 `.chi.*`；自动补全为「否」时该项隐藏 |

### 下载与筛选

| 配置项 | 默认 | 说明 |
|---|---|---|
| 启用插件 | 开 | 关闭后两个任务都不再下载 |
| 单次上限 | 5 | 每次运行最多处理多少部（只统计真正发起搜索的） |
| 最多候选数 | 25 | 每部影片最多下载多少条候选来比较 |
| 请求间隔 | 500 ms | 两次**搜索**之间的等待 |
| 下载并发数 | 4 | 同时下载多少条候选（1~8） |
| 单部影片下载预算 | 90 秒 | 到点就用手上已下载的候选排序，不再等慢 CDN |
| 优先语言 | 简体中文 | 简/繁都接受，选中的优先 |
| 日志级别 | 常规 | 错误 / 警告 / 常规 / 调试（逐条候选） |

---

## AI 评审

### 送审范围怎么定

只按"台词最多"取前几名是不够的：条数最多的那一批，往往只是同一份机翻的不同转码，
真正的人翻会被挤出视野。所以改为**按条数分档**：

1. 候选按「条数」从多到少分档，相差 **2%** 以内算同一档（500 和 504 同档，500 和 480 不同档）
2. 前 **3** 档，每档挑 **2 份**（档内按质量排序取前面几个）
3. **不足则补**：档位太少时（例如条数很分散，每档只有 1 份），从第 4 档起继续按档位顺序补，
   直到凑满 6 份（= 每档份数 × 3 档），上限 12 份

这样即使条数最多的那批全是同一份机翻的副本，排在后面的人翻仍然能进入评审。

### 送给模型什么

- 只发**台词文本**，不带时间轴（省 token，也避免模型拿时间码当线索）
- 每份候选抽 **24 条**对白、最多 **3000 字**，覆盖整部片子而不是只取开头
- 模型返回 `{"candidates":[{"id":1,"score":85,"machine":false,"reason":"…"}]}`，
  插件只用来重排顺序

成本参考：3 份候选 ≈ 6k token / 部。

### 配置项

| 配置项 | 默认 | 说明 |
|---|---|---|
| 启用 AI 评审 | 关 | 每部影片一次请求，取样的候选放在同一个请求里 |
| 供应商 | **Open Code（内置）** | 见下；也可以切「自定义」 |
| API Base URL | — | 仅「自定义」需要填 |
| 模型名 | `deepseek-v4.1-flash` | Open Code 可用 `glm-5.3-flash`、`kimi-k3` 等 |
| API Key | 空 | 以 `Authorization: Bearer` 发送，不写日志 |
| 每档评审份数 | 2 | 1~3；档数固定 3 |
| 超时 | 60 秒 | 超时即回退到字符数排序 |

### 供应商

**Open Code（内置）**

Base URL 固定为 `https://opencode.ai/inference/go/openai/v1`，不用填。

按 [OpenCode Go 文档](https://opencode.ai/docs/go/) 的要求，每个请求都要带一个稳定的会话 ID：

> Send a stable session ID in `x-opencode-session` for each conversation so we can
> optimize routing and prompt caching.

插件会自动加上这个头，值形如 `<本次运行 GUID>-<条目 ID>`，同一部影片在同一次运行内保持稳定。
客户端也使用插件自己的 User-Agent（`Jellyfin.Plugin.ThunderSubtitle/x.y.z`），文档同样要求不要用通用 SDK 名。

**自定义（任何 OpenAI 兼容端点）**

填自己的 Base URL 即可，不会发送 `x-opencode-session`。本地模型示例：

```
Base URL: http://127.0.0.1:11434/v1
模型名:   qwen2.5:14b
API Key:  （留空）
```

**隐私说明**：开启 AI 评审后，会把字幕抽样文本（部分对白）发送到你配置的服务端。
介意的话用本地 Ollama / LM Studio，数据不出机器。

---

## 处理流程

1. **逐库枚举**：番号库取 `Movie`，片名库取 `Movie + Episode`
   （`CollapseBoxSetItems = false`，避免已进合集的影片被隐藏）。
2. **生成查询词**：按该库配置的方式 —— 番号，或 `条目名 + 年份` / `剧名 + SxxExx`。
3. **搜索**：请求迅雷字幕库。番号模式会用另一种前导零写法补查一次
   （索引把 `NIMA-086` 和 `NIMA-86` 当两次不同搜索）；片名模式在没有任何证据匹配时退一步用纯片名再查。
4. **下载并逐条校验**：
   - 解码：`utf-8-sig → utf-16(BOM) → utf-8 → gb18030 → big5 → shift_jis → windows-1252`，
     按 `汉字×3 - Latin1×2 - 异常字符×8` 打分选最优；
   - 内容校验：必须是真字幕（SRT 时间轴 / ASS `[Script Info]`+`Dialogue:` / `WEBVTT`），
     CDN 返回的错误页、XML 会被丢弃；
   - 格式白名单 `srt / ass / ssa / vtt`，输出扩展名与内容一致（`.ass` 不会存成 `.srt`）。
5. **语言过滤**：只保留简/繁中文。必须排在排序之前 —— 英文每 KB 的字母数远高于中文，
   直接比字符数会天然偏向英文。
6. **去重**：`md5(CRLF→LF 后的文本)`，同一份字幕的换行差异版本只留一份。
7. **排序**：
   - 番号模式先排除番号不符的候选（搜 `NIMA-86` 会返回 `NIMA-014`，它更大但属于别的片）；
   - 片名模式先按**片名 / 季集 / 年份**证据分档，只留最强的一档，错集数的候选直接丢弃；
   - 再按「机翻标记数升序」→「优先语言」→「**台词字符数**降序」。
   - 比的是剥掉样式头、时间轴、`{\pos}` 标签之后的**台词字符数**，不是文件字节
     （同一份字幕存成 `.ass` 会白白大 20%）。
8. **AI 评审（可选）**：见上一节。
9. **写入**：`<片名>.chi.<ext>`，先写临时文件再原子替换，并删掉旧的其它扩展名 `.chi.*`。
   已有 `.chi.*` 时只有真正变好才替换：语言不对无条件换；机翻标记变少直接换；
   优先语言替换另一种中文形式；否则要求台词字符数多 `max(30 字, 5%)`。
10. **刷新条目**：`ValidationOnly` + `ImageRefreshMode.None` —— 重新探测本地文件、挂上外挂字幕，
    但不会触发库设置里的 `SubtitleDownloadLanguages`，因此不会产生 `.chi.0.srt`。

---

## 安全边界

- 只处理 `.chi.*`；网盘同步下来的 `<片名>.srt` / `<片名>.zh.srt` **永远不会被覆盖或删除**。
- 只会删除本插件自己命名的 `<片名>.chi.<ext>`（不含 Jellyfin 的 `<片名>.chi.0.srt` 冲突副本）。
- 下载只允许 `*.xunlei.com` / `*.sandai.net` / `*.geilijiasu.com`，单文件上限 8 MB。
- 同一时间只允许一个任务在跑（信号量），补全与择优不会互相打架。
- 幂等：内容相同直接跳过；**自动补全永远不碰已有 `.chi.*`**，升级只由「自动择优」完成。

---

## 日志

插件行以北京时间打头，便于排查：

```
[2026-11-02 20:32:01 CST] 自动补全开始：影视库 1 个，条目 28 条，待处理 2 条，单次上限 20 条。
[2026-11-02 20:32:02 CST] MIUM-1457.strm [MIUM-1457]：候选 12 条，可下载 12 条，下载耗时 3.1 秒（并发 4）。
[2026-11-02 20:32:05 CST] MIUM-1457.strm [MIUM-1457]：最优「MIUM-1457.海南鸡字幕.37.9KB.srt」（简体，4241 字 / 928 条，.srt，AI标记 0）。
[2026-11-02 20:32:05 CST] MIUM-1457.strm [MIUM-1457]：写入「MIUM-1457.chi.srt」37896 B（utf-8 → utf-8）。
[2026-11-02 20:32:06 CST] MIUM-1457.strm：刷新后识别到 1 条字幕流，「MIUM-1457.chi.srt」已生效。
[2026-11-02 20:32:06 CST] 自动补全结束：处理 2 条，写入 2 条，保留 0 条，无可用字幕 0 条，失败 0 条，耗时 4.3 秒。
```

开启 AI 评审后，每份送审候选还会多一行 `AI #2「xxx.srt」88 分 口语自然`。

```bash
docker logs --tail 500 jellyfin 2>&1 | grep -i "CST\]"
```

日志级别调到「调试」会打印每条候选的编码、字数、条数、机翻标记，适合排查为什么某份没被选中。

---

## 测试与自检

```powershell
# 单元测试
dotnet test tests\Jellyfin.Plugin.ThunderSubtitle.Tests -c Release

# 联网自检：直接打真实 API，打印每份候选的编码 / 格式 / 字数 / 条数 / 机翻标记
dotnet run --project tools\SubtitleProbe -c Release -- ABF-363 MIUM-1457 FSDSS-272
```

`SubtitleProbe` 不依赖 Jellyfin，可以在装插件之前确认上游行为有没有变化。

---

## 从源码构建

需要 .NET 10 SDK。

```powershell
# 编译 + 打包
.\build.ps1

# 找不到 dotnet 时指定路径
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
.\release.ps1 -Version 1.0.1.0 -Changelog "本次改了什么"
```

脚本会同步 csproj / meta.json 的版本号、提交并推 `v1.0.1.0` 标签；
GitHub Actions 随后编译、跑测试、打包、把**本次产物的 MD5** 写回 `manifest.json` 并提交，
最后发布 Release。这样 Jellyfin 下载的 zip 与 manifest 里的 checksum 永远一致。

---

## 目录结构

```
Jellyfin.Plugin.ThunderSubtitle/
├── Plugin.cs                     # 插件入口，名称 / 简介 / 配置页注册
├── PluginConfiguration.cs        # 配置模型（含每库设置）
├── PluginServiceRegistrator.cs   # DI 注册
├── Core/
│   ├── ReleaseCode.cs            # 番号提取（含 FC2 / HEYZO 等特殊形态）
│   ├── SearchTarget.cs           # 检索目标：番号 / 片名 + 年份 / 季集
│   ├── TitleMatch.cs             # 片名模式：候选与目标的证据分档
│   ├── LibraryContext.cs         # 条目/路径 → 所属库 → 检索方式 → 搜索目标
│   ├── ThunderClient.cs          # 迅雷 API + CDN 下载（域名白名单、体积上限）
│   ├── SubtitleText.cs           # 编码探测、格式嗅探、去重哈希
│   ├── SubtitleContent.cs        # 解析 SRT/VTT/ASS，统计台词字符数
│   ├── SubtitleRanker.cs         # 证据分档 + 机翻标记 + 台词字符数排序、升级判断
│   ├── SubtitleFinder.cs         # 共享管线：搜索 → 下载 → 过滤 → 排序 → AI 评审
│   ├── AiSubtitleJudge.cs        # AI 评审：抽样、prompt、JSON 解析
│   ├── Sidecar.cs                # 命名规则、原子写入、旧扩展名清理
│   ├── ProvenanceStore.cs        # 字幕来源台账（供自动择优判断机翻标记）
│   ├── ItemWatchService.cs       # 实时入库监听
│   ├── PreviewTextCache.cs       # 预览全文的内存缓存
│   ├── ClientScriptInjectionFilter.cs  # 把预览按钮注入 Jellyfin Web
│   ├── PluginLog.cs              # 日志级别过滤 + 北京时间戳
│   └── SubtitleJob.cs            # 任务主体（按库分别处理补全 / 择优）
├── Api/PreviewController.cs      # 预览接口（只读内存，不落盘）
├── Providers/ThunderSubtitleProvider.cs  # 注册为 Jellyfin 字幕提供者
├── Tasks/
│   ├── ThunderFillTask.cs
│   └── ThunderRepickTask.cs
└── Web/configPage.html           # 嵌入式配置页
```

---

## 已知限制

- 番号搜索的库只处理 `Movie`，且番号必须能从文件名识别；片名搜索的库同时处理 `Movie` 和 `Episode`。
- 片名搜索没有番号那样的硬标识：中文片名查出来常常是英文命名的候选，此时无法核对片名，
  插件会按质量择优并在日志里告警，建议人工确认一次。
- 排序假设「台词字符数越多越完整」，这条只在同语言、同翻译流派内成立；跨流派依靠 AI 评审。
- AI 评审只看抽样的 24 条对白，读不出时间轴错位的字幕。
- AI 评审结果不缓存：同一个候选在「自动择优」里会被重新评审。
- 番号不符的候选一律丢弃：宁可不写，也不把别的片子的字幕写到这部片上。
- 刷新使用 `ValidationOnly`，不会重新拉取远程元数据；`.strm` 指向的远程视频本身不会被探测。
- 上游没有候选时不会回退到其它字幕站。

---

## 许可

MIT
