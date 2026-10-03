# 已签名建议规则库更新

[English](CATALOG-UPDATES.md) | [简体中文](CATALOG-UPDATES.zh-CN.md) | [繁體中文](CATALOG-UPDATES.zh-TW.md)

本功能更新的是**注册名称的建议标签**，不是病毒查杀特征库。用户关注名单和历史公开报告仍分别说明；命中名称不代表当前文件有毒。不会上传软件清单、下载执行代码或自动卸载软件。

## 使用方式

在**应用卸载 → 规则库**查看版本、来源核对日期及官方仓库。点击**检查规则更新**只下载和核验，不改变当前标签。点击**更新规则库**并确认后，才保存新库并刷新标签。当前版本不自动联网检查，也不自动应用。已在确认或执行中的批量卸载不会被规则更新改变。

内置 68 条产品规则、117 个别名可离线使用。进入卸载页时加载已验证的新缓存。损坏缓存会如实报错并保留可信规则；以后更高修订的有效更新能够修复缓存。网络、HTTP、超时、签名、格式和写入错误均不会伪装成更新成功。

## 信任与边界

固定请求 `https://raw.githubusercontent.com/KangQiovo/ProcessKeeper/main/catalog/v1/catalog.json` 和同目录 `catalog.sig`；不跟随重定向，不关闭系统 HTTPS 证书验证，设置导入不能修改地址或公钥。

JSON 最多 512 KiB、500 条规则、共 2,000 个别名，每条最多20个别名，每个别名160字符，每种语言的理由最多1,500字符。流式下载时即限制大小。所有对象拒绝重复、未知及缺失字段，严格验证 UTF-8、深度、日期、名称和证据链接。匹配类型固定 `exact-name-v1`，不能下载正则、脚本、命令或新匹配引擎。

签名为 RSA-3072 / SHA-256 / PKCS#1 v1.5，验证原始 JSON 字节。公钥内置于客户端，目录内 `public-key.xml` 仅为公开参考。.NET Framework 使用临时 CSP 密钥上下文，不持久写入用户或系统密钥容器。实现依据 [.NET RSA 验签接口](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rsa.verifydata)。

缓存使用一个 `verified.catalog` 文件：8字节 `PKCAT001`、小端32位 JSON 长度、原始JSON和384字节签名。同目录临时文件写入并刷新后原子替换；独占写锁覆盖重读最新缓存、修订比较及替换。并发写入会及时失败，失败保留旧文件。

只应用更高修订号；相同修订内容不同及旧版本均拒绝。纠错、撤回也必须发布**更高修订**，可在新修订中恢复旧内容。最低门槛为内置修订及当前已信任状态。管理员删除/恢复全部本地状态、更换应用或发布者私钥泄露不属于绝对防回滚保证；无法联网也不能证明服务器没有新版本。

## 维护格式与发布

顶层字段固定：`schema=1`、`repository=KangQiovo/ProcessKeeper`、递增整数 `revision`、点分数字 `version`、UTC `publishedUtc`、日期 `checkedOn`、`matching=exact-name-v1`、`rules`。

每条包含 `id`、`basis`（`watchlist` 或 `published-report`）、`names`、含 `en/zh-Hans/zh-Hant` 的 `reason`、`sourceUrl` 和 `sourcePublishedOn`。关注名单来源字段为空；公开报告需受允许的一手 HTTPS 来源，不编造未知日期。新增证据域名需发版审核客户端。名称为规范化完整名，只沿用有限数字版本/架构后缀匹配。

1. 核对一手资料，区分历史渠道行为与当前文件结论，更新三语理由。
2. 增加修订号并更新版本、日期；撤回规则应在更高修订中移除。保留上一版公开文件以比较。
3. 构建 Core，私钥离线保存在仓库外，以 PowerShell 7 运行：

```powershell
./scripts/Sign-Catalog.ps1 -PrivateKeyPath 'E:\offline-keys\catalog-signing.private.xml' -PreviousCatalogPath 'E:\review\previous-catalog.json'
```

4. 跑双框架 `ProcessKeeper.CatalogUpdate.Tests`，检查实际 Git blob 验签、内容差异和来源，一次提交同时发布 JSON 与签名。下载时若恰逢更新导致二者不一致，客户端会拒绝，稍后重试即可。
5. 签名后不要格式化JSON。文件采用 UTF-8无BOM、LF，`.gitattributes` 保留签名字节。不要提交私钥，需离线备份；密钥遗失或轮换须发布含新公钥的审核版应用。

首版签名库已包含在源码中。只有发布到固定公开仓库后端点才可访问；此前 HTTP 404 会如实显示。
