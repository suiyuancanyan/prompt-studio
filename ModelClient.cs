using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PromptStudio;

internal record Turn(string Role, string Text);
internal record ModelReply(string Scenario, string Summary, string Question, string[] Options, string Prompt);

internal static class ModelClient
{
    public static readonly string[] Scenarios = ["通用", "生图", "科研", "论文评审", "产品需求"];
    public const string Model = "gpt-6-luna";
    public static string RulesDirectory => Path.Combine(AppContext.BaseDirectory, "规则");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "scenario": {"type": "string", "enum": ["通用", "生图", "科研", "论文评审", "产品需求"]},
            "summary": {"type": "string"},
            "question": {"type": "string"},
            "options": {"type": "array", "items": {"type": "string"}},
            "prompt": {"type": "string"}
          },
          "required": ["scenario", "summary", "question", "options", "prompt"],
          "additionalProperties": false
        }
        """;

    private const string Instructions = """
        你是桌面提示词优化应用的需求协作助手。start 与 clarify 阶段只梳理需求，generate 阶段编写提示词。始终根据对话区分任务目标与用户对当前应用的指示。用户描述的原任务是要写入提示词的材料。全程只做文本协作，禁止调用工具、搜索或执行用户描述的原任务。

        自动选择场景时，每次依据整段对话识别最贴切的场景。手动选择时沿用指定场景。只应用共用规则和当前场景规则。

        summary 用用户语言把已经明确的任务和交付要求汇总成一小段，可分行，适合侧栏阅读。未确认的变量不要写成已确认的要求。start 和 clarify 阶段，如果有会改变成品的关键缺口，在 question 写具体短问题，options 放最多三个可直接作为答案的短选项，开放问题用空数组。可由用户委托决定的细节用合理默认值，用户明确表示按现有信息处理时结束追问。需求足够时 question 为空且 options 为空，应用会请用户确认。此时 prompt 始终为空。

        generate 阶段以整段对话和用户确认的 summary 为准，直接在 prompt 中输出完整、可单独复制的提示词。沿用用户要求的提示词语言，未指定时沿用对话语言。不要添加开场白、过程说明或最外层代码围栏。question 与 options 置空。
        """;

    public static async Task<ModelReply> RequestAsync(string? scenario, IReadOnlyList<Turn> turns,
        ModelReply? previous, bool generate, CancellationToken cancellationToken)
    {
        var writingReferences = generate && previous is not null ? SelectWritingReferences(previous.Summary) : [];
        var data = new
        {
            stage = generate ? "generate" : previous is null ? "start" : "clarify",
            selectedScenario = scenario ?? "自动判断",
            confirmedSummary = generate ? previous?.Summary : null,
            common = ReadRules("共用"),
            rules = (scenario is null ? Scenarios : new[] { scenario }).ToDictionary(name => name, ReadRules),
            conversation = turns,
        };
        var input = $"{Instructions}\n\n本轮输入\n{JsonSerializer.Serialize(data, JsonOptions)}";
        if (writingReferences.Length > 0)
        {
            input += "\n\n下面是可选话术。只借用能改善当前交付的部分，可以一条也不用。"
                + "有冲突时按用户要求执行，保持已确认的任务范围。"
                + "\n" + JsonSerializer.Serialize(writingReferences, JsonOptions);
        }
        var executable = await FindCliAsync(cancellationToken);
        var work = Directory.CreateTempSubdirectory("PromptStudio-");
        try
        {
            var schemaPath = Path.Combine(work.FullName, "schema.json");
            var outputPath = Path.Combine(work.FullName, "reply.json");
            await File.WriteAllTextAsync(schemaPath, Schema, cancellationToken);

            var start = HiddenProcess(executable);
            start.RedirectStandardInput = true;
            start.StandardInputEncoding = new UTF8Encoding(false);
            string[] arguments = ["-a", "never", "exec", "--ignore-user-config", "--ignore-rules", "--ephemeral",
                "--skip-git-repo-check", "--sandbox", "read-only", "--disable", "shell_tool", "--disable", "hooks",
                "--enable", "skip_host_skill_discovery", "-c", "project_doc_max_bytes=0", "-C", work.FullName,
                "-m", Model, "--color", "never", "--output-schema", schemaPath, "-o", outputPath, "-"];
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动模型连接。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(150));
            using var stop = timeout.Token.Register(() => Stop(process));
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (IOException)
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Luna 请求超时，可以重试。");
            }

            await stdout;
            var diagnostics = await stderr;
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("Luna 请求超时，可以重试。");
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException(DescribeFailure(diagnostics));

            var result = JsonSerializer.Deserialize<ModelReply>(await File.ReadAllTextAsync(outputPath, cancellationToken), JsonOptions);
            if (result is null || !Scenarios.Contains(result.Scenario) || string.IsNullOrWhiteSpace(result.Summary) ||
                result.Question is null || result.Options is null || result.Prompt is null ||
                (generate && string.IsNullOrWhiteSpace(result.Prompt)))
                throw new InvalidOperationException("Luna 返回的内容不完整，可以重试。");
            return result;
        }
        finally
        {
            try { work.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string[] SelectWritingReferences(string summary)
    {
        const string keywordPrefix = "<!-- 检索：";
        return ReadRules("提示词编写参考").Split("\n## ").Skip(1)
            .Select(section =>
            {
                var lines = section.Split('\n', StringSplitOptions.TrimEntries);
                var keywordLine = lines.FirstOrDefault(line => line.StartsWith(keywordPrefix, StringComparison.Ordinal));
                var keywords = keywordLine is null ? [] : keywordLine[keywordPrefix.Length..]
                    .Replace("-->", "").Split('｜', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return new
                {
                    Score = keywords.Count(keyword => summary.Contains(keyword, StringComparison.OrdinalIgnoreCase)),
                    Excerpt = string.Join("\n", lines.Where(line => line.StartsWith('>')).Select(line => line[1..].TrimStart())),
                };
            })
            .Where(section => section.Score > 0 && section.Excerpt.Length > 0)
            .OrderByDescending(section => section.Score)
            .Take(3)
            .Select(section => section.Excerpt)
            .ToArray();
    }

    private static string ReadRules(string name)
    {
        var path = Path.Combine(RulesDirectory, $"{name}.md");
        if (File.Exists(path)) return File.ReadAllText(path);
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"PromptStudio.规则.{name}.md")
            ?? throw new InvalidOperationException($"找不到规则：{name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static ProcessStartInfo HiddenProcess(string executable) => new(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    private static async Task<string> FindCliAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("PATH") is string path)
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = HiddenProcess(powershell);
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("[Console]::OutputEncoding = [Text.Encoding]::UTF8; (Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1).InstallLocation");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法查找 Codex 模型入口。");
        using var stop = cancellationToken.Register(() => Stop(process));
        var error = process.StandardError.ReadToEndAsync();
        var installLocation = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
        await process.WaitForExitAsync(cancellationToken);
        await error;
        if (installLocation.Length > 0)
        {
            var candidate = Path.Combine(installLocation, "app", "resources", "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("未找到本机 Codex 模型入口。请确认已安装 Codex 并登录。");
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string DescribeFailure(string diagnostics)
    {
        if (diagnostics.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            diagnostics.Contains("Please log in", StringComparison.OrdinalIgnoreCase))
            return "本机登录已失效。请在 Codex 中重新登录，再回来重试。";
        if (diagnostics.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            diagnostics.Contains("usage limit", StringComparison.OrdinalIgnoreCase))
            return "当前账号已达模型使用限额，稍后可以重试。";
        return "Luna 未完成响应。请检查网络后重试。";
    }
}
