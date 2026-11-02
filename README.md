# Jellyfin Thunder Subtitle

Jellyfin 12.2 插件：为 AV 影视库中**完全没有字幕**的影片，按番号从迅雷字幕 API 自动下载高质量外挂字幕，
写成 `<片名>.chi.srt` / `<片名>.chi.ass` 放在视频（`.strm`）旁边，并刷新条目让 Jellyfin 识别。

面向 Jellyfin **12.2.0**（`targetAbi = 12.2.0.0`，`net10.0`）。

## 功能

| 计划任务 | Key | 默认触发 | 作用 |
|---|---|---|---|
| 迅雷字幕 · 自动补全 | `ThunderSubtitleFill` | 每 6 小时 | 扫描所选影视库里没有任何字幕的影片，下载最合适的字幕 |
| 迅雷字幕 · 重新择优 | `ThunderSubtitleRepick` | 每周日 04:00 | 对已由本插件写入的 `.chi.*` 重新比较，更好的才替换 |

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
5. 去重：`md5(文本归一化 CRLF→LF)`，同一字幕的换行差异版本只留一份。
6. 排序：**先排除番号不符的候选**（搜索 `NIMA-86` 时会出现 `NIMA-014` 之类的别的片子，
   它体积更大，纯按体积排序会把别人的字幕写进来），再按「名字里带本番号 > 无番号的哈希名」、
   **机翻标记数升序**、**体积降序**排序（接口的 `score` / `fingerprintf_score` 恒为 0，不可用）。
7. 写入 `<片名>.chi.<ext>`（先写临时文件再原子替换），并删掉旧的其它扩展名 `.chi.*`，避免同一影片出现两份字幕。
   已有 `.chi.*` 时只有真正变好才替换：机翻标记变少直接换；否则要求体积比现有大 `max(1 KB, 5%)`，
   避免「多了一个注释、多条换行」的重复上传来回覆盖。
8. 刷新条目：`MetadataRefreshMode.ValidationOnly` + `ImageRefreshMode.None`。
   重新探测本地文件、挂上外挂字幕，但**不会**触发库设置里的 `SubtitleDownloadLanguages`，因此不会产生 `.chi.0.srt`。
9. 在插件数据目录记录来源（`<data>/thunder-subtitle/provenance.json`），供「重新择优」判断现有字幕是机翻还是人工。

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
| 目标影视库 | 空 | 必填，一般只勾 AV 库 |
| 单次上限 | 20 | 每次运行最多处理多少部（只统计真正发起搜索的） |
| 最多候选数 | 25 | 每部影片最多下载多少条候选比较体积 |
| 请求间隔 | 500 ms | 两次搜索之间的等待 |
| 允许覆盖已有插件字幕 | 关 | 打开后补全任务也会升级已存在的 `.chi.*` |
| 日志级别 | 常规 | 错误 / 警告 / 常规 / 调试（逐条候选） |

配置页还有「立即补全」「立即重新择优」「打开计划任务」，直接调用 Jellyfin 自带的 `/ScheduledTasks` API。

**首次上线后建议停掉旧的 python 定时器**，避免两套逻辑重复下载：

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
│   ├── JavCode.cs                # 番号提取
│   ├── ThunderClient.cs          # 迅雷 API + CDN 下载（域名白名单、体积上限）
│   ├── SubtitleText.cs           # 编码探测、格式嗅探、去重哈希
│   ├── SubtitleRanker.cs         # 机翻标记 + 体积排序、升级判断
│   ├── Sidecar.cs                # 命名规则、原子写入、旧扩展名清理
│   ├── ProvenanceStore.cs        # 字幕来源台账
│   ├── PluginLog.cs              # 日志级别过滤 + 北京时间戳
│   └── SubtitleJob.cs            # 任务主体
├── Tasks/
│   ├── ThunderFillTask.cs
│   └── ThunderRepickTask.cs
└── Web/configPage.html           # 嵌入式配置页
```

## 已知限制

- 只处理 `Movie` 类型的条目，番号必须能从文件名识别。
- 迅雷侧没有候选时不会回退到 SubtitleCat；旧脚本可以继续作为 fallback。
- 排序假设「体积越大字幕越完整」。极少数情况下小体积人工字幕会被大体积机翻盖过，
  机翻一般在文件名里带 `[MT]` / `AI翻译` / `whisper` 等标记，会被优先排除。
- 番号不符的候选一律丢弃：如果某部影片的候选全部属于别的番号，会记录「全部属于其它番号」并跳过，
  宁可不写，也不把别的片子的字幕写到这部片上。
- 刷新使用 `ValidationOnly`，不会重新拉取远程元数据；`.strm` 指向的远程视频本身不会被探测。
