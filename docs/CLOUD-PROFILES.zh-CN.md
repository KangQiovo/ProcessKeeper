# 云端白名单配置共创

[English](CLOUD-PROFILES.md) | **简体中文** | [繁體中文](CLOUD-PROFILES.zh-TW.md)

Process Keeper 首次启动始终使用**一套空的本地白名单配置**。云端配置只是可选参考，不会后台获取、自动导入或安装缺失软件。

## 获取配置

进入**设置 → 备份 → 白名单配置 → 云端白名单配置**，点击**从 GitHub 加载**，选择文件并检查 JSON。兼容界面另有**预览云端配置**按钮。点击**导入并应用**并确认后，将创建新配置并切换当前白名单，不会覆盖已有配置或关闭程序。重名时自动添加数字后缀。

云端导入也计入**最多五套本地配置**，首次空配置同样占用一套。达到五套后，请先删除不用的非当前配置。保存时检查文件修订版本，遇到并发修改会拒绝覆盖。完整设置备份包含云端导入的配置，仅白名单导出只包含当前规则。

应用从公开仓库 [KangQiovo/ProcessKeeper 的 community/profiles](https://github.com/KangQiovo/ProcessKeeper/tree/main/community/profiles) 读取普通 JSON 文件；官方应用中的仓库固定。请求无需登录，不会上传本地规则或设置。网络异常、限流、格式错误及选择后文件发生变化均会如实显示。上限为 100 个 JSON 配置、200 个目录项、每个文件 1 MiB。

可选的 [`kangqi-default.json`](https://github.com/KangQiovo/ProcessKeeper/blob/main/community/profiles/kangqi-default.json) 保留 Clash Verge、Codex、QQ、Steam、火绒、UU 远程与 TranslucentTB 及匹配的子进程，**不是正式包的本地默认配置**。其他电脑没有的软件只会显示未匹配。按进程名称匹配的范围可能较广，导入前请核对规则和白名单生效页面。

## 提交共创配置

1. Fork 本项目，在 **`community/profiles/`** 下直接新增一个不重名的 `.json`，例如 `my-work-tools.json`。文件名使用简短英文字母、数字、`-` 或 `_`；文件名会成为配置显示名称，扩展名前不超过 80 个字符。
2. 使用下方便携格式。建议从仅白名单导出中保留受支持的 `Application` 身份或准确的 `ProcessName` 文件名，删除本机路径、账号信息及无关设置。
3. 在应用配置编辑器里检查格式。说明各规则保护的软件、是否需要匹配子进程，以及实际验证过的版本和系统。
4. 提交 Pull Request，包含配置文件及上述说明。所有用户都可提案；维护者审核合并后，应用才能获取。提交提案不等于取得主仓库写入权限。

```json
{
  "Version": 1,
  "Rules": [
    {
      "Id": "example-auradio",
      "Name": "Auradio",
      "Kind": "ProcessName",
      "Value": "Auradio.exe",
      "Enabled": true,
      "IncludeDescendants": false
    }
  ]
}
```

每条规则必须包含全部六个字段，`Id` 在文件内唯一；`Enabled` 和 `IncludeDescendants` 必须为 JSON 布尔值。根节点仅接受 `Version` 和 `Rules`。未知或重复字段、无效类型、非法 UTF-8、超过 1,000 条规则或超过 1 MiB 均会被拒绝。

云端只接受 `Application` 的便携 `known:` 身份、有效 `package:` 包族身份，以及 `ProcessName` 的完整可执行文件名。未知 `known:` 身份只会显示未匹配。不接受通配符、`ExecutablePath`、`Directory`、`Application` 的 `path:`、符号链接、子目录或嵌入命令。含本机规则的完整备份不应提交到此公开目录。更多隐私和审核要求见[贡献指南](https://github.com/KangQiovo/ProcessKeeper/blob/main/CONTRIBUTING.zh-CN.md)。

共创规则是匹配条件，不是可执行脚本，也不代表推荐安装对应软件。名称歧义或过宽的子进程匹配可能保留额外进程；发现问题欢迎提交 Issue 或修正 Pull Request。
