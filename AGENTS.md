# Thunder Subtitle · 项目交接文档

> 这份文档写给**接手继续开发的人或 AI**。读完这一篇就能直接改代码，不需要翻聊天记录。
> 最后更新：2026-11-04，当前版本 **1.9.0.0**。

---

## 1. 这是什么

一个 **Jellyfin 12.2 插件**：为按番号命名的影片（如 `ABF-363`）从**迅雷字幕 API** 自动搜索并下载外挂中文字幕，
写成 `<片名>.chi.srt` / `<片名>.chi.ass` 放在视频（`.strm`）旁边。

除自动下载外，插件还把自己注册成 Jellyfin 的**字幕提供者**，所以单部影片可以在
「右键影片 → 修改字幕 → 搜索字幕」里手动搜索、**预览全文**、再下载。

- 仓库：<https://github.com/Louis-15/Jellyfin-Thunder-Subtitle>
- 插件仓库地址（Jellyfin 内添加，支持在线更新）：
  `https://raw.githubusercontent.com/Louis-15/Jellyfin-Thunder-Subtitle/main/manifest.json`

---

## 2. 30 秒上手

```powershell
# 编译
dotnet build .\Jellyfin.Plugin.ThunderSubtitle\Jellyfin.Plugin.ThunderSubtitle.csproj -c Release

# 跑测试（172 个）
dotnet test .\tests\Jellyfin.Plugin.ThunderSubtitle.Tests\Jellyfin.Plugin.ThunderSubtitle.Tests.csproj -c Release

# 发版（自动改版本号 + commit + 打 tag + push，CI 会编译/打包/写 manifest/发 Release）
.\release.ps1 -Version 1.8.3.3 -Changelog "改了什么"
```

> 本机 SDK 装在 `C:\Users\aaa\dotnet-sdk`（10.0.403），**不在 PATH 里**。如果 `dotnet` 报 “兼容的 .NET SDK 未找到”，
> 用全路径 `C:\Users\aaa\dotnet-sdk\dotnet.exe`。`global.json` 固定 `10.0.400` + `rollForward: latestFeature`。

---

## 3. 技术栈

| 层 | 选型 |
|---|---|
| 运行时 | .NET **10**（`net10.0`），`Nullable` 开启 |
| 平台 | Jellyfin **12.2**（`meta.json` 里 `targetAbi = 12.2.0.0`） |
| 依赖 | `Jellyfin.Controller 12.2.0`（`ExcludeAssets=runtime`）+ `FrameworkReference Microsoft.AspNetCore.App` |
| 服务端 API | ASP.NET Core MVC 控制器（Jellyfin 会把插件程序集加入 MVC ApplicationParts） |
| HTTP 管线 | `IStartupFilter` 注册中间件，在**响应时**改写 jellyfin-web 的 `index.html` |
| 前端 | 原生 JS（无框架、无构建），`<script>` 注入 + MutationObserver 操作 DOM |
| 插件页 | 单文件 HTML + ES5 风格 JS，走 Jellyfin 的 `IHasWebPages` |
| 测试 | xUnit（172 个用例），核心逻辑全部可离线测 |
| CI/CD | GitHub Actions（`ubuntu-latest`，`setup-dotnet 10.0.x`），tag 触发 |
| 部署目标 | Docker 里的 Jellyfin + Caddy 反代（`https://931002.xyz:8443`） |

**技术路线的三个关键决定**

1. **所有筛选逻辑都在服务端**，前端只做展示。定时任务和手动搜索走**同一个** `SubtitleFinder`，
   所以「手动看到的第一条」永远等于「自动任务会选的那条」。
2. **AI 只负责重排顺序，不做兜底依赖**。任何超时 / 非 JSON / 字段不全都回退到字符数排序，绝不阻断写字幕。
3. **不修改磁盘上的 jellyfin-web 文件**。预览按钮靠请求时注入 `<script>` 实现，Docker 只读镜像也能用、
   Jellyfin 升级不会被覆盖、中间件出错时原样返回不影响页面。

---

## 4. 仓库结构

```
manifest.json                  Jellyfin 插件仓库清单（CI 自动写 checksum，勿手改）
meta.json                      插件元数据（guid / version / targetAbi）
release.ps1                    一条命令发版
build.ps1                      编译 + 打 zip（CI 与手工部署共用）
scripts/update-manifest.ps1    CI 用来把 checksum 写回 manifest
global.json                    SDK 版本锚点
.github/workflows/build.yml    tag 触发：test → package → 写 manifest → 发 Release
```

### 主工程 `Jellyfin.Plugin.ThunderSubtitle/`

| 文件 | 职责 |
|---|---|
| `Plugin.cs` | `BasePlugin` + `IHasWebPages`；静态 `Plugin.Current` 取配置；北京时区日志格式 |
| `PluginConfiguration.cs` | 全部配置项 + `Normalize()` 兜底 + `ResolvePreferredLanguage()` / `ApiRoot` |
| `PluginServiceRegistrator.cs` | **所有 DI 注册都在这里**（漏注册 = 功能静默失效） |
| `Tasks/ThunderFillTask.cs` | 计划任务「自动补全」，Key `ThunderSubtitleFill`，每 6 小时 |
| `Tasks/ThunderRepickTask.cs` | 计划任务「重新择优」，Key `ThunderSubtitleRepick`，每周日 04:00 |
| `Providers/ThunderSubtitleProvider.cs` | Jellyfin `ISubtitleProvider` 实现 + **`SubtitleId` 编解码** |
| `Api/PreviewController.cs` | 预览用 API：`Client.js` / `Text` / `Cache/Clear` |
| `Web/configPage.html` | 插件配置页（内嵌资源） |
| `Web/clientScript.js` | 注入到 jellyfin-web 的脚本：加预览按钮 + 全文弹窗 |

### 核心 `Core/`

| 文件 | 职责 | 要点 |
|---|---|---|
| `ThunderClient.cs` | 迅雷 API 客户端 | 搜索端点、重试 5 次、域名白名单、按请求超时 |
| `SubtitleFinder.cs` | **共享管线**：搜索→并发下载→解码→语言过滤→排序→可选 AI | `FindOutcome` 供任务与 provider 复用；`SelectShortlist` 决定送审范围 |
| `SubtitleJob.cs` | 计划任务的实际工作 | 枚举库、判断是否处理、写盘、刷新条目、记 provenance |
| `SubtitleRanker.cs` | `SubtitlePick` 模型 + 排序 + 升级判定 | `KeepChineseOnly`、`IsUpgrade`、`ApplyAiVerdicts` |
| `SubtitleContent.cs` | SRT/VTT/ASS 解析与**台词字符数**统计 | 排序指标来源；`StripMarkup`、`Measure`、`Parse` |
| `SubtitleText.cs` | 编码探测（GB18030/Big5/Shift-JIS…）、格式嗅探、CRLF 归一化 | 打分式选编码 |
| `SubtitleLanguage.cs` | 简/繁/日/韩/英判定 | 繁简对 + 假名占比 |
| `Sidecar.cs` | 字幕文件路径与原子写 | `FindPluginSubtitles`、`TargetPath`、`WriteAtomicAsync` |
| `SubtitleFiles.cs` | 查/删 `<片名>.chi\|zh*.<ext>` | 避免 Jellyfin 生成 `.chi.0.srt` |
| `ReleaseCode.cs` | 从文件名提取番号 | `CompareKey`（去前导零）、`AlternativeSpelling` |
| `AiSubtitleJudge.cs` | OpenAI 兼容的 AI 评审 | prompt 构造、JSON 解析、失败返回 `null` |
| `ProvenanceStore.cs` | 记录"这份字幕是我写的、机翻标记几个" | `<data>/thunder-subtitle/provenance.json` |
| `PreviewTextCache.cs` | 预览全文的内存缓存 | 30 分钟过期、最多 24 份、**从不落盘** |
| `ClientScriptInjectionFilter.cs` | 注入中间件 | 见第 6.4 节，**这里最容易出错** |
| `PluginLog.cs` | 按配置的日志级别输出 + 北京时间前缀 | |

### 其它

- `tests/…` — xUnit，13 个测试文件，172 个用例。
- `tools/SubtitleProbe/` — 不依赖 Jellyfin 的离线探针：直接打真实迅雷 API，打印每条候选的体积/编码/格式/字符数。
  改动搜索或排序后先用它验证真实数据，比装插件迭代快得多。
  ⚠️ 用 `dotnet run --project tools/SubtitleProbe`，**不要加 `--no-build`**（会跑旧二进制导致误判）。

---

## 5. 处理管线（`SubtitleFinder.FindAsync`）

1. **搜索** `https://api-shoulei-ssl.xunlei.com/oracle/subtitle?name=<番号>`
   - 上游会**随机返回空数据**（HTTP 200 + `code:0` + `data:[]`，实测约 40%）。因此**最多重试 5 次**，
     间隔 4×1 秒；带浏览器 UA / Referer 命中率更高。
   - 索引把 `NIMA-086` 和 `NIMA-86` 当两次不同搜索：先用文件名里的写法，若没有任何候选匹配番号，
     再用另一种拼写补查并合并。
2. **下载全部候选**（并发默认 4，单请求 15 秒超时，整部影片预算 90 秒），逐个：
   - 解码：`utf-8-sig → utf-16(BOM) → utf-8 → gb18030 → big5 → shift_jis → windows-1252`，
     按 `汉字×3 − Latin1×2 − 异常字符×8` 打分选最优；
   - 内容校验：必须是真字幕（SRT 时间轴 / ASS `[Script Info]`+`Dialogue:` / `WEBVTT`），
     CDN 的 `NoSuchKey` XML、HTML 错误页丢弃；
   - 格式白名单 `srt/ass/ssa/vtt`；输出扩展名与内容一致。
3. **语言过滤**：只留简/繁中文。必须排在排序之前——英文字幕每 KB 的字母数远高于中文，会天然占优。
4. **去重**：`md5(CRLF→LF 后的文本)`。
5. **排序**（`SubtitleRanker.Rank`）：先排除番号不符的（搜 `NIMA-86` 会返回 `NIMA-014`，它更大但属于别的片），
   再按「名字带本番号 > 无番号的哈希名」→「机翻标记数升序」→「优先语言」→「**台词字符数**降序」。
   - 比的是 `SubtitleContent.Measure` 算出的台词字符数，**不是文件字节**（同一份字幕存 ASS 会白大 20%）。
   - 接口的 `score` / `fingerprintf_score` 恒为 0，不可用。
6. **AI 评审（可选）**：送审范围 = 字符数前 `AiTopCandidates`（默认 3）名 **加上所有条数并列最大者**，上限 8 份。
   - 只发**台词文本**，不带时间轴（省 token，也更准）。
   - 模型返回 `{"candidates":[{"id":1,"score":85,"machine":false,"reason":"…"}]}`，只用来重排。
   - 失败/超时/字段不全都回退到字符数排序。
7. **写入**：`<片名>.chi.<ext>`，先写临时文件再原子替换，并删掉其它扩展名的旧 `.chi.*`。
   - **已有字幕只在不差于候选时才保留**：语言不对无条件换；机翻标记变少直接换；优先语言替换另一种中文；
     否则要求字符数多 `max(30 字, 5%)`。
8. **刷新条目**：`ValidationOnly` + `ImageRefreshMode.None`——重新探测本地文件、挂上外挂字幕，
   **不会**触发库设置里的 `SubtitleDownloadLanguages`，因此不会产生 `.chi.0.srt`。
9. **记账**：写 `provenance.json`，供「重新择优」判断现有字幕是机翻还是人工。

---

## 6. 与 Jellyfin 的四个集成点

### 6.1 计划任务
`IScheduledTask` 实现注册进 DI 就会被 Jellyfin 发现。`SubtitleJob` 里有静态 `SemaphoreSlim(1,1)`，
**同一时间只允许一个任务跑**，补全与择优不会互相打架。

### 6.2 字幕提供者（手动搜索）
`ThunderSubtitleProvider : ISubtitleProvider`（注意：12.2 是 `ISubtitleProvider`，不是 `IRemoteSubtitleProvider`）。

- **语言判断**：Jellyfin 传进来的 `TwoLetterISOLanguageName` 实际是 `zh-cn`，也可能是不确定值 `iv`/`und`。
  必须做**前缀匹配**并把不确定值当作"让调用方决定"，否则列表会静默为空。
- **id 编解码**（`SubtitleId`）：
  - Jellyfin 会把 provider 名做 md5 后加下划线作为 id 前缀，调用 `GetSubtitles` 前再 `Split('_', 2)` 去掉；
    `NormalizeIncoming()` 两种形态都能吃（幂等）。
  - 本轮新增前缀标记：真实候选 `ts1-`，提示行 `nt1-`。**注入脚本靠 `ts1-` 认自己那一行**，
    所以两者必须不同，否则"上游为空"的提示行也会长出预览按钮。
- **同名冲突**：Jellyfin 保存时若同名会另存 `<片名>.chi.0.srt`，因此下载前先 `SubtitleFiles.FindChinese` 清旧文件。

### 6.3 插件配置页
`IHasWebPages` 返回**一个**页面（`thundersubtitle`）。曾经的坑：第二个页面如果带 `EnableInMainMenu = true`，
Jellyfin 会把它当成"插件设置页"，导致插件「设置」按钮跳到错误的页面。**只保留一个页面，别加标记。**
页面内的 AI 供应商下拉只做两件事：`OpenCode`（内置）与 `Custom`。

1.8.3.1 起页面按 **`fieldset.ts-group` + `legend.ts-group-title`** 分组，组内内容统一包在
`.ts-group-body` 里（**不做左缩进**，全部贴齐组边框——用户偏好紧凑排版，层级靠 legend 加粗 +
外框区分）。新增设置项时**必须放进对应的 fieldset**，不要再写回
平铺的 `.ts-section`——那套样式已经删掉了。影视库每行沿用官方 `.checkboxContainer` +
`.emby-checkbox-label` 结构，**不要给 label 加内联 padding**：会覆盖官方自带的
`padding-left: 2.4em`，让绝对定位的方框压住库名。

**`<style>` 必须写在 `<body>` 里（1.8.3.2 的教训）**：Jellyfin 前端加载插件页时只把 `<body>`
的子节点搬进 SPA 容器，`<head>` 里的 `<style>` 会被整块丢弃——现象就是"页面结构对、但所有自定义
样式都不生效"（分组没边框、内容没缩进、legend 不变粗）。把 `<style>` 挪回 `<head>` 会立刻复现。

### 6.4 预览按钮（本轮新增，最容易踩坑）

Jellyfin 的「修改字幕 → 搜索字幕」弹窗编译在 jellyfin-web 里，插件无法从服务端给它加按钮。
做法与社区 `Jellyfin-JavaScript-Injector` 一致：

```
ClientScriptInjectionFilter : IStartupFilter        ← 在 DI 里 AddSingleton<IStartupFilter, …>()
   └─ 请求 /web 、/web/ 、/web/index.html 时：
        1. 去掉 Accept-Encoding / Range / If-Range
        2. 去掉 If-None-Match / If-Modified-Since   ← 关键！否则一直 304，永远注入不上
        3. 缓冲响应，在 </body> 前插入
           <!--thunder-subtitle-client--><script src="{basePath}/ThunderSubtitle/Client.js" defer></script>
        4. 重新计算 ContentLength，删掉 ETag / Last-Modified / Accept-Ranges
        5. 任何异常 → 原样返回（绝不破坏页面）
```

`Web/clientScript.js` 被浏览器加载后：
- 用 `MutationObserver` 监听 `.subtitleResults`，给 `data-subid` 带 `ts1-` 的行，在 `.btnDownload` **前面**插入预览按钮
  （用 `insertAdjacentHTML` 写 `is="paper-icon-button-light"`，才能拿到官方按钮样式）；
- 另外每秒补扫一次（只在 `.subtitleEditorDialog` 存在时），防止行被重绘而没有 mutation 事件；
- 点按钮 → 覆盖式弹窗（文件名 / `简体 · 959 条 · 5792 字 · 76.2 KB · SRT · utf-8` / 全文 / 自动换行 / 使用这份字幕 / ✕）；
- API 地址优先 `window.ApiClient.getUrl`，兜底用脚本自身 `src` 推导（`document.currentScript` 在文件执行时就捕获）；
- 令牌优先 `ApiClient.accessToken()`，兜底读 `localStorage['jellyfin_credentials'].Data.Servers[].AccessToken`。

对应接口（`Api/PreviewController.cs`）：

| 方法 | 路径 | 鉴权 | 作用 |
|---|---|---|---|
| GET | `/ThunderSubtitle/Client.js` | `AllowAnonymous`（静态脚本，无密钥） | 下发注入脚本 |
| GET | `/ThunderSubtitle/Text?id=<字幕id>` | `[Authorize]` | 返回正文 + 统计信息（**不落盘**） |
| POST | `/ThunderSubtitle/Cache/Clear` | `RequiresElevation` | 清空预览缓存（配置页按钮） |

预览缓存：只放内存，30 分钟过期，最多 24 份，重启即清空。**任何时候都不会写进影片文件夹。**

---

## 7. 配置项（`PluginConfiguration`）

| 配置 | 默认 | 说明 |
|---|---|---|
| `Enabled` | true | 总开关 |
| `LibraryIds` | `[]` | 目标影视库（**必须选**，否则任务直接返回） |
| `MaxItemsPerRun` | 5 | 单次任务处理上限（引入 AI 后从 20 降到 5） |
| `MaxCandidates` | 25 | 单部影片最多下载多少候选（上限 100） |
| `RequestIntervalMs` | 500 | 搜索间隔 |
| `DownloadConcurrency` | 4 | 并发下载（1–8） |
| `ItemBudgetSeconds` | 90 | 单部影片时间预算（20–600） |
| `AllowOverwritePluginSubtitle` | false | 是否允许重新择优覆盖已有 `.chi.*` |
| `LogLevel` | Information | Debug 会打印每条候选 |
| `PreferredLanguage` | Simplified | 优先简体 / 繁体 |
| `AiEnabled` | false | AI 评审开关 |
| `AiProvider` | OpenCode | `OpenCode` 内置 / `Custom` |
| `AiBaseUrl` | 空 | 仅 Custom 用，自动补 `/chat/completions` |
| `AiApiKey` / `AiModel` | 空 / `deepseek-v4.1-flash` | 密钥只存服务器配置，**不进日志** |
| `AiTopCandidates` | 3 | 送审前 N 名（2–5，条数并列会扩大，总上限 8） |
| `AiSampleCues` | 24 | 每份抽多少句（4–80） |
| `AiMaxCharsPerCandidate` | 3000 | 每份字符上限（500–20000） |
| `AiTimeoutSeconds` | 60 | AI 单请求超时（10–300） |

**Open Code 内置供应商**（用户明确要求单独内置）：Base URL `https://opencode.ai/inference/go/openai/v1`，
自 2025-09-06 起该 API **强制要求** `x-opencode-session` 请求头，`AiSettings.UseSessionHeader` 为真时自动附带。
选 `Custom` 时不发这个头。

---

## 8. 发版与部署（固定闭环）

```
改代码 → dotnet test → .\release.ps1 -Version x.y.z.w -Changelog "..."
        → CI: test → build.ps1 打包 → MD5 写进 manifest.json → 提交 manifest → 发 Release
        → 在服务器上装新版本 → 重启 → 用真实接口验证
```

**装到服务器**（`{KEY}` 为 Jellyfin API Key，**不写进仓库**，用控制台 → API 密钥新建，或向用户索取）：

```powershell
curl.exe -sk -X POST -H "Authorization: MediaBrowser Token=`"{KEY}`"" `
  "https://931002.xyz:8443/Packages/Installed/Thunder%20Subtitle?version=1.8.3.3&repositoryUrl=https%3A%2F%2Fraw.githubusercontent.com%2FLouis-15%2FJellyfin-Thunder-Subtitle%2Fmain%2Fmanifest.json"
ssh -p 2222 root@931002.xyz 'docker restart jellyfin'
```

**验证**：

```powershell
# 版本是否 Active
curl.exe -sk -H "Authorization: MediaBrowser Token=`"{KEY}`"" "https://931002.xyz:8443/Plugins"

# 手动搜索是否返回候选（顺便看 Comment 格式）
curl.exe -sk -H "Authorization: MediaBrowser Token=`"{KEY}`"" `
  "https://931002.xyz:8443/Items/{ITEM_ID}/RemoteSearch/Subtitles/zh-CN"

# 注入是否生效（必须 200 且含 thunder-subtitle-client）
curl.exe -sk -H "Authorization: MediaBrowser Token=`"{KEY}`"" -H 'If-None-Match: "1dd551cc7c4f8c1"' `
  "https://931002.xyz:8443/web/index.html" | Select-String "thunder-subtitle-client"

# 日志（注意：ssh 传参别用嵌套引号，会被远端 shell 吃掉）
ssh -p 2222 root@931002.xyz "docker logs --tail 400 jellyfin 2>&1 | grep -a 预览"
```

服务器环境要点：

- Jellyfin 12.2，docker 容器名 `jellyfin`，插件目录 `/config/plugins/Thunder Subtitle_<version>/`。
- 媒体在宿主机 `/opt/litepan/strm/日本大姐姐/`，容器内 `/media/strm/日本大姐姐/`。
- 前面有 **Caddy** 反代（响应头 `Via: 1.1 Caddy`）；Caddy 会自己做 gzip，所以**压缩后的响应体里搜不到注入标记**，
  排查时要么不带 `Accept-Encoding`，要么先解压。
- 升级后旧版本目录可以删：`docker exec jellyfin rm -rf '/config/plugins/Thunder Subtitle_<old>'`。

---

## 9. 测试

```powershell
dotnet test .\tests\Jellyfin.Plugin.ThunderSubtitle.Tests\...\csproj -c Release
```

172 个用例，覆盖：番号提取、编码探测、字幕解析与字符数、语言判定、排序与升级阈值、
AI prompt 与 JSON 解析（含错误回复）、搜索结果 id 编解码、预览缓存过期/容量、注入改写（幂等/basePath/无 body）、
以及 HTTP 层的重试与超时。

**改逻辑时先加断言再改实现**：这些测试是唯一能在不装插件的情况下验证行为的手段。

---

## 10. 已知的坑（都是真踩过的）

1. **`If-None-Match` → 304**：`index.html` 带 ETag，浏览器每次校验都拿 304，注入永远不生效。
   必须同时剥掉请求侧校验头。**这是"服务端明明注入了但浏览器没有"的头号原因。**
2. **`EnableInMainMenu` 会抢走"设置页"**：插件第二个页面不要加这个标记。
3. **`TwoLetterISOLanguageName` 是 `zh-cn`**：严格等于 `zh` 会让搜索结果为空。
4. **id 前缀**：Jellyfin 用 `md5(provider name)_` 前缀，且可能在调用 provider 前就切掉了；
   编解码要能同时吃两种形态。
5. **PowerShell 5.1 + git**：git 往 stderr 写东西会触发 `$ErrorActionPreference='Stop'` 中断脚本，
   `release.ps1` 里已局部改成 `Continue`。
6. **`git push` 常常 non-fast-forward**：CI 会往 main 提交 manifest。流程是
   `git tag -d v<ver>` → `git pull --rebase origin main` → 重新打 tag → push（`release.ps1` 目前**不会**自动重试，
   这是下一个可以改进的点）。
7. **`tools/SubtitleProbe --no-build`** 会跑旧二进制，导致"改了没生效"的错觉。
8. **内容测量必须先解析成 cue**：SRT 条目之间没有空行时，按行统计会把时间轴算进正文。
9. **`.strm` 条目查不到**：`TopParentIds` 对 `.strm` 无效，要用 `ParentId + Recursive`，且
   `CollapseBoxSetItems = false`，否则已进合集的影片会"消失"。
10. **绝对不要动 115 同步的字幕**：`<片名>.srt` / `<片名>.zh.srt` 不属于本插件，任何情况下不覆盖不删除。
11. **插件页的 `<style>` 放在 `<head>` 等于没写**：Jellyfin 只搬 `<body>` 子节点进 SPA，
    自定义样式会全部丢失。判断方法：看 `legend`/标题有没有变粗——没变粗就是 CSS 没加载。

---

## 11. 版本时间线（2026-11-02 ~ 11-03）

| 版本 | 内容 |
|---|---|
| 1.0.0.0 ~ 1.2.x | 插件骨架：定时任务、迅雷客户端、编码探测、排序、写盘、配置页、插件仓库与 CI |
| 1.3.x | 按**台词字符数**排序（不再按字节）；双拼写补查；番号校验 |
| 1.4.x | AI 评审（OpenAI 兼容）；Open Code 内置供应商 + `x-opencode-session` |
| 1.5.x | 只保留简/繁中文 + 优先语言 + 升级阈值；超时/并发/预算重构（16 部从 10 分钟降到 25.6 秒）；默认上限 5；手动「搜索字幕」接入 Jellyfin；搜索抗抖动（5 次重试 + 浏览器 UA） |
| 1.6.0.0 | AI 送审范围按**条数并列**扩大（上限 8）；送审台词**去时间轴**；修正 AI 重排后分数错位 |
| 1.7.0.0 | （已被取代）独立的"全文预览"页 |
| 1.7.1.0 | 修 `EnableInMainMenu` 抢走设置页 |
| 1.8.0.0 | 预览改为**插在下载按钮旁**的按钮 + 全文弹窗；删除独立预览页与"预览行数"；AI 评分单独一行 |
| 1.8.1.0 | 修 304 导致注入不生效；脚本增加地址/令牌兜底与补扫 |
| 1.8.2.0 | 修弹窗顶部 `undefined`（字段名对齐 `cueCount`/`contentChars`/`encodingName`） |
| 1.8.3.0 | 文档与文案去掉敏感用词（统一"按番号命名的影片"）；内部类 `JavCode` 更名 `ReleaseCode` |
| **1.8.3.1** | 配置页改为**分组卡片布局**：`fieldset` + `legend` 划出 基本设置 / 目标影视库 / 下载与筛选 / AI 评审 / 预览缓存 / 手动运行 / 关于；修影视库勾选框被方框压住的问题 |
| **1.8.3.2** | 修 1.8.3.1 的样式全部失效：`<style>` 从 `<head>` 移进 `<body>`（Jellyfin 只搬 body 子节点，head 里的 style 会被丢掉），缩进/边框/字重恢复生效 |
| **1.8.3.3** | 按用户偏好取消组内左缩进（`.ts-group-body` padding 归零），只保留分组边框 + `legend` 加粗的层级感 |
| **1.9.0.0** | 新增**实时入库补字幕**：`ItemWatchService` 每 30 秒查一次「最近 6 小时入库且已刮削完成（ProviderIds 非空）」的影片，只对这些片子跑 `SubtitleJob.ProcessItemsAsync`，**不扫全库**。配置项 `RealtimeFill` 默认开。原有 6 小时定时任务保留作兜底。顺带修复 CI 回写 manifest 时的 rebase 冲突（改成在 main 上重新生成） |

---

## 12. 当前状态与下一步

**已完成并验证**：搜索、语言过滤、排序、AI 评审、自动补全/重新择优、手动搜索、预览按钮的注入与弹窗
（服务端注入 + 真实浏览器 DOM 复刻环境均已验证）。

**待用户确认**：1.8.3.1 的配置页**分组卡片布局**在用户真实浏览器里的观感（尤其深色主题下的
`legend` 与边框对比度），以及影视库勾选框是否不再压住库名。
预览按钮的注入已确认可用（重启日志有 `已把预览脚本注入 jellyfin-web` + `预览脚本已被浏览器加载`）。
若预览按钮又不见了：先看日志有没有 `预览脚本已被浏览器加载`——
- 没有 → 注入没到浏览器（检查 Caddy 缓存 / 强刷 / 别的端口）；
- 有 → 脚本跑了但没匹配到行（用 F12 检查 `.subtitleResults` 里 `data-subid` 是否带 `ts1-`）。

**候选项**（用户尚未拍板，不要擅自开工）：

1. `release.ps1` 自动处理 non-fast-forward（drop tag → rebase → re-tag → push）。
2. 繁体 → 简体转换（用户早前提过：若简体不合适而繁体的 AI 分更高，把繁体转成简体写入）。
3. 更多 AI 供应商（目前只做 Open Code 内置 + 自定义，用户说以后再加）。
4. 预览弹窗里直接显示 AI 逐条评语、或按条数/字符数排序切换。

---

## 13. 接手时请遵守的约定

1. **一次只改一件事，每改完发一个版本**（用户明确要求，不要攒成一个版本）。
2. **不要动已经正常的东西**：搜索/语言/下载/写盘逻辑目前是稳定态。
3. 发版前必须 `dotnet test` 全绿；改前端（`configPage.html` / `clientScript.js`）先用 `node --check` 验语法。
4. 任何"服务端看起来对了"的结论，都要在服务器上用真实接口再验一遍再汇报。
5. **密钥不入库**：API Key 只放 Jellyfin 插件配置（`AiApiKey`）或临时命令行变量里。
6. 用户偏好：中文回答；结论要有证据（贴接口返回 / 日志 / 截图），不要只说"应该可以"。
