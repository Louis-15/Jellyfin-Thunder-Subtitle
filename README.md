# Jellyfin Thunder Subtitle

Jellyfin 12.2 插件：为影视库中**完全没有字幕**的影片，按番号（如 `ABF-363`）从迅雷字幕 API 自动下载高质量外挂字幕，
写成 `<片名>.chi.srt` / `<片名>.chi.ass` 放在视频（`.strm`）旁边，并刷新条目让 Jellyfin 识别。

面向 Jellyfin **12.2.0**（`targetAbi = 12.2.0.0`，`net10.0`）。

> **要接手继续开发？先读 [`AGENTS.md`](./AGENTS.md)**：技术栈、仓库结构、处理管线、Jellyfin 集成点、
> 发版部署流程、已知的坑和当前状态都在那一篇里。本 README 只写"插件能给用户做什么"。

## 功能

| 计划任务 | Key | 默认触发 | 作用 |
|---|---|---|---|
| 迅雷字幕 · 自动补全 | `ThunderSubtitleFill` | 每 6 小时 | 扫描所选影视库里没有任何字幕的影片，下载最合适的字幕 |
| 迅雷字幕 · 重新择优 | `ThunderSubtitleRepick` | 每周日 04:00 | 对已由本插件写入的 `.chi.*` 重新比较，更好的才替换 |

### 实时补字幕（1.9.0.0 起，默认开启）

新片入库并刮削完成后，插件会在 30 秒内自动为它补一次字幕，**不用等定时任务**。

- `ItemWatchService` 每 30 秒查一次「最近 6 小时入库 + ProviderIds 非空（已刮削）」的影片
- 只处理这些新片，不扫全库；已有字幕的会被跳过
- 开关：配置项 `RealtimeFill`
- 上面两个定时任务保留：漏网条目、或实时流程被全量任务挡住时由它们兜底

另外，插件同时注册为 Jellyfin 的**字幕提供者**，所以单部影片也能手动来：

> 影视库 → 右键影片 → **修改字幕** → 「搜索字幕」里选 **Chinese (Simplified)** → 点放大镜

结果会列出所有简体/繁体候选（附 `简体 · 4241 字 / 928 条 · 49.0 KB` 这样的说明），开启 AI 时
**另起一行**显示 `AI 82 分（口语自然）`。每条结果后面有两个按钮：

- **预览**（眼睛图标）：在弹窗里读整份字幕，带时间轴、可切自动换行，**不会写进影片文件夹**；
  弹窗里的「使用这份字幕」等价于点下载。
- **下载**（↓ 图标）：Jellyfin 自己写成 `<片名>.chi.srt`。

这条路径和定时任务用的是**同一套**搜索/下载/过滤/排序逻辑，所以看到的第一条就是自动任务会选的那条。

> **预览按钮是怎么加进去的**：Jellyfin 的「搜索字幕」弹窗编译在 web 客户端里，插件无法从服务端
> 直接给它加按钮。插件因此注册了一个 `IStartupFilter`，在 **响应时** 往 jellyfin-web 的
> `index.html` 里追一个 `<script src="/ThunderSubtitle/Client.js">`（不写磁盘，Docker 只读镜像也能用），
> 由这个脚本在弹窗渲染出来后，把预览按钮插到下载按钮旁边。Jellyfin 大版本升级不会破坏它，
> 脚本找不到弹窗时什么都不做。
>
> 预览的字幕全文只放在**服务器内存**里：30 分钟过期、最多 24 份、重启即清空，插件配置页有
> 「清空预览缓存」按钮，**任何情况下都不会落盘**。

处理流程：

1. 用 `InternalItemsQuery` 枚举目标库的电影（`TopParentIds` + `Recursive`，并显式 `CollapseBoxSetItems = false`，避免已进合集的影片被隐藏）。
2. 从文件名提取番号：`ABF-363-U.strm` → `ABF-363`，`[ThZu.Cc]SSIS-706-C.4K.strm` → `SSIS-706`，`FC2-PPV-1234567` 原样保留。
3. 请求 `https://api-shoulei-ssl.xunlei.com/oracle/subtitle?name=<番号>`。
   迅雷的索引把 `NIMA-086` 和 `NIMA-86` 当成两个不同的搜索（前者返回该片，后者返回一堆无关影片），
   所以先用文件名里的写法查询；若没有任何候选的番号与之匹配，再用另一种写法补查一次并合并结果。
4. **下载全部候选**（CDN 不返回 `Content-Length`，只能下完才知道体积），逐个：
   - 解码：`utf-8-sig → utf-16(BOM) → utf-8 → gb18030 → big5 → shift_jis → windows-1252`，按 `汉字×3 - Latin1×2 - 异常字符×8` 打分选最优；
   - 内容校验：必须是真正的字幕（SRT 时间轴 / ASS `[Script Info]`+`Dialogue:` / `WEBVTT`），CDN 返回的 `NoSuchKey` XML、HTML 错误页会被丢弃；
   - 格式白名单：只接受 `srt / ass / ssa / vtt`，`smi / sub / sup / idx` 直接丢弃，输出扩展名与内容一致（`.ass` 不会存成 `.srt`）。
5. **语言过滤**：只保留简体中文与繁体中文候选，其它语言（英文、日文、韩文…）直接丢弃。
   这一步必须排在排序之前：英文字幕每 KB 的"字母数"远高于中文，字符数排序会天然偏向英文。
   简繁判定靠常用字的繁简对（这/這、说/說、们/們…）计数，日文靠假名占比识别。
6. 去重：`md5(文本归一化 CRLF→LF)`，同一字幕的换行差异版本只留一份。
7. 排序：**先排除番号不符的候选**（搜索 `NIMA-86` 时会出现 `NIMA-014` 之类的别的片子，
   它体积更大，纯按体积排序会把别人的字幕写进来），再按「名字里带本番号 > 无番号的哈希名」、
   **机翻标记数升序**、**优先语言优先**、**台词字符数降序**排序。
   - 接口的 `score` / `fingerprintf_score` 恒为 0，不可用。
   - 比的是 `SubtitleContent.Measure` 算出的**台词字符数**（剥掉 ASS 样式头、`Dialogue:` 字段、
     `{\pos}` 标签、`\N`、SRT 序号与时间轴后，只数汉字/假名/字母/数字），不是文件字节数。
     同一份字幕存成 `.ass` 会白白大 20%，按字节排会让格式而不是内容决定胜负。
   - 机翻标记排在语言之前：一份人工繁体字幕胜过一份机翻简体字幕。
8. **（可选）AI 评审**：把字符数前 `AiTopCandidates`（默认 3）名送进同一个 chat 请求，
   让模型横向比较哪一份像人工翻译。机翻常常**更长**——每个语气词都被逐字翻出来——
   所以体积没法区分，但"驴唇不对马嘴"能读出来。AI 只负责重排顺序：
   连接失败、超时、返回不是合法 JSON 时全部回退到字符数排序，绝不阻断写字幕。
   - 送审范围会按**条数并列**扩大：如果第 N 名和后面几名的 `条数`（cue 数）都等于最大值，
     说明它们是同一份字幕的不同翻译，字符数已经没有区分度，这时把这些并列的全部一起送审
     （上限 8 份）。否则人工翻译排在 3 份机翻后面就永远进不了模型视野。
   - 送给模型的只有台词本身，**不带时间轴**：省 token，也避免模型拿时间码当线索。
   - 评审结果按**候选实例**对应回原候选，所以 AI 重排后每行的分数仍然属于它自己。
9. 写入 `<片名>.chi.<ext>`（先写临时文件再原子替换），并删掉旧的其它扩展名 `.chi.*`，避免同一影片出现两份字幕。
   已有 `.chi.*` 时只有真正变好才替换：语言不对（英文/日文）无条件换；机翻标记变少直接换；
   优先语言替换另一种中文形式；否则要求台词字符数比现有多 `max(30 字, 5%)`，
   避免「多了一个注释、多条换行」的重复上传来回覆盖。
10. 刷新条目：`MetadataRefreshMode.ValidationOnly` + `ImageRefreshMode.None`。
   重新探测本地文件、挂上外挂字幕，但**不会**触发库设置里的 `SubtitleDownloadLanguages`，因此不会产生 `.chi.0.srt`。
11. 在插件数据目录记录来源（`<data>/thunder-subtitle/provenance.json`），供「重新择优」判断现有字幕是机翻还是人工。

## 安全边界

- 只处理 `.chi.*`；115 网盘同步下来的 `<片名>.srt` / `<片名>.zh.srt` **永远不会被覆盖或删除**。
- 只会删除本插件自己命名的 `<片名>.chi.<ext>`（不含 `Jellyfin` 的 `<片名>.chi.0.srt` 冲突副本）。
- 下载只允许 `*.xunlei.com` / `*.sandai.net` / `*.geilijiasu.com`，单文件上限 8 MB。
- 同一时间只允许一个任务在跑（信号量），补全与择优不会互相打架。
- 幂等：内容相同直接跳过；`AllowOverwritePluginSubtitle` 关闭时完全不碰已有 `.chi.*`。

## 构建

需要 .NET 10 SDK。

```powershell
# 编译 + 打包（生成 artifacts/thunder-subtitle_1.0.0.0.zip）
.\build.ps1

# 编译时找不到 dotnet 时指定路径
.\build.ps1 -Dotnet "D:\dotnet10\dotnet.exe"
```

产物：

```
artifacts/Jellyfin.Plugin.ThunderSubtitle_1.0.0.0/   # dll + meta.json + logo.png
artifacts/thunder-subtitle_1.0.0.0.zip               # 目录内容打平后的 zip
```

单元测试与联网自检：

```powershell
dotnet test tests\Jellyfin.Plugin.ThunderSubtitle.Tests -c Release
dotnet run --project tools\SubtitleProbe -c Release -- ABF-363 MIUM-1457 FSDSS-272
```

`SubtitleProbe` 不依赖 Jellyfin：直接打真实 API、打印每条候选的体积/编码/格式，并把最优结果写到临时目录，
可以在装插件之前确认迅雷侧的行为有没有变化。

## 安装

### 方式 A：通过插件仓库（可在 Jellyfin 内检查和更新）

控制台 → 插件 → 仓库 → 添加：

```
https://raw.githubusercontent.com/Louis-15/Jellyfin-Thunder-Subtitle/main/manifest.json
```

然后在「目录」里搜索 **Thunder Subtitle** 安装。之后插件页面会出现更新提示，
「更新」即可升级到新版本，不需要再手工传文件。

### 方式 B：手工安装

```bash
# 宿主机
VERSION=1.0.0.0
scp -P 2222 artifacts/thunder-subtitle_$VERSION.zip root@931002.xyz:/tmp/
ssh -p 2222 root@931002.xyz "
  mkdir -p /www/jellyfin/config/plugins/ThunderSubtitle_$VERSION &&
  unzip -o /tmp/thunder-subtitle_$VERSION.zip -d /www/jellyfin/config/plugins/ThunderSubtitle_$VERSION &&
  docker restart jellyfin"

# 确认加载
docker logs --tail 200 jellyfin 2>&1 | grep -i "Thunder Subtitle"
```

插件目录里必须是 `Jellyfin.Plugin.ThunderSubtitle.dll` + `meta.json` + `logo.png`，不要再放 Jellyfin 自身的 dll
（项目已设置 `CopyLocalLockFileAssemblies=false`）。

## 发布新版本

```powershell
.\release.ps1 -Version 1.0.1.0 -Changelog "修复 xxx"
```

脚本会同步 csproj / meta.json / manifest.json 的版本号、提交并推 `v1.0.1.0` 标签；
GitHub Actions 随后编译、跑测试、打包、把**本次产物的 MD5** 写回 `manifest.json` 并提交，
最后发布 Release。这样 Jellyfin 下载的 zip 与 manifest 里的 checksum 永远一致。

## 配置

控制台 → 插件 → Thunder Subtitle：

| 配置项 | 默认 | 说明 |
|---|---|---|
| 启用插件 | 开 | 关闭后两个任务都不再下载 |
| 目标影视库 | 空 | 必填，只勾需要自动补字幕的那个库 |
| 单次上限 | 5 | 每次运行最多处理多少部（只统计真正发起搜索的） |
| 最多候选数 | 25 | 每部影片最多下载多少条候选来比较台词字符数 |
| 请求间隔 | 500 ms | 两次**搜索**之间的等待 |
| 下载并发数 | 4 | 同时下载多少条候选（1~8） |
| 单部影片下载预算 | 90 秒 | 到点就用手上已下载的候选排序，不再等慢 CDN |
| 允许覆盖已有插件字幕 | 关 | 打开后补全任务也会升级已存在的 `.chi.*` |
| 优先语言 | 简体中文 | 简/繁都接受，选中的优先 |
| 日志级别 | 常规 | 错误 / 警告 / 常规 / 调试（逐条候选） |
| AI 评审 | 关 | 见下一节 |

### AI 评审

按**条数分档**取样，再让大模型横向比较一次。只做**相对排序**，不做绝对阈值。

取样规则（1.9.1.0 起）：

1. 候选按「条数」从多到少分档，相差 **2%** 以内算同一档（500 和 504 同档，500 和 480 不同档）
2. 前 **3** 档，每档挑 **2 份**（档内按质量排序取前面几个）
3. **不足则补**：如果档位太少（例如条数很分散，每档只有 1 份），从第 4 档起继续按档位顺序补，
   直到凑满 6 份（= 每档份数 × 3 档），上限 12 份

这样即使条数最多的那批全是同一份机翻的不同转码，排在后面的人翻仍然能进入评审 ——
只按条数取前 N 名做不到这一点。

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

- Base URL 固定为 `https://opencode.ai/inference/go/openai/v1`，不用填。
- 按 [OpenCode Go 文档](https://opencode.ai/docs/go/)的要求，每个请求都要带一个稳定的会话 ID：

  > Send a stable session ID in `x-opencode-session` for each conversation so we can
  > optimize routing and prompt caching.

  插件会自动加上这个头，值形如 `<本次运行 GUID>-<条目 ID>`，同一部影片在同一次运行内保持稳定。
- 客户端用插件自己的 User-Agent（`Jellyfin.Plugin.ThunderSubtitle/x.y.z`），文档同样要求不要用通用 SDK 名。

**自定义（OpenAI 兼容）**

填你自己的 Base URL 即可，不会发送 `x-opencode-session`。本地模型示例：

```
Base URL: http://127.0.0.1:11434/v1
模型名:   qwen2.5:14b
API Key:  （留空）
```

成本控制：每个候选只抽 24 条对白、最多 3000 字，3 个候选 ≈ 6k token/部。

本地模型示例（数据不出机器）：

```
Base URL: http://127.0.0.1:11434/v1
模型名:   qwen2.5:14b
API Key:  （留空）
```

**隐私说明**：开启后会把字幕抽样文本（部分对白）发送到你配置的服务端。
介意的话用本地 Ollama / LM Studio。

配置页还有「立即补全」「立即重新择优」「打开计划任务」，直接调用 Jellyfin 自带的 `/ScheduledTasks` API。

**首次上线后建议停掉旧的 python 定时器**（2026-11-04 已在服务器上停用并删除，
备份在 `/root/backup/jellyfin-legacy-20261104/`），避免两套逻辑重复下载：

```bash
systemctl disable --now jellyfin-fill-subs.timer
```

同时保持库设置 `SubtitleDownloadLanguages = []`（不要把 `chi` 填回去）。

## 日志

插件行以北京时间打头，便于和旧脚本的日志对照：

```
[2026-11-02 20:32:01 CST] 自动补全开始：影视库 1 个，条目 28 条，待处理 2 条，单次上限 20 条。
[2026-11-02 20:32:02 CST] MIUM-1457.strm [MIUM-1457]：候选 12 条，可下载 12 条（已丢弃不支持或超出上限的 0 条）。
[2026-11-02 20:32:05 CST] MIUM-1457.strm [MIUM-1457]：有效 6 条，最优「MIUM-1457.海南鸡字幕.37.9KB.20261028.srt」（37895 B，.srt，utf-8，AI标记 0）。
[2026-11-02 20:32:05 CST] MIUM-1457.strm [MIUM-1457]：写入「MIUM-1457.chi.srt」37896 B（utf-8 → utf-8）。
[2026-11-02 20:32:06 CST] MIUM-1457.strm：刷新后识别到 1 条字幕流，「MIUM-1457.chi.srt」已生效。
[2026-11-02 20:32:06 CST] 自动补全结束：处理 2 条，写入 2 条，保留 0 条，无可用字幕 0 条，失败 0 条，耗时 4.3 秒。
```

排查：

```bash
docker logs --tail 500 jellyfin 2>&1 | grep -i "CST\]"
```

## 目录结构

```
Jellyfin.Plugin.ThunderSubtitle/
├── Plugin.cs                     # BasePlugin<IHasWebPages>
├── PluginConfiguration.cs        # 配置模型
├── PluginServiceRegistrator.cs   # DI 注册（任务必须注册，Jellyfin 通过容器实例化）
├── Core/
│   ├── ReleaseCode.cs                # 番号提取
│   ├── ThunderClient.cs          # 迅雷 API + CDN 下载（域名白名单、体积上限）
│   ├── SubtitleText.cs           # 编码探测、格式嗅探、去重哈希
│   ├── SubtitleContent.cs        # 解析 SRT/VTT/ASS，统计台词字符数
│   ├── SubtitleRanker.cs         # 番号校验 + 机翻标记 + 台词字符数排序、升级判断
│   ├── AiSubtitleJudge.cs        # OpenAI 兼容评审（抽样、prompt、JSON 解析）
│   ├── Sidecar.cs                # 命名规则、原子写入、旧扩展名清理
│   ├── ProvenanceStore.cs        # 字幕来源台账
│   ├── PluginLog.cs              # 日志级别过滤 + 北京时间戳
│   └── SubtitleJob.cs            # 任务主体
├── Tasks/
│   ├── ThunderFillTask.cs
│   └── ThunderRepickTask.cs
└── Web/configPage.html           # 嵌入式配置页
```

## 超时与并发

早先版本在 `HttpClient` 上设了全局 60 秒超时，结果是**每个请求**最长 60 秒：一次处理 25 个候选，
最坏 25 分钟，一部片子就能把整个任务卡住（实测卡过一次 10 分钟没动静）。

现在：

| 环节 | 超时 | 说明 |
|---|---|---|
| 搜索 | 20 秒 | 一次小 JSON，正常 <1 秒 |
| 单条下载 | 15 秒 | 一份字幕 10~100 KB，正常 <1 秒 |
| 单部影片总预算 | 90 秒（可配） | 到点就用手上已下载的候选排序 |

下载改为**并发 4 路**（可配 1~8），不再逐条串行。超时按请求用 `CancellationTokenSource` 实现，
不用 `HttpClient.Timeout` —— 后者必须跟最慢的场景对齐，正是它造成了上面那次卡死。

## 已知限制

- 只处理 `Movie` 类型的条目，番号必须能从文件名识别。
- 迅雷侧没有候选时不会回退到 SubtitleCat；旧脚本可以继续作为 fallback。
- 排序假设「台词字符数越多，字幕越完整」。这条只在**同语言、同翻译流派**内成立，
  跨流派仍需 AI 评审；机翻一般在文件名里带 `[MT]` / `AI翻译` / `whisper` 等标记，会被优先排除。
- AI 评审只看抽样的 24 条对白，读不出时间轴错位的字幕（时间轴问题靠 `duration` 才能查，暂未做）。
- AI 评审结果不缓存：同一个候选在"重新择优"里会被重新评审。
- 番号不符的候选一律丢弃：如果某部影片的候选全部属于别的番号，会记录「全部属于其它番号」并跳过，
  宁可不写，也不把别的片子的字幕写到这部片上。
- 刷新使用 `ValidationOnly`，不会重新拉取远程元数据；`.strm` 指向的远程视频本身不会被探测。
