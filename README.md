# 提示词工作台 · Prompt Studio

一个 Windows 桌面应用，帮你把任务描述或已有草稿整理成可以直接复制的提示词。

- 通过对话梳理需求，确认后生成提示词。
- 支持自动识别场景，也可手动选择通用、生图、科研、论文评审或产品需求。
- 内置 280 条 AiShort 提示词，可按标题、标签和关键词搜索，并填入对话继续修改。

## 在 Windows 上运行

安装 .NET 8 SDK，并登录能够使用 `gpt-6-luna` 的 Codex。应用会查找 Windows 版 Codex 自带的命令行程序，也支持通过 `PATH` 查找 `codex.exe`。

```powershell
git clone https://github.com/suiyuancanyan/prompt-studio.git
cd prompt-studio
dotnet run --project PromptStudio.csproj
```

打开后输入任务描述，点击「开始梳理」。需求确认后，点击「确认并生成」，再复制结果。也可以从右上角打开「提示词库」选用模板。

## 许可

本项目采用 [MIT 许可证](LICENSE)。内置模板来自 [AiShort](https://github.com/rockbenben/ChatGPT-Shortcut)，其版权和许可说明见 [AiShort-LICENSE.txt](模板/AiShort-LICENSE.txt)。
